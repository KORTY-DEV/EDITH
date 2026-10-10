using System.Diagnostics;

namespace Edith.Audio.Wake;

public enum WakeEngineKind
{
    None,
    Vosk,
    Custom,
    CustomV3,
    Hybrid
}


public sealed class WakeRuntimeOptions
{
    public string ConfigPath { get; init; } =
        WakePaths.ResolveConfigFile();

    public string DiagnosticsDirectory { get; init; } =
        WakePaths.DiagnosticsDirectory;

    // Как часто проверять, не появилась/не обновилась ли
    // custom-модель. 0 = не проверять.
    public int PollIntervalMs { get; init; } =
        3000;

    // Значения по умолчанию при отсутствующем/сломанном конфиге.
    // null = боевые пути; переопределяется только тестами.
    public WakeConfig? DefaultConfig { get; init; }

    public Func<long>? ClockMs { get; init; }

    // Если задан — runtime им не владеет (не Dispose).
    public IWakeDiagnostics? Diagnostics { get; init; }
}


// Единая точка wake-подсистемы EDITH.
//
//   - читает config/wake.json и выбирает движок (custom / vosk);
//   - маршрутизирует ОДИН поток микрофона: ни второго capture,
//     ни второго ресемплера;
//   - для custom держит pre-roll и отдаёт Vosk начало произнесения,
//     чтобы "Эдит открой блокнот" одной фразой по-прежнему
//     превращалось в команду;
//   - следит за файлами моделей и автоматически переходит на
//     custom, когда edith.onnx устанавливается;
//   - при сбое custom в работе откатывается на Vosk.
//
// Все публичные методы потокобезопасны. Аудио-методы вызываются
// из одного потока (pump MicrophoneMonitor).
public sealed class WakeRuntime : IDisposable
{
    // 1.5 s хвоста до сработавшего wake — максимум для Vosk.
    private const int MaxPreRollSamples = 24000;

    // Если начало речи неизвестно — берём 1.2 s.
    private const int DefaultPreRollSamples = 19200;

    // VAD подтверждает речь с задержкой ~100-200 ms:
    // берём начало с запасом.
    private const int SpeechStartMarginSamples = 6400;

    // Custom wake принимается, только если VAD видел речь за
    // последние 3 s (окно модели ~2 s + задержка VAD). Так тихий
    // комнатный шум, на котором модель может дать высокий score,
    // не открывает session.
    private const int SpeechGraceSamples = 48000;

    private readonly IWakeUtterance _utterance;
    private readonly WakeConfig _config;
    private readonly WakeRuntimeOptions _options;
    private readonly IWakeDiagnostics _diagnostics;
    private readonly bool _ownsDiagnostics;
    private readonly IReadOnlyList<string> _configWarnings;

    private readonly VoskWakeDetector _vosk;

    private readonly PreRollBuffer _preRoll =
        new(MaxPreRollSamples);

    private readonly float[] _scratch =
        new float[MaxPreRollSamples];

    private readonly object _lifecycle =
        new();

    private readonly object _hybridGate =
        new();

    private volatile IWakeWordDetector? _detector;
    private volatile int _engineKind;
    private volatile string _engineLabel =
        "none";

    private long _speechStart = -1;

    private long _lastSpeechPosition =
        long.MinValue;

    private long _rejectedDetections;
    private long _lastRejectionReportTicks;

    private long _hybridCooldownUntilMs;
    private long _lastHybridAcceptedAtMs = long.MinValue;
    private string _lastWakeSource = "none";
    private long _voskAccepted;
    private long _customAccepted;
    private long _bothObserved;
    private long _duplicateSuppressed;
    private long _weakVoskCustomAccepted;
    private long _customOnlyRejected;
    private long _speechSegment;
    private long _customConfirmationAtMs = long.MinValue;
    private long _customConfirmationSegment = -1;
    private string _lastVoskToken = "";
    private string _lastFusionReason = "none";

    private const long FusionWindowMs = 1000;

    private OnnxWakeDetector? _pendingCustom;

    private Timer? _pollTimer;
    private string? _appliedFingerprint;
    private string? _candidateFingerprint;

    private bool _voskSubscribed;
    private bool _started;
    private bool _disposed;

    public event Action<WakeDetection>? WakeDetected;

    private WakeRuntime(
        IWakeUtterance utterance,
        WakeConfig config,
        IReadOnlyList<string> configWarnings,
        WakeRuntimeOptions options,
        IWakeDiagnostics diagnostics,
        bool ownsDiagnostics)
    {
        _utterance = utterance;
        _config = config;
        _configWarnings = configWarnings;
        _options = options;
        _diagnostics = diagnostics;
        _ownsDiagnostics = ownsDiagnostics;

        _vosk =
            new VoskWakeDetector(
                utterance);
    }


    public WakeEngineKind Engine =>
        (WakeEngineKind)_engineKind;

    // custom / vosk-fallback / forced-vosk / none
    public string EngineLabel =>
        _engineLabel;

    public WakeConfig Config =>
        _config;

    // Vosk-режим ищет wake ПО ТЕКСТУ (в т.ч. в конце фразы после
    // паузы). Custom-режим решает акустической моделью.
    public bool UsesTextWakeHeuristics =>
        Engine is WakeEngineKind.Vosk or WakeEngineKind.Hybrid;

    public WakeDetectorStatus? DetectorStatus =>
        _detector?.Status;

    public WakeDetectorStatus VoskStatus =>
        _vosk.Status;

    public WakeDetectorStatus? CustomStatus =>
        Engine is WakeEngineKind.Custom or WakeEngineKind.CustomV3 or WakeEngineKind.Hybrid
            ? _detector?.Status
            : null;

    public WakeV3Status? V3Status =>
        _detector is OnnxWakeDetector detector ? detector.V3Status : null;

    public string VoskPartial =>
        _utterance is global::Edith.Audio.WakeWordDetector detector
            ? detector.LastPartial
            : "";

    public string VoskFinal =>
        _utterance is global::Edith.Audio.WakeWordDetector detector
            ? detector.LastFinal
            : "";

    public WakeHybridStatus HybridStatus
    {
        get
        {
            WakeDetectorStatus? custom = CustomStatus;
            return new WakeHybridStatus(
                _lastWakeSource,
                Interlocked.Read(ref _voskAccepted),
                Interlocked.Read(ref _customAccepted),
                Interlocked.Read(ref _weakVoskCustomAccepted),
                Interlocked.Read(ref _customOnlyRejected),
                Interlocked.Read(ref _bothObserved),
                Interlocked.Read(ref _duplicateSuppressed),
                custom?.LastScore ?? 0,
                _detector is OnnxWakeDetector detector
                    ? detector.ConsecutiveHits
                    : 0,
                SpeechSeenRecently(),
                _lastVoskToken,
                _lastFusionReason);
        }
    }

    // Сколько срабатываний custom-модели отклонено как "без речи".
    public long RejectedDetections =>
        Interlocked.Read(ref _rejectedDetections);


    // Не бросает: любая проблема с custom = fallback + diagnostics.
    public static WakeRuntime Create(
        IWakeUtterance utterance,
        WakeRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(
            utterance);

        options ??=
            new WakeRuntimeOptions();

        IWakeDiagnostics diagnostics =
            options.Diagnostics ??
            new WakeDiagnostics(
                options.DiagnosticsDirectory,
                options.ClockMs);

        bool owns =
            options.Diagnostics is null;

        WakeConfigLoadResult loaded =
            WakeConfigLoader.Load(
                options.ConfigPath,
                defaults: options.DefaultConfig);

        var runtime =
            new WakeRuntime(
                utterance,
                loaded.Config,
                loaded.Warnings,
                options,
                diagnostics,
                owns);

        runtime.SelectInitialEngine(
            loaded);

        return runtime;
    }


    public void Start()
    {
        lock (_lifecycle)
        {
            if (_disposed ||
                _started)
            {
                return;
            }

            _started = true;

            _detector?.Start();

            if (Engine == WakeEngineKind.Hybrid)
            {
                _vosk.Start();
            }

            if (_config.Engine !=
                    WakeEngineMode.Vosk &&
                _options.PollIntervalMs > 0)
            {
                _pollTimer =
                    new Timer(
                        _ => CheckForModelChange(),
                        null,
                        _options.PollIntervalMs,
                        _options.PollIntervalMs);
            }
        }
    }


    // ---------------------------------------------------------
    // Аудио. Вызывается из pump-потока микрофона.
    // ---------------------------------------------------------

    // WaitingWake: непрерывный поток в wake-детектор.
    //
    // speechActive — текущее состояние Silero VAD (то же самое,
    // что MicrophoneMonitor.IsSpeech). Custom-wake без речи
    // рядом отклоняется. По умолчанию true = "не фильтровать".
    public void AcceptWakeAudio(
        float[] samples,
        int count,
        bool speechActive = true)
    {
        if (_disposed)
            return;

        if (Volatile.Read(ref _pendingCustom)
            is not null)
        {
            ApplyPendingCustom();
        }

        IWakeWordDetector? detector =
            _detector;

        if (detector is null)
            return;

        var span =
            samples.AsSpan(0, count);

        try
        {
            if (Engine is WakeEngineKind.Custom or WakeEngineKind.CustomV3 or WakeEngineKind.Hybrid)
            {
                _preRoll.Write(span);

                if (speechActive)
                {
                    Volatile.Write(
                        ref _lastSpeechPosition,
                        _preRoll.TotalWritten);
                }
            }

            if (Engine == WakeEngineKind.Hybrid)
            {
                // Один и тот же chunk MCHOSE одновременно видят
                // Vosk и custom ONNX.  Второго capture нет.
                _vosk.AcceptAudio(span);
            }

            detector.AcceptAudio(span);
        }
        catch (Exception ex)
        {
            // Исключение здесь (Vosk, обработчик wake) убило бы
            // pump микрофона и весь голосовой ввод. Логируем
            // не чаще раза в 30 секунд и продолжаем.
            ReportAudioPathError(ex);
        }
    }


    private long _lastAudioErrorTicks;

    private void ReportAudioPathError(
        Exception exception)
    {
        long now =
            Environment.TickCount64;

        if (now -
            Volatile.Read(
                ref _lastAudioErrorTicks) <
            30_000 &&
            Volatile.Read(
                ref _lastAudioErrorTicks) != 0)
        {
            return;
        }

        Volatile.Write(
            ref _lastAudioErrorTicks,
            now);

        _diagnostics.ReportError(
            "audio-path",
            exception.ToString());
    }


    // WakeActive: wake уже сработал, идёт транскрипция
    // произнесения (один и тот же Vosk-поток в обоих режимах).
    public void AcceptActiveAudio(
        float[] samples,
        int count)
    {
        if (_disposed)
            return;

        _utterance.AcceptAudio(
            samples.AsSpan(0, count));
    }


    // Вызывается VAD-ом, когда начинается речь в WaitingWake.
    public void NotifySpeechStarted()
    {
        Interlocked.Increment(ref _speechSegment);

        Volatile.Write(
            ref _speechStart,
            _preRoll.TotalWritten);
    }


    public string FinishUtterance()
    {
        return
            _utterance.FinishUtterance();
    }


    // Граница session / после wake: забывает накопленное аудио,
    // hit-счётчики и pre-roll. Cooldown сохраняется.
    public void Reset()
    {
        if (_disposed)
            return;

        _detector?.Reset();

        if (Engine == WakeEngineKind.Hybrid)
        {
            _vosk.Reset();
        }

        if (Engine != WakeEngineKind.Vosk)
        {
            _utterance.Reset();
        }

        _preRoll.Clear();

        Volatile.Write(
            ref _speechStart,
            -1);

        Volatile.Write(
            ref _lastSpeechPosition,
            long.MinValue);
    }


    public bool TryExtractCommand(
        string phrase,
        out string command)
    {
        if (Engine is WakeEngineKind.Custom or WakeEngineKind.CustomV3 ||
            (Engine == WakeEngineKind.Hybrid &&
             string.Equals(_lastWakeSource, "custom", StringComparison.Ordinal)))
        {
            // Wake подтверждён моделью. Пустая команда = "просто Эдит".
            command =
                WakePhraseMatcher
                    .ExtractCommandAfterConfirmedWake(
                        phrase);

            return true;
        }

        return WakePhraseMatcher
            .TryExtractCommand(
                phrase,
                out command);
    }


    public void Dispose()
    {
        Timer? timer;
        OnnxWakeDetector? pending;

        lock (_lifecycle)
        {
            if (_disposed)
                return;

            _disposed = true;

            timer = _pollTimer;
            _pollTimer = null;
        }

        try
        {
            timer?.Dispose();
        }
        catch
        {
        }

        pending =
            Interlocked.Exchange(
                ref _pendingCustom,
                null);

        pending?.Dispose();

        IWakeWordDetector? detector =
            _detector;

        _detector = null;

        if (detector is OnnxWakeDetector custom)
        {
            custom.WakeDetected -= OnCustomWake;
            custom.Faulted -= OnCustomFaulted;
        }

        detector?.Dispose();

        _vosk.WakeDetected -= OnVoskWake;
        _vosk.Dispose();

        _utterance.Dispose();

        if (_ownsDiagnostics)
        {
            _diagnostics.Dispose();
        }
    }


    // ---------------------------------------------------------
    // Выбор движка
    // ---------------------------------------------------------

    private void SelectInitialEngine(
        WakeConfigLoadResult loaded)
    {
        WakeEngineResolution resolution;

        try
        {
            resolution =
                WakeEngineSelector.Resolve(
                    _config,
                    _diagnostics,
                    _options.ClockMs);
        }
        catch (Exception ex)
        {
            // Resolve не должен бросать, но wake-подсистема
            // не имеет права уронить запуск EDITH.
            resolution =
                new WakeEngineResolution(
                    _config.FallbackToVosk
                        ? WakeEngineChoice.VoskFallback
                        : WakeEngineChoice.None,
                    _config.Engine,
                    "engine selection crashed",
                    null,
                    null,
                    ex.ToString());
        }

        var notes =
            new List<string>(
                _configWarnings.Select(
                    w => "config: " + w));

        if (!loaded.FileFound)
        {
            notes.Add(
                "config: wake.json not found, defaults used");
        }

        if (resolution.Error is not null)
        {
            notes.Add(
                "error: " +
                resolution.Error);
        }

        switch (resolution.Choice)
        {
            case WakeEngineChoice.Custom:

                AttachCustom(
                    resolution.Detector!);

                break;

            case WakeEngineChoice.CustomV3:

                AttachCustom(resolution.Detector!, WakeEngineKind.CustomV3);

                break;

            case WakeEngineChoice.Hybrid:

                AttachHybrid(
                    resolution.Detector!);

                break;


            case WakeEngineChoice.VoskFallback:
            case WakeEngineChoice.HybridVoskFallback:
            case WakeEngineChoice.ForcedVosk:

                AttachVosk();

                break;


            default:

                _detector = null;
                _engineKind = (int)WakeEngineKind.None;

                break;
        }

        _engineLabel =
            resolution.Label;

        _diagnostics.ReportEngine(
            resolution.Label,
            _config.Engine
                .ToString()
                .ToLowerInvariant(),
            resolution.Reason,
            notes);

        _diagnostics.ReportModelStatus(
            WakeEngineSelector
                .DescribeModelStatus(
                    resolution));

        _diagnostics.ReportError(
            "startup",
            resolution.Error ??
            "none");

        _appliedFingerprint =
            Fingerprint();
    }


    private void AttachCustom(
        OnnxWakeDetector detector,
        WakeEngineKind kind = WakeEngineKind.Custom)
    {
        _utterance.WakeEventsEnabled =
            false;

        _utterance.Reset();

        _vosk.Stop();

        detector.WakeDetected +=
            OnCustomWake;

        detector.Faulted +=
            OnCustomFaulted;

        _detector =
            detector;

        _engineKind = (int)kind;
    }


    private void AttachVosk()
    {
        if (!_voskSubscribed)
        {
            _vosk.WakeDetected +=
                OnVoskWake;

            _voskSubscribed = true;
        }

        _utterance.WakeEventsEnabled =
            true;

        _detector =
            _vosk;

        _engineKind =
            (int)WakeEngineKind.Vosk;
    }


    // Called only at VAD speech-end. A weak token therefore cannot fire from
    // an intermediate partial such as "едет дождь".
    public bool TryAcceptWeakVoskFinal(
        string phrase,
        out WakeDetection detection)
    {
        detection = default;

        if (Engine != WakeEngineKind.Hybrid ||
            !WakePhraseMatcher.IsIsolatedWeakWake(phrase))
        {
            SetFusionRejection("weak-vosk-not-single-whitelist-token", "");
            return false;
        }

        string token = phrase.Trim().ToLowerInvariant().Replace('ё', 'е');
        long now = Environment.TickCount64;
        long segment = Interlocked.Read(ref _speechSegment);
        bool accepted = false;

        lock (_hybridGate)
        {
            _lastVoskToken = token;

            if (now < _hybridCooldownUntilMs)
            {
                _lastFusionReason = "weak-vosk-rejected-cooldown";
            }
            else if (_customConfirmationSegment != segment ||
                now - _customConfirmationAtMs > FusionWindowMs)
            {
                _lastFusionReason = "weak-vosk-rejected-no-custom-same-segment";
            }
            else
            {
                _hybridCooldownUntilMs = now + _config.CooldownMs;
                _lastHybridAcceptedAtMs = now;
                _lastWakeSource = "weak-vosk+custom";
                _lastFusionReason = "accepted-weak-vosk+custom-same-segment";
                Interlocked.Increment(ref _weakVoskCustomAccepted);

                detection = new WakeDetection(
                    VoskWakeDetector.EngineName,
                    CustomStatus?.LastScore ?? 0,
                    token,
                    DateTimeOffset.Now)
                { Source = "weak-vosk+custom" };
                accepted = true;
            }
        }

        _diagnostics.ReportHybrid(HybridStatus);
        if (!accepted)
            return false;

        _diagnostics.ReportDetection(detection);
        return true;
    }


    private void AttachHybrid(
        OnnxWakeDetector detector)
    {
        AttachVosk();

        detector.WakeDetected += OnCustomWake;
        detector.Faulted += OnCustomFaulted;

        // _detector остаётся custom для score/status, Vosk хранится
        // отдельно и получает тот же audio chunk в AcceptWakeAudio.
        _detector = detector;
        _engineKind = (int)WakeEngineKind.Hybrid;
    }


    // Только из аудио-потока (или до Start).
    private void SwapToVosk(
        string reason)
    {
        IWakeWordDetector? old =
            _detector;

        AttachVosk();

        if (old is OnnxWakeDetector custom)
        {
            custom.WakeDetected -= OnCustomWake;
            custom.Faulted -= OnCustomFaulted;

            custom.Dispose();
        }

        _utterance.Reset();
        _preRoll.Clear();

        _vosk.Start();

        _engineLabel = _config.Engine == WakeEngineMode.Hybrid
            ? "hybrid-vosk-fallback"
            : _config.Engine == WakeEngineMode.CustomV3
                ? "custom-v3-vosk-fallback"
                : "vosk-fallback";

        _diagnostics.ReportEngine(
            _engineLabel,
            _config.Engine
                .ToString()
                .ToLowerInvariant(),
            reason,
            []);
    }


    private void ApplyPendingCustom()
    {
        OnnxWakeDetector? next =
            Interlocked.Exchange(
                ref _pendingCustom,
                null);

        if (next is null)
            return;

        IWakeWordDetector? old =
            _detector;

        if (old is OnnxWakeDetector previous)
        {
            previous.WakeDetected -= OnCustomWake;
            previous.Faulted -= OnCustomFaulted;

            previous.Dispose();
        }
        else if (old is VoskWakeDetector)
        {
            _vosk.Stop();
        }

        if (_config.Engine == WakeEngineMode.Hybrid)
        {
            AttachHybrid(next);
            _vosk.Start();
        }
        else
        {
            AttachCustom(next, _config.Engine == WakeEngineMode.CustomV3
                ? WakeEngineKind.CustomV3
                : WakeEngineKind.Custom);
        }

        _preRoll.Clear();

        Volatile.Write(
            ref _speechStart,
            -1);

        next.Start();

        _engineLabel =
            _config.Engine == WakeEngineMode.Hybrid
                ? "hybrid"
                : _config.Engine == WakeEngineMode.CustomV3
                    ? "custom-v3"
                    : "custom";

        _diagnostics.ReportEngine(
            _engineLabel,
            _config.Engine
                .ToString()
                .ToLowerInvariant(),
            "custom wake model installed; switched " +
            "without restart (baseline score " +
            next.BaselineScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
            ")",
            []);

        _diagnostics.ReportModelStatus(
            "engine=custom" +
            Environment.NewLine +
            "baseline-score=" +
            next.BaselineScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
            Environment.NewLine +
            next.ModelReport.Describe());

        _diagnostics.ReportError(
            "hot-reload",
            "none");
    }


    // ---------------------------------------------------------
    // Автопереход после установки модели
    // ---------------------------------------------------------

    // Публичный для тестов; в работе вызывается таймером.
    public void CheckForModelChange()
    {
        try
        {
            if (_disposed ||
                _config.Engine ==
                WakeEngineMode.Vosk)
            {
                return;
            }

            string fingerprint =
                Fingerprint();

            if (fingerprint ==
                _appliedFingerprint)
            {
                _candidateFingerprint = null;

                return;
            }

            // Файл должен быть неизменным на двух проверках
            // подряд: так мы не читаем недокопированную модель.
            if (fingerprint !=
                _candidateFingerprint)
            {
                _candidateFingerprint =
                    fingerprint;

                return;
            }

            // Что бы ни случилось дальше, эти же файлы
            // больше не пробуем.
            _appliedFingerprint =
                fingerprint;

            _candidateFingerprint =
                null;

            WakeModelSetReport report = _config.Engine == WakeEngineMode.CustomV3
                ? WakeEngineSelector.TryCreateCustomV3(_config, _diagnostics, _options.ClockMs,
                    out OnnxWakeDetector? detector, out string? error)
                : WakeEngineSelector.TryCreateCustom(_config, _diagnostics, _options.ClockMs,
                    out detector, out error);

            if (detector is null)
            {
                // Файлов может просто ещё не быть целиком.
                if (report.Classifier.Exists)
                {
                    _diagnostics.ReportError(
                        "hot-reload",
                        error ??
                        "custom model rejected");

                    _diagnostics.ReportModelStatus(
                        "engine=" +
                        _engineLabel +
                        Environment.NewLine +
                        "hot-reload-rejected=" +
                        (error ?? "") +
                        Environment.NewLine +
                        report.Describe());
                }

                return;
            }

            OnnxWakeDetector? replaced =
                Interlocked.Exchange(
                    ref _pendingCustom,
                    detector);

            replaced?.Dispose();

            if (_disposed)
            {
                Interlocked.Exchange(
                        ref _pendingCustom,
                        null)
                    ?.Dispose();
            }
        }
        catch (Exception ex)
        {
            _diagnostics.ReportError(
                "hot-reload",
                ex.ToString());
        }
    }


    private string Fingerprint()
    {
        return string.Join(
            "|",
            Describe(_config.MelModel),
            Describe(_config.EmbeddingModel),
            Describe(_config.Engine == WakeEngineMode.CustomV3 ? _config.CustomV3Model : _config.CustomModel),
            Describe(_config.Engine == WakeEngineMode.CustomV3 ? _config.VerifierModel : "not-used"));

        static string Describe(
            string path)
        {
            try
            {
                var info =
                    new FileInfo(path);

                return info.Exists
                    ? info.Length +
                      ":" +
                      info.LastWriteTimeUtc.Ticks
                    : "missing";
            }
            catch
            {
                return "unreadable";
            }
        }
    }


    // ---------------------------------------------------------
    // События детекторов (аудио-поток)
    // ---------------------------------------------------------

    private void OnVoskWake(
        WakeDetection detection)
    {
        if (_disposed)
            return;

        PublishWake(
            detection with { Source = "strong-vosk" },
            "strong-vosk");
    }


    private void OnCustomWake(
        WakeDetection detection)
    {
        if (_disposed)
            return;

        if (RequireRecentSpeech && !SpeechSeenRecently())
        {
            RejectWithoutSpeech(detection);

            return;
        }

        if (Engine == WakeEngineKind.Hybrid)
        {
            RegisterCustomConfirmation(detection);
            return;
        }

        // Do not reset the active Vosk command recognizer when the other
        // hybrid engine has already opened this same session.
        if (Engine == WakeEngineKind.Hybrid &&
            HybridCooldownActive())
        {
            PublishWake(
                detection with { Source = "custom" },
                "custom");
            return;
        }

        FeedPreRollToUtterance();

        PublishWake(
            detection with { Source = "custom" },
            "custom");
    }


    // Hybrid arbitration lives here rather than in App, so all consumers
    // (including WakeCheck) get the same single-session contract.
    private void PublishWake(
        WakeDetection detection,
        string source)
    {
        if (Engine != WakeEngineKind.Hybrid)
        {
            _diagnostics.ReportDetection(detection);
            WakeDetected?.Invoke(detection);
            return;
        }

        bool accepted;
        long now = Environment.TickCount64;

        lock (_hybridGate)
        {
            accepted = now >= _hybridCooldownUntilMs;

            if (accepted)
            {
                _hybridCooldownUntilMs = now + _config.CooldownMs;
                _lastHybridAcceptedAtMs = now;
                _lastWakeSource = source;

                if (source is "vosk" or "strong-vosk")
                    Interlocked.Increment(ref _voskAccepted);
                else
                    Interlocked.Increment(ref _customAccepted);
            }
            else
            {
                Interlocked.Increment(ref _duplicateSuppressed);

                // Two engines detecting the same utterance is observable in
                // diagnostics, while the already-opened session remains sole.
                if (now - _lastHybridAcceptedAtMs <= _config.CooldownMs &&
                    !string.Equals(_lastWakeSource, source, StringComparison.Ordinal))
                {
                    _lastWakeSource = "both";
                    Interlocked.Increment(ref _bothObserved);
                }
            }
        }

        _diagnostics.ReportHybrid(HybridStatus);

        if (!accepted)
            return;

        _diagnostics.ReportDetection(detection);
        WakeDetected?.Invoke(detection);
    }


    private void RegisterCustomConfirmation(
        WakeDetection detection)
    {
        long now = Environment.TickCount64;
        long segment = Interlocked.Read(ref _speechSegment);

        lock (_hybridGate)
        {
            _customConfirmationAtMs = now;
            _customConfirmationSegment = segment;

            // A strong Vosk event already opened the current session. This is
            // an observed pair, not an independent custom rejection.
            if (now < _hybridCooldownUntilMs &&
                _lastWakeSource == "strong-vosk")
            {
                _lastWakeSource = "strong-vosk+custom-observed";
                _lastFusionReason = "strong-vosk-already-accepted";
            }
            else
            {
                _lastWakeSource = "custom-only-rejected";
                _lastFusionReason = "custom-only-never-opens-session";
                Interlocked.Increment(ref _customOnlyRejected);
            }
        }

        _diagnostics.ReportHybrid(HybridStatus);
    }


    private bool HybridCooldownActive()
    {
        lock (_hybridGate)
        {
            return Environment.TickCount64 < _hybridCooldownUntilMs;
        }
    }


    private void SetFusionRejection(
        string reason,
        string token)
    {
        if (Engine != WakeEngineKind.Hybrid)
            return;

        lock (_hybridGate)
        {
            _lastFusionReason = reason;
            _lastVoskToken = token;
        }

        _diagnostics.ReportHybrid(HybridStatus);
    }


    /// <summary>
    /// Custom wakes need recent VAD speech (noise guard). The app turns this off when GigaAM
    /// re-reads every acoustic wake anyway: VAD often misses a short, quick "Эдит".
    /// </summary>
    public bool RequireRecentSpeech { get; set; } = true;

    private bool SpeechSeenRecently()
    {
        long last =
            Volatile.Read(
                ref _lastSpeechPosition);

        return
            last != long.MinValue &&
            _preRoll.TotalWritten - last <=
            SpeechGraceSamples;
    }


    private void RejectWithoutSpeech(
        WakeDetection detection)
    {
        Interlocked.Increment(
            ref _rejectedDetections);

        long now =
            Environment.TickCount64;

        long last =
            Volatile.Read(
                ref _lastRejectionReportTicks);

        if (last != 0 &&
            now - last < 10_000)
        {
            return;
        }

        Volatile.Write(
            ref _lastRejectionReportTicks,
            now);

        _diagnostics.ReportError(
            "wake-rejected",
            "custom detector fired (score " +
            detection.Score.ToString(
                "F3",
                System.Globalization.CultureInfo
                    .InvariantCulture) +
            ") but the VAD saw no speech in the last 3 s; " +
                    "treated as a false accept on room noise");

        if (Engine == WakeEngineKind.Hybrid)
        {
            _diagnostics.ReportHybrid(HybridStatus);
        }
    }


    private void OnCustomFaulted(
        Exception exception)
    {
        _diagnostics.ReportError(
            "custom-detector-fault",
            exception.ToString());

        if (_config.FallbackToVosk ||
            _config.Engine ==
            WakeEngineMode.Auto)
        {
            SwapToVosk(
                "custom detector faulted: " +
                exception.Message);

            return;
        }

        IWakeWordDetector? old =
            _detector;

        _detector = null;

        _engineKind =
            (int)WakeEngineKind.None;

        _engineLabel =
            "none";

        if (old is OnnxWakeDetector custom)
        {
            custom.WakeDetected -= OnCustomWake;
            custom.Faulted -= OnCustomFaulted;

            custom.Dispose();
        }
    }


    // Отдаёт Vosk начало произнесения, которое уже прошло
    // через custom-детектор: "эдит открой ..." одной фразой.
    private void FeedPreRollToUtterance()
    {
        long now =
            _preRoll.TotalWritten;

        long speechStart =
            Volatile.Read(
                ref _speechStart);

        long start =
            speechStart >= 0
                ? speechStart -
                  SpeechStartMarginSamples
                : now -
                  DefaultPreRollSamples;

        start =
            Math.Max(
                start,
                now -
                MaxPreRollSamples);

        int count =
            _preRoll.CopySince(
                start,
                _scratch);

        _utterance.Reset();

        if (count > 0)
        {
            _utterance.AcceptAudio(
                _scratch.AsSpan(0, count));
        }
    }
}
