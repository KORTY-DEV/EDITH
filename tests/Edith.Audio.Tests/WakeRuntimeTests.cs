using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class WakeRuntimeTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables)
            d.Dispose();

        _dir.Dispose();
    }


    private string Models =>
        Path.Combine(_dir.Path, "models");

    private string Diagnostics =>
        Path.Combine(_dir.Path, "diag");


    private string WriteConfig(
        string engine = "auto",
        bool fallback = true,
        int cooldownMs = 1500,
        string? extra = null)
    {
        string path =
            _dir.File("wake.json");

        string J(string file) =>
            Path.Combine(Models, file).Replace("\\", "\\\\");

        File.WriteAllText(
            path,
            $$"""
            {
              "engine": "{{engine}}",
              "customModel": "{{J("edith.onnx")}}",
              "melModel": "{{J("melspectrogram.onnx")}}",
              "embeddingModel": "{{J("embedding_model.onnx")}}",
              "threshold": 0.7,
              "minConsecutiveHits": 2,
              "cooldownMs": {{cooldownMs}},
              "fallbackToVosk": {{fallback.ToString().ToLowerInvariant()}}
              {{extra}}
            }
            """);

        return path;
    }


    private (WakeRuntime Runtime,
             FakeUtterance Utterance,
             WakeDiagnostics Diag)
        Create(
            string configPath,
            int pollMs = 0)
    {
        var utterance = new FakeUtterance();

        var diag =
            new WakeDiagnostics(Diagnostics);

        var runtime =
            WakeRuntime.Create(
                utterance,
                new WakeRuntimeOptions
                {
                    ConfigPath = configPath,

                    // Missing/broken config falls back to defaults whose model paths point
                    // at the production C:\EDITH\models; keep the tests off the live machine.
                    DefaultConfig =
                        new WakeConfig
                        {
                            CustomModel = Path.Combine(Models, "edith.onnx"),
                            MelModel = Path.Combine(Models, "melspectrogram.onnx"),
                            EmbeddingModel = Path.Combine(Models, "embedding_model.onnx")
                        },

                    Diagnostics = diag,
                    PollIntervalMs = pollMs
                });

        _disposables.Add(runtime);
        _disposables.Add(diag);

        return (runtime, utterance, diag);
    }


    private string Read(
        WakeDiagnostics diag,
        string file)
    {
        diag.Flush();

        string path =
            Path.Combine(Diagnostics, file);

        return File.Exists(path)
            ? File.ReadAllText(path)
            : "";
    }


    private static void Feed(
        WakeRuntime runtime,
        float[] audio,
        int block = 1280)
    {
        var buffer = new float[block];

        for (int i = 0; i < audio.Length; i += block)
        {
            int n =
                Math.Min(block, audio.Length - i);

            Array.Copy(audio, i, buffer, 0, n);

            runtime.AcceptWakeAudio(buffer, n);
        }
    }


    // ---------------------------------------------------------
    // Выбор движка
    // ---------------------------------------------------------

    [Fact]
    public void Auto_WithoutCustomModel_UsesVoskFallback()
    {
        var (runtime, utterance, diag) =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("vosk-fallback", runtime.EngineLabel);
        Assert.True(runtime.UsesTextWakeHeuristics);
        Assert.True(utterance.WakeEventsEnabled);

        string engine =
            Read(diag, WakeDiagnosticFiles.Engine);

        Assert.Equal(
            "vosk-fallback",
            engine.Split('\n')[0].Trim());

        // Отсутствие модели — штатное состояние, не ошибка.
        Assert.StartsWith(
            "none",
            Read(diag, WakeDiagnosticFiles.Error)
                .Split('\n')[2].Trim());
    }


    [Fact]
    public void MissingConfigFile_UsesDefaultsAndVosk()
    {
        var (runtime, _, _) =
            Create(_dir.File("does-not-exist.json"));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
    }


    [Fact]
    public void Auto_WithValidModels_UsesCustom()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, diag) =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);
        Assert.Equal("custom", runtime.EngineLabel);
        Assert.False(runtime.UsesTextWakeHeuristics);

        // Vosk остаётся только транскрибером.
        Assert.False(utterance.WakeEventsEnabled);

        Assert.Equal(
            "custom",
            Read(diag, WakeDiagnosticFiles.Engine)
                .Split('\n')[0].Trim());

        string status =
            Read(diag, WakeDiagnosticFiles.ModelStatus);

        Assert.Contains("onnx::Flatten_0", status);
        Assert.Contains("[1, 16, 96]", status);
        Assert.Contains("load-ms=", status);
        Assert.Contains("all-valid=True", status);
    }


    [Fact]
    public void ForcedVosk_IgnoresValidCustomModels()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, diag) =
            Create(WriteConfig(engine: "vosk"));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("forced-vosk", runtime.EngineLabel);
        Assert.True(utterance.WakeEventsEnabled);

        Assert.Contains(
            "not evaluated",
            Read(diag, WakeDiagnosticFiles.ModelStatus));
    }


    [Fact]
    public void Auto_WithInvalidOnnx_FallsBackToVosk_AndReportsError()
    {
        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllText(
            Path.Combine(Models, "edith.onnx"),
            "garbage");

        var (runtime, _, diag) =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("vosk-fallback", runtime.EngineLabel);

        Assert.Contains(
            "wake_model",
            Read(diag, WakeDiagnosticFiles.Error));

        Assert.Contains(
            "valid=False",
            Read(diag, WakeDiagnosticFiles.ModelStatus));
    }


    [Fact]
    public void Custom_Missing_FallsBackToVosk_WhenAllowed()
    {
        var (runtime, _, diag) =
            Create(WriteConfig(engine: "custom", fallback: true));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("vosk-fallback", runtime.EngineLabel);

        // Явный engine=custom без модели — это ошибка конфигурации.
        Assert.Contains(
            "file not found",
            Read(diag, WakeDiagnosticFiles.Error));
    }


    [Fact]
    public void Custom_Missing_WithoutFallback_ReportsNone_AndDoesNotThrow()
    {
        var (runtime, _, _) =
            Create(WriteConfig(engine: "custom", fallback: false));

        Assert.Equal(WakeEngineKind.None, runtime.Engine);
        Assert.Equal("none", runtime.EngineLabel);

        // Аудио просто игнорируется, EDITH не падает.
        Feed(runtime, Sig.Noise(1));
        runtime.Reset();
        runtime.Start();
    }


    [Fact]
    public void Custom_InvalidModel_WithoutFallback_ReportsNone()
    {
        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier(embeddingDim: 64));

        var (runtime, _, diag) =
            Create(WriteConfig(engine: "custom", fallback: false));

        Assert.Equal(WakeEngineKind.None, runtime.Engine);

        Assert.Contains(
            "96",
            Read(diag, WakeDiagnosticFiles.Error));
    }


    [Fact]
    public void ModelThatFiresOnSilence_IsNotUsed()
    {
        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier(pivot: -10_000f));

        var (runtime, _, diag) =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        Assert.Contains(
            "room tone",
            Read(diag, WakeDiagnosticFiles.Error));
    }


    [Fact]
    public void BrokenConfigFile_DoesNotPreventStartup()
    {
        string path = _dir.File("wake.json");

        File.WriteAllText(path, "{ definitely not json");

        var (runtime, _, diag) = Create(path);

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        Assert.Contains(
            "not valid JSON",
            Read(diag, WakeDiagnosticFiles.Engine));
    }


    // ---------------------------------------------------------
    // Vosk-режим: поведение прежнее
    // ---------------------------------------------------------

    [Fact]
    public void Vosk_WakeText_RaisesWakeDetected()
    {
        var (runtime, utterance, diag) =
            Create(WriteConfig());

        var detections = new List<WakeDetection>();
        runtime.WakeDetected += detections.Add;

        runtime.Start();

        utterance.RaiseWake("эдит");

        var d = Assert.Single(detections);

        Assert.Equal("vosk", d.Engine);
        Assert.Equal("эдит", d.Text);

        Assert.Contains(
            "engine=vosk",
            Read(diag, WakeDiagnosticFiles.LastDetection));
    }


    [Fact]
    public void Vosk_AudioGoesToTheSameRecognizer_InBothPaths()
    {
        var (runtime, utterance, _) =
            Create(WriteConfig());

        runtime.Start();

        var block = new float[1280];

        runtime.AcceptWakeAudio(block, 1280);
        runtime.AcceptActiveAudio(block, 500);

        Assert.Equal([1280, 500], utterance.Accepted);
    }


    [Fact]
    public void Vosk_AudioBeforeStart_IsIgnored()
    {
        var (runtime, utterance, _) =
            Create(WriteConfig());

        runtime.AcceptWakeAudio(new float[1280], 1280);

        Assert.Empty(utterance.Accepted);
    }


    [Fact]
    public void Vosk_CommandExtraction_UsesTextMatcher()
    {
        var (runtime, _, _) =
            Create(WriteConfig());

        Assert.True(
            runtime.TryExtractCommand(
                "эдит открой блокнот",
                out string command));

        Assert.Equal("открой блокнот", command);

        // Без текстового wake — не команда (как раньше).
        Assert.False(
            runtime.TryExtractCommand(
                "открой блокнот",
                out _));
    }


    [Fact]
    public void Reset_InVoskMode_ResetsRecognizer()
    {
        var (runtime, utterance, _) =
            Create(WriteConfig());

        runtime.Reset();

        Assert.True(utterance.Resets >= 1);
    }


    // ---------------------------------------------------------
    // Custom-режим
    // ---------------------------------------------------------

    private (WakeRuntime, FakeUtterance, WakeDiagnostics)
        CreateCustom()
    {
        OnnxTestModels.WriteValidSet(Models);

        var created =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Custom, created.Runtime.Engine);

        created.Runtime.Start();

        return created;
    }


    [Fact]
    public void CustomV3_WithBothModels_UsesTwoStageEngine()
    {
        OnnxTestModels.WriteValidSet(Models);
        File.Copy(Path.Combine(Models, "edith.onnx"), Path.Combine(Models, "verifier.onnx"));
        string stage1 = Path.Combine(Models, "edith.onnx").Replace("\\", "\\\\");
        string verifier = Path.Combine(Models, "verifier.onnx").Replace("\\", "\\\\");

        var (runtime, utterance, _) = Create(WriteConfig(
            engine: "custom-v3",
            fallback: false,
            extra: ",\n  \"customV3Model\": \"" + stage1 +
                   "\",\n  \"verifierModel\": \"" + verifier +
                   "\",\n  \"verifierThreshold\": 0.75"));

        Assert.Equal(WakeEngineKind.CustomV3, runtime.Engine);
        Assert.Equal("custom-v3", runtime.EngineLabel);
        Assert.False(utterance.WakeEventsEnabled);
        Assert.NotNull(runtime.V3Status);
    }


    // ---------------------------------------------------------
    // Hybrid: one capture stream feeds both engines; central
    // arbitration owns cooldown/dedup, not the App state machine.
    // ---------------------------------------------------------

    [Fact]
    public void Hybrid_WithValidModels_ArmsVoskAndCustomFromOneRuntime()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, _) =
            Create(WriteConfig(engine: "hybrid"));

        Assert.Equal(WakeEngineKind.Hybrid, runtime.Engine);
        Assert.Equal("hybrid", runtime.EngineLabel);
        Assert.True(runtime.UsesTextWakeHeuristics);

        runtime.Start();
        runtime.AcceptWakeAudio(new float[1280], 1280, speechActive: false);

        // Exactly one WakeWordDetector receives the one supplied chunk.
        Assert.Equal(1280, utterance.TotalAcceptedSamples);
        Assert.Equal(WakeDetectorState.Running, runtime.VoskStatus.State);
        Assert.Equal(WakeDetectorState.Running, runtime.CustomStatus?.State);
    }


    [Fact]
    public void Hybrid_StrongVosk_AcceptsImmediately()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, _) =
            Create(WriteConfig(engine: "hybrid"));

        var detections = new List<WakeDetection>();
        runtime.WakeDetected += detections.Add;
        runtime.Start();

        utterance.RaiseWake("эдит");

        WakeDetection detection = Assert.Single(detections);
        Assert.Equal("strong-vosk", detection.Source);
        Assert.Equal("strong-vosk", runtime.HybridStatus.Source);
    }


    [Fact]
    public void Hybrid_CustomOnly_IsRecordedButNeverOpensSession()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, _, _) =
            Create(WriteConfig(engine: "hybrid"));

        var detections = new List<WakeDetection>();
        runtime.WakeDetected += detections.Add;
        runtime.Start();

        FeedWithVad(runtime, Sig.Tone(0.8), speechActive: true);

        Assert.Empty(detections);
        Assert.Equal(1, runtime.HybridStatus.CustomOnlyRejected);
        Assert.Equal("custom-only-rejected", runtime.HybridStatus.Source);

        Assert.True(runtime.TryAcceptWeakVoskFinal("едет", out WakeDetection fused));
        Assert.Equal("weak-vosk+custom", fused.Source);
        Assert.Equal(1, runtime.HybridStatus.WeakVoskCustomAccepted);
    }


    [Fact]
    public void Hybrid_SilenceCustomCandidate_IsRejectedByVad()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, _, _) =
            Create(WriteConfig(engine: "hybrid"));

        int wakes = 0;
        runtime.WakeDetected += _ => wakes++;
        runtime.Start();

        FeedWithVad(runtime, Sig.Tone(0.8), speechActive: false);

        Assert.Equal(0, wakes);
        Assert.True(runtime.RejectedDetections > 0);
    }


    [Fact]
    public void Hybrid_WeakMultiWordAndWrongSegment_NeverFuse()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, _, _) =
            Create(WriteConfig(engine: "hybrid"));

        runtime.Start();
        FeedWithVad(runtime, Sig.Tone(0.8), speechActive: true);

        Assert.False(runtime.TryAcceptWeakVoskFinal("едет дождь", out _));
        Assert.Equal("weak-vosk-not-single-whitelist-token", runtime.HybridStatus.Reason);

        runtime.NotifySpeechStarted();
        Assert.False(runtime.TryAcceptWeakVoskFinal("едет", out _));
        Assert.Equal("weak-vosk-rejected-no-custom-same-segment", runtime.HybridStatus.Reason);
    }


    [Fact]
    public void Hybrid_StrongVoskAndCustom_OpenOneSession()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, _) =
            Create(WriteConfig(engine: "hybrid"));

        int wakes = 0;
        runtime.WakeDetected += _ => wakes++;
        runtime.Start();

        utterance.RaiseWake("эдит");
        FeedWithVad(runtime, Sig.Tone(0.8), speechActive: true);

        Assert.Equal(1, wakes);
        Assert.Equal("strong-vosk+custom-observed", runtime.HybridStatus.Source);
        Assert.Equal(0, runtime.HybridStatus.CustomOnlyRejected);
    }


    [Fact]
    public void Hybrid_TenLifecycleResets_RearmsBothEngines()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, utterance, _) =
            Create(WriteConfig(engine: "hybrid", cooldownMs: 0));

        int wakes = 0;
        runtime.WakeDetected += _ => wakes++;
        runtime.Start();

        for (int i = 0; i < 10; i++)
        {
            utterance.RaiseWake("эдит");
            runtime.Reset();
        }

        Assert.Equal(10, wakes);
        Assert.Equal(WakeDetectorState.Running, runtime.VoskStatus.State);
        Assert.Equal(WakeDetectorState.Running, runtime.CustomStatus?.State);
    }


    [Fact]
    public void Custom_WakeWord_RaisesEvent_AndFeedsPreRollToTranscriber()
    {
        var (runtime, utterance, diag) =
            CreateCustom();

        var detections = new List<WakeDetection>();
        runtime.WakeDetected += detections.Add;

        Feed(
            runtime,
            Sig.Concat(
                Sig.Noise(1.0),
                Sig.Tone(0.8),
                Sig.Noise(0.1, seed: 5)));

        var d = Assert.Single(detections);

        Assert.Equal("custom", d.Engine);
        Assert.True(d.Score >= 0.7f);

        // До wake Vosk аудио не получал (экономия CPU),
        // а в момент wake ему отдали начало произнесения.
        Assert.True(utterance.TotalAcceptedSamples > 0);
        Assert.True(utterance.TotalAcceptedSamples <= 24000);

        Assert.Contains(
            "engine=custom",
            Read(diag, WakeDiagnosticFiles.LastDetection));
    }


    private static void FeedWithVad(
        WakeRuntime runtime,
        float[] audio,
        bool speechActive)
    {
        var buffer = new float[1280];

        for (int i = 0; i < audio.Length; i += 1280)
        {
            int n =
                Math.Min(1280, audio.Length - i);

            Array.Copy(audio, i, buffer, 0, n);

            runtime.AcceptWakeAudio(buffer, n, speechActive);
        }
    }


    [Fact]
    public void Custom_DetectionWithoutAnySpeechActivity_IsRejectedAsRoomNoise()
    {
        var (runtime, utterance, diag) =
            CreateCustom();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        // Модель "сработала", но VAD не видел речи ни разу:
        // это ложное срабатывание на шумовом полу.
        FeedWithVad(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8), Sig.Noise(0.5, seed: 4)),
            speechActive: false);

        Assert.Equal(0, fired);
        Assert.Equal(1, runtime.RejectedDetections);
        Assert.Empty(utterance.Accepted);

        Assert.Contains(
            "wake-rejected",
            Read(diag, WakeDiagnosticFiles.Error));

        Assert.Contains(
            "false accept",
            Read(diag, WakeDiagnosticFiles.Error));
    }


    [Fact]
    public void Custom_DetectionWithRecentSpeech_IsAccepted()
    {
        var (runtime, _, _) =
            CreateCustom();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        FeedWithVad(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8), Sig.Noise(0.5, seed: 4)),
            speechActive: true);

        Assert.Equal(1, fired);
        Assert.Equal(0, runtime.RejectedDetections);
    }


    [Fact]
    public void Custom_SpeechActivity_ExpiresAfterThreeSeconds()
    {
        var (runtime, _, _) =
            CreateCustom();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        // Речь была, но давно (10 s тишины перед "wake").
        FeedWithVad(runtime, Sig.Noise(0.5), speechActive: true);

        FeedWithVad(
            runtime,
            Sig.Concat(Sig.Noise(10, seed: 6), Sig.Tone(0.8)),
            speechActive: false);

        Assert.Equal(0, fired);
        Assert.Equal(1, runtime.RejectedDetections);
    }


    [Fact]
    public void Custom_Reset_ForgetsEarlierSpeechActivity()
    {
        var (runtime, _, _) =
            CreateCustom();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        FeedWithVad(runtime, Sig.Noise(1), speechActive: true);

        runtime.Reset();

        FeedWithVad(
            runtime,
            Sig.Concat(Sig.Noise(0.5, seed: 8), Sig.Tone(0.8)),
            speechActive: false);

        Assert.Equal(0, fired);
    }


    [Fact]
    public void Vosk_Mode_IsNotGatedByVad()
    {
        var (runtime, utterance, _) =
            Create(WriteConfig());

        runtime.Start();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        runtime.AcceptWakeAudio(new float[1280], 1280, speechActive: false);

        utterance.RaiseWake("эдит");

        Assert.Equal(1, fired);
    }


    [Fact]
    public void Custom_SilenceNeverReachesTranscriber()
    {
        var (runtime, utterance, _) =
            CreateCustom();

        Feed(runtime, Sig.Noise(5));

        Assert.Empty(utterance.Accepted);
    }


    [Fact]
    public void Custom_PreRoll_StartsAtSpeechStart_NotAtOldAudio()
    {
        var (runtime, utterance, _) =
            CreateCustom();

        // 5 s тишины, затем речь. Vosk должен получить только
        // произнесение (с запасом), а не всю историю.
        Feed(runtime, Sig.Noise(5));

        runtime.NotifySpeechStarted();

        Feed(runtime, Sig.Concat(Sig.Tone(0.8), Sig.Noise(0.1)));

        // 0.8 s речи + 0.4 s запаса + хвост: заметно меньше 1.5 s.
        Assert.InRange(
            utterance.TotalAcceptedSamples,
            9000,
            21000);
    }


    [Fact]
    public void Custom_Reset_ForgetsPreRoll_AndEnablesRedetection()
    {
        var (runtime, utterance, _) =
            CreateCustom();

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        float[] word =
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8));

        Feed(runtime, word);

        Assert.Equal(1, fired);

        runtime.Reset();

        utterance.Accepted.Clear();

        Feed(runtime, Sig.Noise(3, seed: 7));

        // Старое слово не воскресло.
        Assert.Equal(1, fired);
        Assert.Empty(utterance.Accepted);
    }


    [Fact]
    public void Custom_CommandExtraction_TrustsTheModel()
    {
        var (runtime, _, _) =
            CreateCustom();

        Assert.False(runtime.UsesTextWakeHeuristics);

        Assert.True(
            runtime.TryExtractCommand("эдит открой блокнот", out var a));
        Assert.Equal("открой блокнот", a);

        // Vosk ошибся в самом слове — команда всё равно есть.
        Assert.True(
            runtime.TryExtractCommand("идет открой блокнот", out var b));
        Assert.Equal("открой блокнот", b);

        // Только wake -> пустая команда, но true (wake был).
        Assert.True(runtime.TryExtractCommand("эдит", out var c));
        Assert.Equal("", c);

        Assert.True(runtime.TryExtractCommand("", out var e));
        Assert.Equal("", e);
    }


    [Fact]
    public void Custom_FaultInWork_FallsBackToVosk_WithoutRestart()
    {
        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier(unbounded: true));

        var (runtime, utterance, diag) =
            Create(WriteConfig());

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);

        runtime.Start();

        Feed(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(1.5)));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("vosk-fallback", runtime.EngineLabel);
        Assert.True(utterance.WakeEventsEnabled);

        Assert.Contains(
            "custom-detector-fault",
            Read(diag, WakeDiagnosticFiles.Error));

        // И Vosk wake реально работает после отката.
        var detections = new List<WakeDetection>();
        runtime.WakeDetected += detections.Add;

        utterance.RaiseWake("эдит");

        Assert.Equal("vosk", Assert.Single(detections).Engine);
    }


    [Fact]
    public void Custom_FaultInWork_WithFallbackDisabled_GoesQuiet()
    {
        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier(unbounded: true));

        var (runtime, _, _) =
            Create(WriteConfig(engine: "custom", fallback: false));

        runtime.Start();

        Feed(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(1.5)));

        Assert.Equal(WakeEngineKind.None, runtime.Engine);

        Feed(runtime, Sig.Noise(1));
    }


    [Fact]
    public void ExceptionsFromWakeHandlers_DoNotEscapeIntoTheAudioPump()
    {
        var (runtime, _, diag) =
            CreateCustom();

        runtime.WakeDetected +=
            _ => throw new IOException("disk on fire");

        Feed(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8)));

        Assert.Contains(
            "audio-path",
            Read(diag, WakeDiagnosticFiles.Error));
    }


    // ---------------------------------------------------------
    // Автопереход после установки модели
    // ---------------------------------------------------------

    [Fact]
    public void InstallingModel_SwitchesVoskToCustom_WithoutRestart()
    {
        var (runtime, utterance, diag) =
            Create(WriteConfig());

        runtime.Start();

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        // Пока ничего не изменилось — ничего не происходит.
        runtime.CheckForModelChange();
        runtime.CheckForModelChange();
        Feed(runtime, Sig.Noise(0.5));
        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        // Пользователь запустил Install-WakeModel.ps1.
        OnnxTestModels.WriteValidSet(Models);

        // Первая проверка только запоминает "кандидата"
        // (файл мог быть недокопирован), вторая — применяет.
        runtime.CheckForModelChange();
        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        runtime.CheckForModelChange();

        // Переключение происходит на границе аудио (WaitingWake).
        Feed(runtime, Sig.Noise(0.2));

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);
        Assert.Equal("custom", runtime.EngineLabel);
        Assert.False(utterance.WakeEventsEnabled);

        Assert.Equal(
            "custom",
            Read(diag, WakeDiagnosticFiles.Engine)
                .Split('\n')[0].Trim());

        // И новый детектор действительно работает.
        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        Feed(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8)));

        Assert.Equal(1, fired);
    }


    [Fact]
    public void InstallingBrokenModel_KeepsVosk_AndDoesNotRetryUntilFilesChange()
    {
        var (runtime, _, diag) =
            Create(WriteConfig());

        runtime.Start();

        OnnxTestModels.WriteValidSet(Models);

        File.WriteAllText(
            Path.Combine(Models, "edith.onnx"),
            "half-written garbage");

        runtime.CheckForModelChange();
        runtime.CheckForModelChange();
        Feed(runtime, Sig.Noise(0.2));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        Assert.Contains(
            "hot-reload",
            Read(diag, WakeDiagnosticFiles.Error));

        // Починили файл — теперь подхватывается.
        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier());

        // Гарантированно другой mtime/размер.
        File.SetLastWriteTimeUtc(
            Path.Combine(Models, "edith.onnx"),
            DateTime.UtcNow.AddMinutes(1));

        runtime.CheckForModelChange();
        runtime.CheckForModelChange();
        Feed(runtime, Sig.Noise(0.2));

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);
    }


    [Fact]
    public void ForcedVosk_NeverHotSwapsToCustom()
    {
        var (runtime, _, _) =
            Create(WriteConfig(engine: "vosk"), pollMs: 0);

        runtime.Start();

        OnnxTestModels.WriteValidSet(Models);

        runtime.CheckForModelChange();
        runtime.CheckForModelChange();
        Feed(runtime, Sig.Noise(0.2));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);
        Assert.Equal("forced-vosk", runtime.EngineLabel);
    }


    [Fact]
    public void ReplacingCustomModel_WithNewValidModel_HotSwapsDetector()
    {
        OnnxTestModels.WriteValidSet(Models);

        var (runtime, _, _) =
            Create(WriteConfig());

        runtime.Start();

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);

        File.WriteAllBytes(
            Path.Combine(Models, "edith.onnx"),
            OnnxTestModels.Classifier());

        File.SetLastWriteTimeUtc(
            Path.Combine(Models, "edith.onnx"),
            DateTime.UtcNow.AddMinutes(2));

        runtime.CheckForModelChange();
        runtime.CheckForModelChange();

        Feed(runtime, Sig.Noise(0.2));

        Assert.Equal(WakeEngineKind.Custom, runtime.Engine);

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        Feed(
            runtime,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8)));

        Assert.Equal(1, fired);
    }


    // ---------------------------------------------------------
    // Жизненный цикл
    // ---------------------------------------------------------

    [Fact]
    public void Dispose_IsIdempotent_DisposesTranscriber_AndStopsAcceptingAudio()
    {
        OnnxTestModels.WriteValidSet(Models);

        var utterance = new FakeUtterance();

        var runtime =
            WakeRuntime.Create(
                utterance,
                new WakeRuntimeOptions
                {
                    ConfigPath = WriteConfig(),
                    DiagnosticsDirectory = Diagnostics,
                    PollIntervalMs = 0
                });

        runtime.Start();

        runtime.Dispose();
        runtime.Dispose();

        Assert.Equal(1, utterance.Disposed);

        Feed(runtime, Sig.Noise(1));
        runtime.Reset();
        runtime.CheckForModelChange();

        Assert.Empty(utterance.Accepted);
    }


    [Fact]
    public void StartTwice_DoesNotCreateSecondPollTimerOrDoubleSubscribe()
    {
        var (runtime, utterance, _) =
            Create(WriteConfig(), pollMs: 60_000);

        int fired = 0;
        runtime.WakeDetected += _ => fired++;

        runtime.Start();
        runtime.Start();

        utterance.RaiseWake("эдит");

        Assert.Equal(1, fired);
    }


    [Fact]
    public void ForcedVosk_NeverTouchesOnnxRuntimeModels()
    {
        // Даже если файлы моделей испорчены — engine=vosk
        // не должен их читать и не должен писать ошибок.
        Directory.CreateDirectory(Models);

        File.WriteAllText(
            Path.Combine(Models, "edith.onnx"),
            "garbage");

        var (runtime, _, diag) =
            Create(WriteConfig(engine: "vosk"));

        Assert.Equal(WakeEngineKind.Vosk, runtime.Engine);

        Assert.StartsWith(
            "none",
            Read(diag, WakeDiagnosticFiles.Error)
                .Split('\n')[2].Trim());
    }
}
