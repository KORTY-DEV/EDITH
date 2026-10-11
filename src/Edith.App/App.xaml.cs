using Edith.Audio;
using Edith.Audio.Wake;
using Edith.Core;
using Edith.Core.Brain;
using Edith.Core.Runtime;
using Edith.Speech;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using Vosk;
using static Edith.App.VoiceCommandText;

namespace Edith.App;

public partial class App : Application
{
    private enum VoiceMode
    {
        WaitingWake,
        WakeActive,

        WaitingCommand,
        CapturingCommand,

        Busy,
        Speaking,

        // Acoustic wake candidate is being checked by GigaAM; audio is held back meanwhile.
        ConfirmingWake,

        InterruptTriggered
    }

    private MicrophoneMonitor? _microphone;

    private Model? _speechModel;

    private WakeRuntime? _wake;
    private ICommandRecognizer? _command;
    private string _sttEngine = "vosk-command";

    // Wake confirmation: the last seconds of audio, re-read by GigaAM when V3 fires.
    private readonly AudioRingBuffer _recentAudio = new(16000 * 8);
    private long _totalSamples;
    private long _speechStartSample = -1;
    private int _utteranceWakeRunning;
    private GigaAmCommandRecognizer? _giga;
    private bool _confirmWake = true;
    private readonly object _confirmGate = new();
    private readonly List<float[]> _confirmBacklog = [];
    private bool _confirmSpeechEnded;

    // "Эдит, открой телеграм" in one breath: the whole phrase, re-read by GigaAM at its end.
    private readonly List<float> _wakePhraseAudio = [];
    private readonly object _wakePhraseGate = new();
    private InterruptRecognizer? _interrupt;

    private MainWindow? _overlay;
    private HybridTtsService? _tts;

    private ResponseEngine _response =
        new();

    private EdithRuntime? _runtime;
    private StatusWindow? _statusWindow;
    private SettingsWindow? _settingsWindow;

    // End-of-phrase decision: Smart Turn model + dangling-word check (see HandleSpeechEnded).
    private static volatile Edith.Audio.Embedding.E5Embedder? s_embedder;
    private SmartTurnDetector? _turnDetector;

    // Optional echo cancellation (config conversation.json "echoCancel"): the barge-in scan reads the cleaned copy.
    private Edith.Audio.Echo.EchoCanceller? _echo;
    private Edith.Audio.Echo.EchoReference? _echoReference;
    private readonly AudioRingBuffer _cleanAudio = new(16000 * 8);

    // Microphone watchdog: no frames for ~12 s (device unplugged, PC slept) or never started -> rebuild it.
    private Timer? _micWatchdog;
    private long _watchdogLastFrames = -1;
    private int _watchdogStale;
    private int _micRestarting;
    private DateTimeOffset _micErrorLoggedAt = DateTimeOffset.MinValue;
    private int _turnHoldVersion;
    private Timer? _turnHoldTimer;
    private string _turnPrefix = "";
    private TrayIcon? _tray;
    private long _healthLastAudioFrames = -1;

    private volatile VoiceMode _voiceMode =
        VoiceMode.WaitingWake;

    private long _sessionCounter;

    private string _lastResponse =
        string.Empty;
    private long _activeSession;

    private CancellationTokenSource?
        _commandTimeout;

    private readonly object _interactionGate = new();

    // Things EDITH says on her own (reminders). Spoken only while she is idle.
    private readonly System.Collections.Concurrent.ConcurrentQueue<(string Text, Edith.Core.Reminders.Reminder? Reminder)> _proactive = new();
    private Timer? _proactiveTimer;

    // Learns facts about the user from the conversation while EDITH is idle.
    private Edith.Core.Memory.MemoryDistiller? _distiller;
    private Edith.Core.Memory.EpisodeSummarizer? _episodes;
    private Timer? _distillerTimer;
    private PulseService? _pulseService;
    private LibrarianService? _librarian;
    private AnswerWindow? _answerWindow;

    private AnswerWindow Answers()
    {
        if (_answerWindow is null)
        {
            _answerWindow = new AnswerWindow
            {
                HoldSeconds = () => UiModes.Quiet ? 12 : 4,
                StillSpeaking = () => _voiceMode is VoiceMode.Speaking or VoiceMode.Busy,
            };
            UiModes.ShowRequested += () => Dispatcher.BeginInvoke(() => Answers().ShowLast());
            AnswerImages.Arrived += (bytes, caption) => Dispatcher.BeginInvoke(() => Answers().ShowImage(bytes, caption));
            UiModes.HideRequested += () => Dispatcher.BeginInvoke(() => _answerWindow?.HideNow());
        }

        return _answerWindow;
    }

    /// <summary>Ctrl+Alt+T: the last answer on the desktop, or away if it is already there.</summary>
    private void ShowTextBar()
    {
        AnswerWindow window = Answers();
        if (window.IsVisible)
            window.HideNow();
        else
            window.ShowLast();
    }
    private int _proactiveSpeaking;
    private CancellationTokenSource? _interactionCancellation;
    private long _interactionCancellationSession;
    private ConversationSettings _conversationSettings = new();
    private InteractionLatency? _interactionLatency;

    private volatile bool
        _permissionFollowUpActive;

    // true, пока аудио шло мимо wake-пути (session/ответ).
    // При возврате в WaitingWake wake-окно сбрасывается:
    // старые samples не должны склеиваться с новыми.
    private bool _wakePathIdle =
        true;

    private bool _audioStarted;
    private DateTimeOffset? _commandSpeechStartedAt;
    private Stopwatch? _commandSpeechClock;

    private long _audioFramesReceived;
    private long _weakRewakeUntilTicks;
    private const long WeakRewakeWindowMs = 12_000;


    private static void WriteRunState(string state)
    {
        try { File.WriteAllText(@"C:\EDITH\data\run.state", state); } catch { }
    }

    protected override void OnStartup(
        StartupEventArgs e)
    {
        // Software rendering: a transparent window on the hardware path reads the picture back from the video card, and
        // under a game's load that read-back stalls (the orb crawled at 5-10 frames a second). The orb is small and cheap.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);

        // One EDITH at a time: a second start (shortcut clicked again) only opens the panel
        // of the running one. After "restart" the new process waits for the old one to leave.
        if (!AppInstance.TryAcquire(wait: e.Args.Contains("--restart")))
        {
            AppInstance.SignalShow();
            Shutdown();
            return;
        }

        WriteRunState("running");

        AppInstance.ListenForSignals(
            show: () => Dispatcher.BeginInvoke(ToggleStatusPanel),
            exit: () => Dispatcher.BeginInvoke(() => Shutdown()));

        // Crashes anywhere end up in the journal, not only in a lost console.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            EdithEvents.Publish(EventKinds.Error, "Необработанное исключение: " + (args.ExceptionObject as Exception)?.Message,
                data: new Dictionary<string, string> { ["exception"] = args.ExceptionObject?.ToString() ?? "" });
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            EdithEvents.Publish(EventKinds.Error, "Фоновая задача упала: " + args.Exception.InnerException?.Message,
                data: new Dictionary<string, string> { ["exception"] = args.Exception.ToString() });
            args.SetObserved();
        };
        DispatcherUnhandledException += (_, args) =>
        {
            EdithEvents.Publish(EventKinds.Error, "Ошибка интерфейса: " + args.Exception.Message,
                data: new Dictionary<string, string> { ["exception"] = args.Exception.ToString() });
            args.Handled = true;
        };

        // The core goes first: everything below may already publish events.
        _runtime = EdithRuntime.Start(
            CreateHealthChecks(),
            () => GameMode.Monitor.Evaluate(LoadBrainSettings().Gaming).Active);
        _response = new ResponseEngine(
            activitySource: _runtime.RecentActivity,
            shutdown: _runtime.Shutdown,
            apps: _runtime.Apps,
            reminders: _runtime.Reminders,
            documents: _runtime.Documents,
            browser: _runtime.Browser,
            files: _runtime.Files,
            sorter: _runtime.Sorter,
            screen: _runtime.Screen);
        BrainService.Shared.ResourcePlanSource = () => _runtime?.Resources.Current;
        BrainToolRouter.BrowserProvider = () => _runtime?.Playwright;

        // Meaning search (notes, memory): the model loads in the background; until then, words only.
        Edith.Core.Memory.EmbeddingService.Provider = () => s_embedder;
        _ = Task.Run(() => s_embedder = Edith.Audio.Embedding.E5Embedder.TryCreate(@"C:\EDITH\models\embedding"));
        // The warm-up uses the real persona, so its cached prompt prefix serves the first question.
        BrainService.Shared.DefaultPersonaSource = _response.PersonaPromptForBrain();

        _overlay =
            new MainWindow();

        MainWindow =
            _overlay;

        _overlay.InitializeBackground();
        _overlay.ManualActivationRequested += BeginManualActivation;
        _overlay.ManualDeactivationRequested += EndManualActivation;
        _overlay.StatusPanelRequested += ToggleStatusPanel;
        _overlay.TextBarRequested += ShowTextBar;
        Edith.Speech.WavPlayer.LevelChanged += level => _overlay?.SetOutputLevel(level);
        _ = Answers(); // created hidden now: pictures from tools can arrive before the first answer

        _tray = new TrayIcon(
            talk: BeginManualActivation,
            statusPanel: ShowStatusPanel,
            settings: ShowSettings,
            restart: Restart,
            exit: Shutdown);
        _tray.AddToggle("Библиотекарь (Obsidian)", () => LibrarianSwitch.On, LibrarianSwitch.Set);
        _overlay.AssistantStateChanged += state => Dispatcher.BeginInvoke(() => _tray?.SetState(state));

        _tts = new HybridTtsService(
            TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json"));
        _ = Task.Run(async () =>
        {
            await _tts.WarmUpAsync();
            // After a change of voice or speed the fixed phrases are recorded again, quietly.
            try { await _tts.PrerenderPhrasesAsync(); await _tts.RenderVoiceSamplesAsync(); } catch { }
        });

        _tts.Diagnostic += diagnostic => _interactionLatency?.Mark(
            $"tts-{diagnostic.Phase}",
            $"provider={diagnostic.Provider}; chars={diagnostic.Characters}; words={diagnostic.Words}; elapsed-ms={diagnostic.ElapsedMilliseconds:F0}; synthesis-ms={diagnostic.SynthesisMilliseconds?.ToString("F0") ?? "n/a"}; playback-ms={diagnostic.PlaybackMilliseconds?.ToString("F0") ?? "n/a"}");

        _conversationSettings =
            ConversationSettingsLoader.Load(
                @"C:\EDITH\config\conversation.json");

        VoiceDiagnostics.WriteRaw("tts-voice.txt", _tts.SelectedVoice);

        StartAudio();

        _proactiveTimer = new Timer(_ => DeliverProactive(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        if (_conversationSettings.EchoCancel)
        {
            try
            {
                _echo = new Edith.Audio.Echo.EchoCanceller();
                _echoReference = new Edith.Audio.Echo.EchoReference(_echo);
                _echoReference.Start();
                EdithEvents.Publish("voice.echo", "эхоподавление включено: " + _echoReference.FormatDescription);
            }
            catch (Exception ex)
            {
                _echo = null;
                EdithEvents.Publish(EventKinds.Error, "Эхоподавление не запустилось: " + ex.Message);
            }
        }

        _micWatchdog = new Timer(_ => CheckMicrophone(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(6));
        _bargeScanTimer = new Timer(
            _ =>
            {
                if (_voiceMode is VoiceMode.Speaking or VoiceMode.Busy)
                    RequestBargeCheck("scan", "");
            },
            null, TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(900));

        if (_response.Memory is Edith.Core.Memory.SqliteMemoryStore memoryStore)
        {
            _distiller = new Edith.Core.Memory.MemoryDistiller(memoryStore, BrainService.Shared.CompleteJsonAsync, _runtime.Bus);
            _episodes = new Edith.Core.Memory.EpisodeSummarizer(memoryStore, BrainService.Shared.CompleteJsonAsync, _runtime.Bus);
            _distillerTimer = new Timer(_ => _ = DistillMemoryAsync(), null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        }

        PulseSettings pulseSettings = PulseSettings.Load();
        if (pulseSettings.Enabled)
        {
            _pulseService = new PulseService(
                new Edith.Core.Activity.ActivityStore(@"C:\EDITH\data\memory\activity.db"),
                pulseSettings,
                _runtime.Bus,
                (title, text) =>
                {
                    // Silent balloon, and not over a game.
                    if (_runtime.Resources.Current is { GameActive: true })
                        return;
                    Dispatcher.BeginInvoke(() => _tray?.ShowMessage(title, text));
                });
        }

        _librarian = new LibrarianService(
            _runtime.Bus,
            () => _voiceMode == VoiceMode.WaitingWake,
            () => _runtime.Resources.Current);

        // UI/wake already run; model residency is intentionally detached.
        // Measure the GPU first so warm-up does not load the big model onto a busy card.
        _ = Task.Run(() =>
        {
            _runtime?.Resources.Poll();
            return BrainService.Shared.PrimeAsync();
        });
    }


    private void StartAudio()
    {
        // Защита от двойной подписки events / второго capture.
        if (_audioStarted)
            return;

        _audioStarted = true;

        try
        {
            Vosk.Vosk.SetLogLevel(-1);

            _speechModel =
                new Model(
                    @"C:\EDITH\models\wake\vosk-model-small-ru-0.22");

            // Выбор движка (custom ONNX / Vosk) и diagnostics
            // делает WakeRuntime; сбой custom = Vosk fallback.
            _wake =
                WakeRuntime.Create(
                    new WakeWordDetector(
                        _speechModel));

            _command =
                CreateCommandRecognizer(
                    new CommandRecognizer(
                        _speechModel));

            _interrupt =
                new InterruptRecognizer(
                    _speechModel);

            _turnDetector = _conversationSettings.SmartTurn
                ? SmartTurnDetector.TryCreate(_conversationSettings.SmartTurnModelPath)
                : null;
            VoiceDiagnostics.WriteRaw("turn-detector.txt", _turnDetector is null ? "off: pause length only" : "smart-turn v3.2");

            // The recognisers and the wake detector first: with no microphone yet (not enumerated
            // after boot or sleep) they simply wait, and the watchdog attaches the microphone later.
            _wake.WakeDetected +=
                OnWakeDetected;

            _interrupt.InterruptDetected +=
                OnInterruptDetected;

            // GigaAM re-reads every acoustic wake, so the VAD guard would only cost real calls.
            _wake.RequireRecentSpeech = !(_confirmWake && _giga is not null);

            _wake.Start();

            _microphone =
                new MicrophoneMonitor(
                    (_turnDetector is null
                        ? _conversationSettings.SpeechEndSilence
                        : _conversationSettings.TurnEndSilence).TotalSeconds);


            _microphone.SamplesReady +=
                OnSamplesReady;

            _microphone.SpeechStarted +=
                OnSpeechStarted;

            _microphone.SpeechEnded +=
                OnSpeechEnded;

            VoiceDiagnostics.WriteRaw(
                "active-microphone.txt",
                _microphone.DeviceName + Environment.NewLine + _microphone.CaptureFormat);

            _microphone.Start();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("microphone-error.txt", ex.ToString());
            EdithEvents.Publish(EventKinds.Error, "Не удалось запустить микрофон: " + ex.Message);
        }
    }

    private void CheckMicrophone()
    {
        if (!_audioStarted)
            return;

        long frames = Interlocked.Read(ref _audioFramesReceived);
        if (_microphone is null)
        {
            TryRestartMicrophone("микрофон не запущен");
            return;
        }

        if (frames == _watchdogLastFrames)
        {
            if (++_watchdogStale >= 2)
            {
                _watchdogStale = 0;
                TryRestartMicrophone("звук с микрофона не идёт");
            }
        }
        else
        {
            _watchdogStale = 0;
        }

        _watchdogLastFrames = frames;
    }


    /// <summary>Rebuilds the microphone capture; the recognizers and the wake detector stay as they are.</summary>
    private void TryRestartMicrophone(string reason)
    {
        if (Interlocked.Exchange(ref _micRestarting, 1) == 1)
            return;

        try
        {
            MicrophoneMonitor? old = _microphone;
            _microphone = null;
            if (old is not null)
            {
                old.SamplesReady -= OnSamplesReady;
                old.SpeechStarted -= OnSpeechStarted;
                old.SpeechEnded -= OnSpeechEnded;
                try { old.Dispose(); } catch { }
            }

            var mic = new MicrophoneMonitor(
                (_turnDetector is null ? _conversationSettings.SpeechEndSilence : _conversationSettings.TurnEndSilence).TotalSeconds);
            mic.SamplesReady += OnSamplesReady;
            mic.SpeechStarted += OnSpeechStarted;
            mic.SpeechEnded += OnSpeechEnded;
            mic.Start();
            _microphone = mic;
            _wake?.Reset();
            _watchdogLastFrames = -1;

            VoiceDiagnostics.WriteRaw("active-microphone.txt", mic.DeviceName + Environment.NewLine + mic.CaptureFormat);
            EdithEvents.Publish("voice.mic-restart", $"микрофон перезапущен ({reason}): {mic.DeviceName}");
        }
        catch (Exception ex)
        {
            _microphone = null;
            // The watchdog tries again in 6 s; the journal hears about it at most once a minute.
            if (DateTimeOffset.Now - _micErrorLoggedAt > TimeSpan.FromMinutes(1))
            {
                _micErrorLoggedAt = DateTimeOffset.Now;
                EdithEvents.Publish(EventKinds.Error, $"Микрофон не поднялся ({reason}): {ex.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _micRestarting, 0);
        }
    }


    /// <summary>The voice is about to start: clean interrupt listening, "speaking" state and orb.</summary>
    private void EnterSpeaking(long session, string spokenText)
    {
        // Чистый interrupt stream
        // перед каждым ответом.
        _interrupt?
            .StartListening();
        _interrupt?.SetEchoText(spokenText);

        _voiceMode =
            VoiceMode.Speaking;

        _interactionLatency?.Mark("response-ready");

        VoiceDiagnostics.VoiceEvent("speaking-entered", $"session={session}");

        Dispatcher.Invoke(() =>
        {
            if (!IsSessionActive(session))
                return;

            _overlay?.SetState(
                Edith.Core.AssistantState.Speaking,
                "Speaking",
                session);
        });
    }


    private void BeginManualActivation()
    {
        if (_voiceMode != VoiceMode.WaitingWake)
            return;

        long session = StartSession();
        LogWakePipeline("wake-accepted", "manual-hotkey");
        EdithEvents.Publish(EventKinds.WakeAccepted, "Горячая клавиша", session,
            new Dictionary<string, string> { ["source"] = "manual-hotkey" });
        _voiceMode = VoiceMode.WakeActive;
        ShowListening();
        BeginWaitingForCommand(
            session,
            (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds,
            "hotkey");
    }

    private void EndManualActivation()
    {
        long session = Volatile.Read(ref _activeSession);
        if (session != 0)
        {
            EndSession(session);
            return;
        }

        _voiceMode = VoiceMode.WaitingWake;
        ProjectWaitingWake("manual-hotkey-toggle-off");
    }


    private void OnSamplesReady(
        float[] samples,
        int count)
    {
        Interlocked.Increment(ref _audioFramesReceived);
        _recentAudio.Add(samples, count);

        // Loudness for the listening orb (the overlay only draws it while visible).
        if (count > 0)
        {
            double sum = 0;
            for (int i = 0; i < count; i++)
                sum += samples[i] * samples[i];
            _overlay?.SetMicrophoneLevel((float)Math.Sqrt(sum / count));
        }

        if (_echo is not null)
        {
            float[] clean = _echo.ProcessNear(samples.AsSpan(0, count));
            _cleanAudio.Add(clean, clean.Length);
        }
        Interlocked.Add(ref _totalSamples, count);

        if (_voiceMode == VoiceMode.ConfirmingWake)
        {
            lock (_confirmGate)
            {
                // Re-checked under the lock: the confirmation may have just finished.
                if (_voiceMode == VoiceMode.ConfirmingWake)
                {
                    _confirmBacklog.Add(samples.AsSpan(0, count).ToArray());
                    return;
                }
            }
        }

        VoiceMode mode =
            _voiceMode;

        if (mode == VoiceMode.WaitingWake)
        {
            if (_wakePathIdle)
            {
                _wakePathIdle = false;

                _wake?.Reset();
                LogWakePipeline("wake-rearm", "audio-path-entered");
            }
        }
        else if (mode !=
                 VoiceMode.WakeActive)
        {
            _wakePathIdle = true;
        }

        switch (mode)
        {
            case VoiceMode.WaitingWake:

                _wake?.AcceptWakeAudio(
                    samples,
                    count,
                    _microphone?.IsSpeech ?? false);

                break;


            case VoiceMode.WakeActive:

                _wake?.AcceptActiveAudio(
                    samples,
                    count);

                lock (_wakePhraseGate)
                {
                    if (_wakePhraseAudio.Count < 16000 * 20)
                        _wakePhraseAudio.AddRange(samples.AsSpan(0, count));
                }

                break;


            case VoiceMode.WaitingCommand:
            case VoiceMode.CapturingCommand:

                _command?.AcceptAudio(
                    samples,
                    count);

                break;


            case VoiceMode.Speaking:
            case VoiceMode.InterruptTriggered:

                FeedHybridBargeWake(samples, count);
                _interrupt?.AcceptAudio(
                    samples,
                    count);

                break;


            case VoiceMode.Busy:
                // Thinking is interruptible too.  Before this pass the busy
                // branch discarded the wake audio, so "Эдит" could not cancel
                // a slow Brain request at all.
                FeedHybridBargeWake(samples, count);
                _interrupt?.AcceptAudio(
                    samples,
                    count);
                break;
        }
    }


    private void OnWakeDetected(
        WakeDetection detection)
    {
        if (_voiceMode !=
            VoiceMode.WaitingWake)
        {
            // Custom hybrid detection has the same cancellation contract as
            // the Vosk interrupt path. Other non-idle wakes stay suppressed.
            if (_wake?.Engine is WakeEngineKind.Hybrid or WakeEngineKind.CustomV3 or WakeEngineKind.Custom &&
                detection.Source == "custom" &&
                _voiceMode is VoiceMode.Busy or VoiceMode.Speaking)
            {
                // The acoustic model also fires on her own voice: GigaAM decides.
                RequestBargeCheck("wake", detection.Text);
            }

            return;
        }

        if (NeedsTranscriptConfirmation(detection))
        {
            BeginWakeConfirmation(detection);
            return;
        }

        AcceptWake(detection);
    }


    /// <summary>
    /// Wakes that only an acoustic model vouches for (custom / custom-v3) are re-read by GigaAM:
    /// V3 alone accepted dense speech (a poem) twice in 11 s; GigaAM never hears "эдит" there.
    /// Hybrid wakes already carry Vosk's word and need no second opinion.
    /// </summary>
    private bool NeedsTranscriptConfirmation(WakeDetection detection) =>
        _confirmWake &&
        _giga is not null &&
        detection.Engine == OnnxWakeDetector.EngineName &&
        _wake?.Engine is WakeEngineKind.CustomV3 or WakeEngineKind.Custom;


    private void BeginWakeConfirmation(WakeDetection detection)
    {
        float[] window = _recentAudio.Last(16000 * 2);

        lock (_confirmGate)
        {
            _confirmBacklog.Clear();
            _confirmSpeechEnded = false;
            _voiceMode = VoiceMode.ConfirmingWake;
        }

        // The orb waits for the GigaAM verdict (~0.5 s): the acoustic model also fires on game and
        // desk noise (about four times a minute in a match), and each of those flashed the blue orb.
        bool speechAtFire = _microphone is { IsSpeech: true };

        GigaAmCommandRecognizer giga = _giga!;
        _ = Task.Run(async () =>
        {
            // The detector fires mid-word; the window must include the end of "Эдит"
            // (live runs heard "эт" and silence and dropped real wakes).
            await Task.Delay(350);

            // The acoustic model also fires on game and desk noise (1700 times a day in a match, each one a GigaAM decode and a
            // journal line). A real "Эдит" is speech: when the microphone heard none around the moment, it was noise.
            if (!speechAtFire && !SpeechRecently())
            {
                FinishWakeConfirmation(detection, confirmed: false, heard: "", quiet: true);
                return;
            }

            window = _recentAudio.Last(16000 * 2);
            Stopwatch clock = Stopwatch.StartNew();
            string heard;
            try
            {
                heard = giga.Decode(window).NormalizedText;
            }
            catch (Exception ex)
            {
                // A broken confirmer must not make EDITH deaf: trust the acoustic model.
                heard = "эдит";
                VoiceDiagnostics.WriteRaw("wake-confirm-error.txt", ex.ToString());
            }

            bool confirmed = WakeTranscriptConfirmer.Confirms(heard);
            EdithEvents.Publish("voice.wake-check", confirmed ? "принято" : "отклонено",
                data: new Dictionary<string, string>
                {
                    ["heard"] = heard,
                    ["confirmed"] = confirmed.ToString(),
                    ["score"] = detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    ["check-ms"] = clock.ElapsedMilliseconds.ToString(),
                    ["vad-at-fire"] = speechAtFire.ToString(),
                });
            VoiceDiagnostics.WriteSnapshot(
                "wake-confirm.txt",
                $"confirmed={confirmed}",
                $"heard={heard}",
                $"score={detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)}",
                $"elapsed-ms={clock.ElapsedMilliseconds}");

            FinishWakeConfirmation(detection, confirmed, heard);
        });
    }


    /// <summary>Speech now, or one that started within the last two seconds.</summary>
    private bool SpeechRecently()
    {
        if (_microphone is { IsSpeech: true })
            return true;

        long start = Interlocked.Read(ref _speechStartSample);
        return start >= 0 && Interlocked.Read(ref _totalSamples) - start < 16000 * 2;
    }

    private void FinishWakeConfirmation(WakeDetection detection, bool confirmed, string heard, bool quiet = false)
    {
        bool speechEnded;
        lock (_confirmGate)
        {
            if (_voiceMode != VoiceMode.ConfirmingWake)
                return;

            speechEnded = _confirmSpeechEnded;

            if (!confirmed)
            {
                _confirmBacklog.Clear();
                _voiceMode = VoiceMode.WaitingWake;
                _wake?.Reset();
                if (!quiet)
                    LogWakePipeline("wake-rejected", "stt-did-not-hear-edith: " + heard);
                ProjectWaitingWake("wake-rejected-by-stt");
                if (speechEnded)
                    TryUtteranceWake();

                if (quiet)
                    return;

                // Visible in the status panel and the journal: what V3 fired on and what was heard.
                EdithEvents.Publish(
                    "voice.wake-rejected",
                    string.IsNullOrWhiteSpace(heard) ? "(тишина)" : heard,
                    data: new Dictionary<string, string>
                    {
                        ["score"] = detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                        ["heard"] = heard,
                    });
                return;
            }

            // Audio that arrived while GigaAM was busy belongs to the phrase after "Эдит".
            _voiceMode = VoiceMode.WaitingWake;
            AcceptWake(detection, ignoreSpeechState: true);
            foreach (float[] chunk in _confirmBacklog)
                _wake?.AcceptActiveAudio(chunk, chunk.Length);
            _confirmBacklog.Clear();
        }

        if (speechEnded || _microphone is { IsSpeech: false })
            OnSpeechEnded();
    }


    private const int MaxUtteranceWakeSamples = 16000 * 4;

    /// <summary>
    /// A short phrase ended while EDITH slept and the acoustic detector did not fire: decode it
    /// with GigaAM (~0.2 s, background) and wake if it contains "Эдит". What follows the name in
    /// the same phrase is the command.
    /// </summary>
    private void TryUtteranceWake()
    {
        long start = Interlocked.Exchange(ref _speechStartSample, -1);
        if (_giga is null || !_confirmWake || start < 0)
            return;

        // VAD start lags the voice a little: take a short pre-roll.
        long length = Interlocked.Read(ref _totalSamples) - start + 16000 * 3 / 10;
        if (length > MaxUtteranceWakeSamples + 16000 || length < 16000 / 4)
            return;

        if (Interlocked.Exchange(ref _utteranceWakeRunning, 1) == 1)
            return;

        float[] audio = _recentAudio.Last((int)length);
        GigaAmCommandRecognizer giga = _giga;
        _ = Task.Run(() =>
        {
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                string heard = giga.Decode(audio).NormalizedText;
                bool wake = WakeTranscriptConfirmer.ConfirmsUtterance(heard);
                EdithEvents.Publish("voice.wake-check", wake ? "принято (фраза)" : "нет имени",
                    data: new Dictionary<string, string>
                    {
                        ["heard"] = heard,
                        ["confirmed"] = wake.ToString(),
                        ["path"] = "utterance",
                        ["check-ms"] = clock.ElapsedMilliseconds.ToString(),
                    });

                if (!wake)
                    return;

                Dispatcher.Invoke(() =>
                {
                    if (_voiceMode != VoiceMode.WaitingWake || Volatile.Read(ref _activeSession) != 0)
                        return;

                    AcceptWake(new WakeDetection("gigaam", 1f, heard, DateTimeOffset.Now) { Source = "utterance" }, ignoreSpeechState: true);
                    long session = Volatile.Read(ref _activeSession);
                    string command = WakeTranscriptConfirmer.CommandAfterWake(heard);
                    if (command.Length > 0)
                        BeginCommand(session, command, new CommandTranscript(command, command, GigaAmCommandRecognizer.EngineName, null));
                    else
                        BeginWaitingForCommand(session, (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds, "utterance-wake");
                });
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.WriteRaw("utterance-wake-error.txt", ex.ToString());
            }
            finally
            {
                Volatile.Write(ref _utteranceWakeRunning, 0);
            }
        });
    }


    private void AcceptWake(WakeDetection detection, bool ignoreSpeechState = false)
    {
        // The wake word and anything said during its confirmation are already in the ring.
        lock (_wakePhraseGate)
        {
            _wakePhraseAudio.Clear();
            if (_giga is not null)
                _wakePhraseAudio.AddRange(_recentAudio.Last(16000 * 3));
        }

        long session =
            StartSession();

        Interlocked.Exchange(ref _weakRewakeUntilTicks, 0);
        LogWakePipeline("wake-accepted", "detector-event");
        EdithEvents.Publish(EventKinds.WakeAccepted, detection.Text, session,
            new Dictionary<string, string>
            {
                ["engine"] = detection.Engine,
                ["source"] = detection.Source,
                ["score"] = detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            });

        VoiceDiagnostics.WriteSnapshot(
            "last-wake.txt",
            [
                $"session={session}",
                $"engine={detection.Engine}",
                $"source={detection.Source}",
                .. detection.Engine == OnnxWakeDetector.EngineName
                    ? new[] { "score=" + detection.Score.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) }
                    : [],
                detection.Text,
            ]);

        _voiceMode =
            VoiceMode.WakeActive;

        ShowListening();

        // Custom-детектор может сработать уже ПОСЛЕ того,
        // как VAD закончил произнесение "Эдит" (score растёт
        // на хвосте слова). Тогда OnSpeechEnded уже не придёт,
        // и session нужно продолжить сразу, иначе она зависнет.
        if (!ignoreSpeechState &&
            detection.Engine ==
                OnnxWakeDetector.EngineName &&
            _microphone is { IsSpeech: false })
        {
            OnSpeechEnded();
        }
    }


    // ========================================================
    // Trigger найден ЕЩЁ ВО ВРЕМЯ речи пользователя.
    // ========================================================

    private void OnInterruptDetected(
        string trigger,
        string recognized)
    {
        // Vosk (small model) both misses stop words over EDITH's voice and invents them in the
        // user's own speech (a command was cancelled by a phantom "стой"): GigaAM has the last word.
        RequestBargeCheck("vosk", recognized);
    }


    private readonly object _bargeScanGate = new();
    private int _bargeChecking;
    private long _scanFloor;
    private string _lastScanText = "";
    private Timer? _bargeScanTimer;

    /// <summary>
    /// While EDITH talks or thinks, read the last 3 s of the microphone with GigaAM and look for a
    /// stop word or "Эдит" that is not part of her own answer. Runs when Vosk or the wake model
    /// suspects something, and every second regardless: Vosk alone hears nothing through her voice.
    /// </summary>
    private void RequestBargeCheck(string source, string vosk)
    {
        GigaAmCommandRecognizer? giga = _giga;
        InterruptRecognizer? interrupt = _interrupt;
        if (giga is null || interrupt is null)
        {
            // No GigaAM: keep the old behaviour, Vosk's word is all there is.
            if (source == "vosk" && InterruptRecognizer.TryFindTrigger(vosk, out string only))
                OnInterruptConfirmed(only, vosk);
            return;
        }

        if (Interlocked.Exchange(ref _bargeChecking, 1) == 1)
            return;

        long session = Volatile.Read(ref _activeSession);
        _ = Task.Run(async () =>
        {
            try
            {
                // The word that triggered us is still being said; let it finish.
                if (source != "scan")
                    await Task.Delay(300);

                if (_voiceMode is not (VoiceMode.Busy or VoiceMode.Speaking) ||
                    session != Volatile.Read(ref _activeSession))
                {
                    return;
                }

                // Only sound recorded after the command ended: the user's own "Эдит, открой…" is
                // still in the last seconds and must not cancel the command it started.
                long total = Interlocked.Read(ref _totalSamples);
                int available = Edith.Core.BargeScanWindow.Available(total, Volatile.Read(ref _scanFloor));
                if (available == 0)
                    return;

                float[] audio = (_echo is not null ? _cleanAudio : _recentAudio).Last(available);
                string heard;
                lock (_bargeScanGate)
                    heard = giga.Decode(audio).NormalizedText;

                bool found = interrupt.FindTrigger(heard, out string trigger);
                if (heard.Length > 0 && (found || heard != _lastScanText))
                {
                    _lastScanText = heard;
                    EdithEvents.Publish("voice.scan", found ? $"«{heard}» → {trigger}" : $"«{heard}»", session,
                        new Dictionary<string, string> { ["source"] = source, ["vosk"] = vosk });
                }

                if (!found ||
                    _voiceMode is not (VoiceMode.Busy or VoiceMode.Speaking) ||
                    session != Volatile.Read(ref _activeSession))
                {
                    return;
                }

                OnInterruptConfirmed(trigger, heard);
            }
            catch (Exception ex)
            {
                VoiceDiagnostics.WriteRaw("barge-scan-error.txt", ex.ToString());
            }
            finally
            {
                Volatile.Write(ref _bargeChecking, 0);
            }
        });
    }


    private void OnInterruptConfirmed(
        string trigger,
        string recognized)
    {
        EdithEvents.Publish("voice.cancel", $"{trigger}: «{recognized}»");
        if (IsGlobalStop(trigger))
        {
            CancelActiveInteraction("stop-partial");
            return;
        }
        BeginBargeIn(trigger, recognized);
    }


    private void BeginBargeIn(
        string trigger,
        string recognized)
    {
        if (_voiceMode is not
            (VoiceMode.Busy or VoiceMode.Speaking))
        {
            return;
        }

        long oldSession =
            Volatile.Read(
                ref _activeSession);

        if (!IsSessionActive(
                oldSession))
        {
            return;
        }


        // Создаём новую сессию.
        // Старая больше ничего не может сделать.
        long newSession =
            StartSession();


        _voiceMode =
            VoiceMode.InterruptTriggered;


        // Останавливаем голос СРАЗУ,
        // как только partial увидел trigger.
        _tts?.Stop();

        VoiceDiagnostics.VoiceEvent(
            "barge-in",
            $"old-session={oldSession}; new-session={newSession}; trigger={trigger}");
        EdithEvents.Publish(EventKinds.BargeIn, trigger, newSession,
            new Dictionary<string, string> { ["old-session"] = oldSession.ToString(), ["heard"] = recognized });


        VoiceDiagnostics.WriteSnapshot(
            "last-interrupt.txt",
            $"old-session={oldSession}",
            $"new-session={newSession}",
            $"trigger={trigger}",
            $"partial={recognized}");


        ShowListening();

        // ВАЖНО:
        // _interrupt НЕ останавливаем.
        //
        // Он продолжает слушать:
        //
        // "стой А какая погода..."
    }


    private void OnSpeechStarted()
    {
        // The user went on talking: any pending "is he done?" decision is void.
        Interlocked.Increment(ref _turnHoldVersion);

        if (_voiceMode == VoiceMode.WaitingWake)
            Interlocked.Exchange(ref _speechStartSample, Interlocked.Read(ref _totalSamples));

        if (_voiceMode == VoiceMode.WaitingWake ||
            (_wake?.Engine == WakeEngineKind.Hybrid &&
             _voiceMode is VoiceMode.Busy or VoiceMode.Speaking))
        {
            // Начало речи нужно custom-детектору, чтобы отдать
            // Vosk именно это произнесение (pre-roll).
            _wake?.NotifySpeechStarted();
        }

        if (_voiceMode ==
            VoiceMode.WaitingCommand)
        {
            CancelCommandTimeout();

            _commandSpeechStartedAt = DateTimeOffset.Now;
            _commandSpeechClock = Stopwatch.StartNew();

            _voiceMode =
                VoiceMode.CapturingCommand;

            VoiceDiagnostics.VoiceEvent("speech-start", $"session={_activeSession}");
        }
    }


    private void OnSpeechEnded() => HandleSpeechEnded(forced: false);


    /// <param name="forced">The end of the phrase is already decided (Smart Turn said so, or the hold ran out).</param>
    private void HandleSpeechEnded(bool forced)
    {
        if (_voiceMode == VoiceMode.ConfirmingWake)
        {
            lock (_confirmGate)
            {
                if (_voiceMode == VoiceMode.ConfirmingWake)
                {
                    // Replayed once GigaAM confirms the wake.
                    _confirmSpeechEnded = true;
                    return;
                }
            }
        }

        // ====================================================
        // SLEEP
        // ====================================================

        if (_voiceMode ==
            VoiceMode.WaitingWake)
        {
            // Custom: wake решает ML-модель по звуку, текст в
            // режиме сна не разбирается (и Vosk в этот момент
            // вообще не получает аудио).
            if (_wake is not
                { UsesTextWakeHeuristics: true })
            {
                // The acoustic model missed it (or its check cut the word): GigaAM reads the
                // whole short phrase itself, so "Эдит" and "Эдит, открой вк" still work.
                TryUtteranceWake();
                ProjectWaitingWake("speech-ended-no-text-wake");
                return;
            }

            string phrase =
                FinishWakeUtterance();

            if (_wake?.TryAcceptWeakVoskFinal(
                    phrase,
                    out WakeDetection fusion) == true)
            {
                long fusionSession = StartSession();
                LogWakePipeline("wake-accepted", fusion.Source);
                ShowListening();
                BeginWaitingForCommand(
                    fusionSession,
                    (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);
                return;
            }

            // Если WakeWordDetected не сработал заранее,
            // сохраняем то, что Vosk реально услышал.
            //
            // Это позволит отличить:
            // - "эдит" -> проблема state machine
            // - "едет"/"эдик"/и т.п. -> проблема wake recognition
            // - пусто -> проблема VAD/audio boundary
            VoiceDiagnostics.WriteSnapshot("last-wake-heard.txt", $"mode={_voiceMode}", phrase);

            if (!TryExtractWakeCommand(
                    phrase,
                    out string command))
            {
                if (TryAcceptPostInteractionWeakWake(phrase, out long weakSession))
                {
                    ShowListening();
                    BeginWaitingForCommand(weakSession,
                        (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);
                    return;
                }

                LogWakePipeline("wake-rejected", "transcript-not-wake");
                ProjectWaitingWake("wake-rejected");
                return;
            }

            long session =
                StartSession();

            VoiceDiagnostics.FullPhrase(
                session,
                phrase);

            ShowListening();

            if (!string.IsNullOrWhiteSpace(
                    command))
            {
                BeginCommand(
                    session,
                    command);

                return;
            }

            BeginWaitingForCommand(
                session,
                (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);

            return;
        }


        // ====================================================
        // ЭДИТ + КОМАНДА одной фразой
        // ====================================================

        if (_voiceMode ==
            VoiceMode.WakeActive)
        {
            long session =
                Volatile.Read(
                    ref _activeSession);

            string phrase =
                FinishWakeUtterance();

            VoiceDiagnostics.FullPhrase(
                session,
                phrase);

            // GigaAM hears the whole "Эдит, <команда>" far better than the small Vosk model.
            string gigaCommand = CommandFromWakePhrase();
            if (gigaCommand.Length > 0)
            {
                BeginCommand(
                    session,
                    gigaCommand,
                    new CommandTranscript(gigaCommand, gigaCommand, GigaAmCommandRecognizer.EngineName, null));

                return;
            }

            if (TryExtractWakeCommand(
                    phrase,
                    out string command) &&
                !string.IsNullOrWhiteSpace(
                    command))
            {
                BeginCommand(
                    session,
                    command);

                return;
            }

            BeginWaitingForCommand(
                session,
                (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);

            return;
        }


        // ====================================================
        // ЭДИТ / пауза / КОМАНДА
        // ====================================================

        if (_voiceMode ==
            VoiceMode.CapturingCommand)
        {
            long session =
                Volatile.Read(
                    ref _activeSession);

            if (!forced && _turnDetector is not null)
            {
                BeginTurnDecision(session);
                return;
            }

            Stopwatch sttClock = Stopwatch.StartNew();
            CommandTranscript transcript =
                _command?.FinishSessionDetailed()
                ?? new CommandTranscript("", "", "vosk-command", null);
            sttClock.Stop();

            double speechDuration = _commandSpeechClock?.Elapsed.TotalMilliseconds ?? 0;
            DateTimeOffset speechStartedAt = _commandSpeechStartedAt ?? DateTimeOffset.Now;
            _commandSpeechClock = null;
            _commandSpeechStartedAt = null;

            CommandTranscript normalizedTranscript = transcript with
            {
                NormalizedText = TechTermNormalizer.Normalize(CleanDirectCommand(transcript.NormalizedText))
            };
            VoiceDiagnostics.Transcript(session, normalizedTranscript, speechStartedAt, speechDuration, sttClock.Elapsed.TotalMilliseconds);
            EdithEvents.Publish("voice.transcript", normalizedTranscript.NormalizedText, session,
                new Dictionary<string, string>
                {
                    ["raw"] = normalizedTranscript.RawText,
                    ["engine"] = normalizedTranscript.Engine,
                    ["speech-ms"] = speechDuration.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                    ["stt-ms"] = sttClock.Elapsed.TotalMilliseconds.ToString("F0", System.Globalization.CultureInfo.InvariantCulture),
                });
            _interactionLatency?.Mark("stt-final", $"engine={transcript.Engine}; confidence={transcript.Confidence?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}");

            string text = normalizedTranscript.NormalizedText;

            text =
                CleanDirectCommand(text);

            if (string.IsNullOrWhiteSpace(
                    text))
            {
                if (_turnPrefix.Length > 0)
                {
                    text = _turnPrefix;
                    _turnPrefix = "";
                }
                else
                {
                    EndSession(session);
                    return;
                }
            }
            else if (_turnPrefix.Length > 0)
            {
                // The rest of a phrase that stopped on a dangling word.
                text = _turnPrefix + " " + text;
                _turnPrefix = "";
            }
            else if (_conversationSettings.ContinueAfterDanglingWord && TurnText.EndsMidSentence(text))
            {
                // "открой и…": nobody ends a command on "и". Keep this part, listen once more.
                _turnPrefix = text;
                EdithEvents.Publish("voice.turn", $"«{text}» — обрыв на предлоге/союзе, жду продолжение", session);
                ShowListening();
                BeginWaitingForCommand(session,
                    (int)(_conversationSettings.TurnMaxHold.TotalMilliseconds + 1500), "continue");
                return;
            }

            BeginCommand(session, text, normalizedTranscript);

            return;
        }


        // ====================================================
        // EDITH ЕЩЁ ГОВОРИТ,
        // но partial НЕ успел обнаружить trigger.
        //
        // Проверяем FINAL результат.
        // Это особенно важно для короткого "стой".
        // ====================================================

        if (_voiceMode ==
            VoiceMode.Speaking)
        {
            long oldSession =
                Volatile.Read(
                    ref _activeSession);

            string phrase =
                _interrupt?
                    .FinishUtterance()
                ?? "";

            VoiceDiagnostics.WriteSnapshot("last-interrupt-heard.txt", $"session={oldSession}", phrase);


            if (_interrupt is null ||
                !_interrupt.FindTrigger(
                    phrase,
                    out string trigger))
            {
                // Это была обычная речь.
                //
                // EDITH продолжает свой ответ.
                // Начинаем чистый interrupt utterance.
                if (IsSessionActive(
                        oldSession))
                {
                    _interrupt?
                        .StartListening();
                }

                return;
            }


            // Trigger нашли только в FINAL.
            long newSession =
                StartSession();

            _tts?.Stop();

            _voiceMode =
                VoiceMode.InterruptTriggered;


            VoiceDiagnostics.WriteSnapshot(
                "last-interrupt.txt",
                $"old-session={oldSession}",
                $"new-session={newSession}",
                $"trigger={trigger}",
                $"final={phrase}");


            ShowListening();


            string tail =
                ExtractInterruptTail(
                    phrase);

            if (!string.IsNullOrWhiteSpace(
                    tail))
            {
                BeginCommand(
                    newSession,
                    tail);

                return;
            }


            BeginWaitingForCommand(
                newSession,
                (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);

            return;
        }


        // ====================================================
        // Trigger был обнаружен раньше по PARTIAL.
        //
        // Теперь пользователь закончил:
        //
        // "стой а какая погода..."
        // ====================================================

        if (_voiceMode ==
            VoiceMode.InterruptTriggered)
        {
            long session =
                Volatile.Read(
                    ref _activeSession);


            string phrase =
                _interrupt?
                    .FinishUtterance()
                ?? "";


            string tail =
                ExtractInterruptTail(
                    phrase);


            VoiceDiagnostics.WriteSnapshot(
                "last-interrupt-tail.txt",
                $"session={session}",
                $"full={phrase}",
                $"tail={tail}");


            // "стой, а открой блокнот"
            if (!string.IsNullOrWhiteSpace(
                    tail))
            {
                BeginCommand(
                    session,
                    tail);

                return;
            }


            // Просто "стой".
            //
            // Голос уже остановлен.
            // Ждём максимум 2 секунды.
            BeginWaitingForCommand(
                session,
                (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds);

            return;
        }
    }


    /// <summary>What followed "Эдит" in the same breath, by GigaAM; "" when nothing did or GigaAM is off.</summary>
    private string CommandFromWakePhrase()
    {
        float[] audio;
        lock (_wakePhraseGate)
        {
            audio = _wakePhraseAudio.ToArray();
            _wakePhraseAudio.Clear();
        }

        if (_giga is null || audio.Length < 16000)
            return "";

        try
        {
            string heard = _giga.Decode(audio).NormalizedText;
            string command = WakeTranscriptConfirmer.CommandAfterWake(heard);
            VoiceDiagnostics.WriteSnapshot("wake-phrase-giga.txt", $"heard={heard}", $"command={command}");
            return command;
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("wake-phrase-giga-error.txt", ex.ToString());
            return "";
        }
    }


    private static bool IsOnlyWakeWords(string text)
    {
        string[] words = text.Split([' ', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 3 &&
               words.All(w => WakeTranscriptConfirmer.IsWakeToken(w) || w is "эй" or "слушай" or "ну" or "энит" or "эдик");
    }


    private string FinishWakeUtterance()
    {
        string phrase =
            _wake?
                .FinishUtterance()
            ?? "";

        _wake?.Reset();

        return phrase.Trim();
    }


    private long StartSession()
    {
        long session =
            Interlocked.Increment(
                ref _sessionCounter);

        Volatile.Write(
            ref _activeSession,
            session);

        _interactionLatency = new InteractionLatency(
            Path.Combine(VoiceDiagnostics.Directory, "interaction-latency.txt"));
        _interactionLatency.Mark("session-start", $"session={session}");

        lock (_interactionGate)
        {
            _interactionCancellation?.Cancel();
            _interactionCancellation?.Dispose();
            _interactionCancellation = new CancellationTokenSource();
            _interactionCancellationSession = session;
        }

        return session;
    }


    private const int WakeLeadSamples = 16000 * 9 / 10;

    private void BeginWaitingForCommand(
        long session,
        int timeoutMilliseconds,
        string reason = "wake")
    {
        if (!IsSessionActive(session))
            return;

        _interrupt?
            .StopListening();

        _command?
            .CancelSession();

        _command?
            .StartSession();

        // The name is recognised a moment after it is said; a fluent "Эдит, убери окно" has already begun the command by then
        // (the first word was lost: only "окно" reached the recogniser). Give the session the last stretch of sound; the leading
        // "Эдит" in it is cut off as for any command.
        if (reason == "wake")
        {
            float[] lead = _recentAudio.Last(WakeLeadSamples);
            _command?.AcceptAudio(lead, lead.Length);
        }

        _voiceMode =
            VoiceMode.WaitingCommand;

        if (reason != "continue")
            _turnPrefix = "";

        VoiceDiagnostics.VoiceEvent("listening-enter", $"session={session}; reason={reason}; speech-start-window={timeoutMilliseconds}ms");

        StartCommandTimeout(
            session,
            timeoutMilliseconds);
    }


    private void BeginCommand(
        long session,
        string text,
        CommandTranscript? transcript = null)
    {
        if (!IsSessionActive(session))
            return;

        CancelCommandTimeout();
        Dispatcher.BeginInvoke(() => Answers().NoteQuestion());

        _interrupt?
            .StopListening();

        string rawText = transcript?.RawText ?? text;
        text = TechTermNormalizer.Normalize(CleanDirectCommand(transcript?.NormalizedText ?? text));
        if (transcript is null)
        {
            VoiceDiagnostics.Transcript(session, new CommandTranscript(rawText, text, "wake-vosk", null), DateTimeOffset.Now, 0, 0);
        }
        VoiceDiagnostics.AppendInteractionTrace(session, "USER HEARD", rawText);
        VoiceDiagnostics.AppendInteractionTrace(session, "TRANSCRIPT",
            $"raw={rawText}{Environment.NewLine}normalized={text}");

        if (IsGlobalStop(text))
        {
            CancelActiveInteraction("stop-command");
            return;
        }

        // Just "Эдит" again (or a mishearing of it): no question yet, keep listening instead
        // of sending the name to the brain, which answered it with made-up greetings.
        if (IsOnlyWakeWords(text))
        {
            ShowListening();
            BeginWaitingForCommand(session, (int)_conversationSettings.SpeechStartTimeout.TotalMilliseconds, "bare-wake");
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            EndSession(session);
            return;
        }

        // The scan hears only what comes AFTER the command, and not at once: the first two seconds are the owner's own phrase
        // ("Эдит, покажи ...") still settling in the buffer; it once cancelled the command it had just started.
        Volatile.Write(ref _scanFloor, Edith.Core.BargeScanWindow.FloorAfterCommand(Interlocked.Read(ref _totalSamples)));
        _voiceMode =
            VoiceMode.Busy;

        VoiceDiagnostics.VoiceEvent("listening-exit", $"session={session}; reason=speech-end");

        _interactionLatency?.Mark("speech-end", $"session={session}");
        _interactionLatency?.Mark("route-start");

        // The same recognizer covers Thinking and Speaking.  A repeated
        // "Эдит" is a new wake, not an input to the old command.
        _interrupt?.StartListening();

        VoiceDiagnostics.VoiceEvent("thinking-entered", $"session={session}");


        VoiceDiagnostics.WriteSnapshot("last-command.txt", $"session={session}", text);
        EdithEvents.Publish(EventKinds.CommandHeard, text, session,
            new Dictionary<string, string> { ["raw"] = rawText });


        _ =
            HandleCommandAsync(
                session,
                text);
    }


    private async Task HandleCommandAsync(
        long session,
        string text)
    {
        bool keepSessionOpen =
            false;

        try
        {
            if (!IsSessionActive(session))
                return;


            Dispatcher.Invoke(() =>
            {
                if (!IsSessionActive(session))
                    return;

                _overlay?.SetState(
                    Edith.Core.AssistantState.Thinking,
                    "Thinking",
                    session);
            });

            _interactionLatency?.Mark("route-started");
            VoiceDiagnostics.AppendInteractionTrace(session, "ROUTE INPUT", text);


            CancellationToken interactionToken = GetInteractionToken(session);
            string response;
            StreamingSpeech? speech = null;

            if (IsRepeatCommand(text))
            {
                response =
                    string.IsNullOrWhiteSpace(
                        _lastResponse)
                        ? "Пока нечего повторять."
                        : _lastResponse;
            }
            else
            {
                _response.CurrentSession = session;

                // The plain conversation answer is spoken as it is written; other routes are spoken whole.
                if (_tts is not null && _conversationSettings.StreamSpeech && !UiModes.Quiet)
                {
                    speech = new StreamingSpeech(
                        _tts,
                        interactionToken,
                        session,
                        onStart: first => EnterSpeaking(session, first),
                        onSentence: spoken => _interrupt?.SetEchoText(spoken));
                    _response.StreamTarget = speech;
                }

                try
                {
                    response =
                        await _response.HandleAsync(
                            text,
                            interactionToken);
                }
                finally
                {
                    _response.StreamTarget = null;
                }

                _interactionLatency?.Mark("route-complete", $"route={_response.LastRoute}");

                if (!string.IsNullOrWhiteSpace(
                        response))
                {
                    _lastResponse =
                        response;
                }
            }


            if (!IsSessionActive(session))
                return;


            if (string.IsNullOrWhiteSpace(
                    response))
            {
                return;
            }

            string generatedResponse = response;
            VoiceDiagnostics.WriteSnapshot("response-generated.txt", $"session={session}", generatedResponse);
            VoiceDiagnostics.AppendInteractionTrace(session, "ROUTE", _response.LastRoute);
            VoiceDiagnostics.AppendInteractionTrace(session, "BRAIN OUTPUT", generatedResponse);

            response = VoiceText.ForSpeech(generatedResponse);

            // The answer is also written on the desktop (always in quiet mode, when the window is on otherwise).
            // Replies to the window's own commands ("убери окно" -> "Убрала.") stay spoken only: showing them brought the window back.
            if ((UiModes.WindowOn || UiModes.Quiet) && _response.LastRoute != "UiMode")
            {
                string asked = text;
                string shown = generatedResponse;
                Dispatcher.BeginInvoke(() => Answers().ShowAnswer(asked, shown));

                // An answer that names a picture file on this PC shows it too.
                if (AnswerImages.PathIn(generatedResponse) is { } picture)
                    _ = Task.Run(async () =>
                    {
                        (byte[]? bytes, _) = await AnswerImages.LoadAsync(picture, CancellationToken.None);
                        if (bytes is not null)
                            AnswerImages.Show(bytes, Path.GetFileName(picture));
                    });
            }

            _interactionLatency?.Mark("tts-input");


            VoiceDiagnostics.WriteSnapshot("last-response.txt", $"session={session}", response);
            VoiceDiagnostics.WriteSnapshot("response-spoken.txt", $"session={session}", response);
            VoiceDiagnostics.AppendInteractionTrace(session, "TTS TEXT", response);
            EdithEvents.Publish(EventKinds.ResponseSpoken, response, session,
                new Dictionary<string, string> { ["route"] = _response.LastRoute });


            if (UiModes.Quiet)
            {
                // Quiet mode: the answer is only written.
            }
            else if (speech is { Started: true })
            {
                // Already speaking sentence by sentence: wait until the last one is out.
                await speech.FinishAsync();
            }
            else
            {
                EnterSpeaking(session, response);

                if (_tts is not null &&
                    IsSessionActive(session))
                {
                    await _tts.SpeakAsync(
                        response,
                        interactionToken,
                        session);
                }
            }

            // ZERO-GAP REARM
            //
            // Звук уже закончился.
            // Сразу начинаем принимать новый wake,
            // не дожидаясь finally/скрытия UI.
            if (IsSessionActive(session))
            {
                _interrupt?
                    .StopListening();


                // EDITH_PERMISSION_FOLLOWUP
                //
                // Только если EDITH сама запросила разрешение,
                // оставляем эту session открытой и принимаем
                // короткий ответ БЕЗ повторного wake-word.
                if (PermissionManager.Shared.HasPending)
                {
                    _permissionFollowUpActive =
                        true;


                    BeginWaitingForCommand(
                        session,
                        7000,
                        "permission");


                    keepSessionOpen =
                        true;


                    VoiceDiagnostics.WriteSnapshot("permission-followup.txt", $"session={session}", "waiting=7000ms");


                    ShowListening();

                    return;
                }


                _permissionFollowUpActive =
                    false;

                // She asked the owner something ("Хотите услышать продолжение?"): listen for the
                // answer without "Эдит" for a few seconds instead of going deaf at once.
                if (Edith.Core.Brain.ReplyPolish.AsksForAnswer(response) &&
                    _conversationSettings.AnswerFollowUp)
                {
                    BeginWaitingForCommand(session, 9000, "question");
                    keepSessionOpen = true;
                    ShowListening();
                    return;
                }

                _wake?
                    .Reset();


                _voiceMode =
                    VoiceMode.WaitingWake;
            }
        }
        catch (TaskCanceledException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (IsSessionActive(session))
            {
                VoiceDiagnostics.WriteRaw("response-error.txt", ex.ToString());
                EdithEvents.Publish(EventKinds.Error, "Ошибка при ответе: " + ex.Message, session);
            }
        }
        finally
        {
            // Если barge-in уже создал новую session,
            // старый ответ ничего не закроет.
            if (!keepSessionOpen)
            {
                EndSession(session);
            }
        }
    }


    private bool IsSessionActive(
        long session)
    {
        return session != 0 &&
               Volatile.Read(
                   ref _activeSession) ==
               session;
    }


    private void StartCommandTimeout(
        long session,
        int timeoutMilliseconds)
    {
        CancelCommandTimeout();

        _commandTimeout =
            new CancellationTokenSource();

        var token =
            _commandTimeout.Token;


        _ =
            Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(
                            timeoutMilliseconds,
                            token);

                        if (token.IsCancellationRequested)
                            return;

                        if (!IsSessionActive(session))
                            return;

                        if (_voiceMode ==
                            VoiceMode.WaitingCommand)
                        {
                            VoiceDiagnostics.VoiceEvent(
                                "listening-exit",
                                $"session={session}; reason=no-speech; timeout={timeoutMilliseconds}ms");

                            _command?
                                .CancelSession();

                            if (_turnPrefix.Length > 0)
                            {
                                // Silence after the dangling word: it was the whole command.
                                string whole = _turnPrefix;
                                _turnPrefix = "";
                                BeginCommand(session, whole);
                                return;
                            }

                            EndSession(
                                session);
                        }
                    }
                    catch (
                        OperationCanceledException)
                    {
                    }
                });
    }


    private void CancelCommandTimeout()
    {
        try
        {
            _commandTimeout?.Cancel();

            _commandTimeout?.Dispose();
        }
        catch
        {
        }

        _commandTimeout =
            null;
    }


    private void EndSession(
        long session,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (session == 0)
            return;

        EdithEvents.Publish("voice.session-end", $"сессия {session} закончена ({caller}, режим {_voiceMode})", session);

        _turnPrefix = "";
        Interlocked.Increment(ref _turnHoldVersion);


        long previous =
            Interlocked.CompareExchange(
                ref _activeSession,
                0,
                session);


        if (previous != session)
            return;


        // EDITH_PERMISSION_SESSION_CLEANUP
        //
        // Если окно подтверждения закрылось без ответа,
        // pending action больше не должен оставаться вооружённым.
        if (_permissionFollowUpActive &&
            PermissionManager.Shared.HasPending)
        {
            PermissionManager.Shared.Deny();
        }


        _permissionFollowUpActive =
            false;

        // The session closed without an answer to the reminder: ask again in a few minutes.
        _runtime?.Reminders.ExpireAck();


        CancelCommandTimeout();


        _interrupt?
            .StopListening();

        _command?
            .CancelSession();

        _wake?
            .Reset();

        // A single post-response Vosk error "едет" is accepted only in this
        // narrow window. It keeps ordinary phrases such as "едет дождь" out
        // of the global wake vocabulary.
        Interlocked.Exchange(
            ref _weakRewakeUntilTicks,
            Environment.TickCount64 + WeakRewakeWindowMs);


        _voiceMode =
            VoiceMode.WaitingWake;

        lock (_interactionGate)
        {
            if (_interactionCancellationSession == session)
            {
                _interactionCancellation?.Cancel();
                _interactionCancellation?.Dispose();
                _interactionCancellation = null;
                _interactionCancellationSession = 0;
            }
        }

        VoiceDiagnostics.VoiceEvent("sleeping-rearm", $"session={session}");
        _interactionLatency?.Mark("interaction-complete", $"session={session}");
        if (_interactionLatency is { } timeline)
        {
            // One compact record per interaction: where the time went (wake → answer → sound).
            EdithEvents.Publish("voice.timing", $"сессия {session}", session,
                new Dictionary<string, string>(timeline.Stages()));
        }
        LogWakePipeline("sleeping-rearm", "session-complete");


        Dispatcher.BeginInvoke(() =>
        {
            if (_overlay is null)
                return;

            // A newer wake may already own the UI. Never let a stale
            // completion hide the newer session's orb.
            if (Volatile.Read(ref _activeSession) != 0)
                return;

            _overlay.SetState(
                Edith.Core.AssistantState.Sleeping,
                "WaitingWake",
                session);
        });
    }


    private void ShowListening()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _overlay?.SetState(
                Edith.Core.AssistantState.Listening,
                "Listening",
                Volatile.Read(ref _activeSession));
            _interactionLatency?.Mark("listening-rendered", $"session={Volatile.Read(ref _activeSession)}");
        });
    }

    private void CancelActiveInteraction(string reason)
    {
        long session = Volatile.Read(ref _activeSession);
        if (session == 0)
            return;

        VoiceDiagnostics.VoiceEvent("listening-exit", $"session={session}; reason=cancel; trigger={reason}");
        _tts?.Stop();
        EndSession(session);
    }

    private void ProjectWaitingWake(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_voiceMode != VoiceMode.WaitingWake)
                return;

            _overlay?.SetState(
                Edith.Core.AssistantState.Sleeping,
                "WaitingWake",
                0);
            VoiceDiagnostics.VoiceEvent("ui-state-projected", $"backend=WaitingWake; reason={reason}");
        });
    }


    private bool TryExtractWakeCommand(
        string phrase,
        out string command)
    {
        if (_wake is not null)
        {
            return _wake.TryExtractCommand(
                phrase,
                out command);
        }

        command =
            string.Empty;

        return false;
    }

    private CancellationToken GetInteractionToken(long session)
    {
        lock (_interactionGate)
        {
            return _interactionCancellationSession == session &&
                   _interactionCancellation is not null
                ? _interactionCancellation.Token
                : new CancellationToken(canceled: true);
        }
    }

    private bool TryAcceptPostInteractionWeakWake(string phrase, out long session)
    {
        session = 0;
        if (_wake?.Engine == WakeEngineKind.Hybrid)
            return false;

        long until = Volatile.Read(ref _weakRewakeUntilTicks);
        if (until == 0 || Environment.TickCount64 > until ||
            !WakePhraseMatcher.IsIsolatedWeakWake(phrase))
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref _weakRewakeUntilTicks, 0, until) != until)
            return false;

        session = StartSession();
        LogWakePipeline("wake-accepted", "post-interaction-isolated-vosk-weak-token");
        return true;
    }

    private void LogWakePipeline(string stage, string reason)
    {
        try
        {
            WakeDetectorStatus? detector = _wake?.DetectorStatus;
            WakeDetectorStatus? vosk = _wake?.VoskStatus;
            WakeHybridStatus? hybrid = _wake?.HybridStatus;
            File.WriteAllText(
                Path.Combine(VoiceDiagnostics.Directory, "wake-conversation.txt"),
                DateTimeOffset.Now.ToString("O") + Environment.NewLine +
                $"stage={stage}" + Environment.NewLine +
                $"reason={reason}" + Environment.NewLine +
                $"state={_voiceMode}" + Environment.NewLine +
                $"session={Volatile.Read(ref _activeSession)}" + Environment.NewLine +
                $"audio-frames={Interlocked.Read(ref _audioFramesReceived)}" + Environment.NewLine +
                $"detector-engine={detector?.Engine ?? "none"}" + Environment.NewLine +
                $"detector-armed={detector?.State == WakeDetectorState.Running}" + Environment.NewLine +
                $"detector-frames={detector?.FramesProcessed ?? 0}" + Environment.NewLine +
                $"detector-last-detection={detector?.LastDetectionAt:O}" + Environment.NewLine +
                $"vosk-armed={vosk?.State == WakeDetectorState.Running}" + Environment.NewLine +
                $"vosk-frames={vosk?.FramesProcessed ?? 0}" + Environment.NewLine +
                $"vosk-partial={_wake?.VoskPartial ?? ""}" + Environment.NewLine +
                $"vosk-final={_wake?.VoskFinal ?? ""}" + Environment.NewLine +
                $"hybrid-source={hybrid?.Source ?? "none"}" + Environment.NewLine +
                $"hybrid-reason={hybrid?.Reason ?? "none"}" + Environment.NewLine +
                $"vosk-token={hybrid?.VoskToken ?? ""}" + Environment.NewLine +
                $"strong-vosk={hybrid?.VoskAccepted ?? 0}" + Environment.NewLine +
                $"weak-vosk-custom={hybrid?.WeakVoskCustomAccepted ?? 0}" + Environment.NewLine +
                $"custom-only-rejected={hybrid?.CustomOnlyRejected ?? 0}" + Environment.NewLine +
                $"custom-max-score={hybrid?.CustomMaxScore ?? 0:F3}" + Environment.NewLine +
                $"custom-consecutive-hits={hybrid?.CustomConsecutiveHits ?? 0}" + Environment.NewLine +
                $"vad-speech-seen={hybrid?.VadSpeechSeen ?? false}" + Environment.NewLine +
                "wake-recognizer-reset=" + (stage is "wake-rearm" or "sleeping-rearm"));
        }
        catch
        {
        }
    }


    private void FeedHybridBargeWake(
        float[] samples,
        int count)
    {
        // Not only the hybrid engine: with V3 "Эдит" said over her own voice used to go unheard.
        if (_wake?.Engine is WakeEngineKind.Hybrid or WakeEngineKind.CustomV3 or WakeEngineKind.Custom)
        {
            _wake.AcceptWakeAudio(
                samples,
                count,
                _microphone?.IsSpeech ?? false);
        }
    }


    /// <summary>
    /// Background learning only while nothing else happens: EDITH asleep, no session, the brain
    /// on a free GPU and no game (it must never cost the user an answer or frames).
    /// </summary>
    private async Task DistillMemoryAsync()
    {
        Edith.Core.Resources.ResourcePlan? plan = _runtime?.Resources.Current;
        // With the cloud up the work needs no GPU; the local model needs a free card and no game.
        bool cloud = GigaChatClient.Shared.IsReady(CloudSettings.Load());
        if (_distiller is null ||
            _voiceMode != VoiceMode.WaitingWake ||
            Volatile.Read(ref _activeSession) != 0 ||
            (!cloud && plan is not { Brain: Edith.Core.Resources.BrainPlacement.Gpu, GameActive: false }))
        {
            return;
        }

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await _distiller.RunOnceAsync(timeout.Token);
            if (_episodes is not null)
                await _episodes.RunOnceAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("memory-distiller-error.txt", ex.ToString());
        }
    }


    private void DeliverProactive()
    {
        try
        {
            if (_runtime is not null)
            {
                foreach (Edith.Core.Reminders.Reminder reminder in _runtime.Reminders.TakeDue())
                    _proactive.Enqueue((reminder.Spoken, reminder));
            }

            // Never talk over the user or over an answer in progress; wait for idle.
            if (_voiceMode != VoiceMode.WaitingWake ||
                Volatile.Read(ref _activeSession) != 0 ||
                Volatile.Read(ref _proactiveSpeaking) == 1 ||
                !_proactive.TryDequeue(out var item))
            {
                return;
            }

            _ = SpeakProactiveAsync(item.Text, item.Reminder);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("proactive-error.txt", ex.ToString());
        }
    }


    private async Task SpeakProactiveAsync(string text, Edith.Core.Reminders.Reminder? reminder)
    {
        Interlocked.Exchange(ref _proactiveSpeaking, 1);
        long session = StartSession();
        bool keepSessionOpen = false;

        try
        {
            _voiceMode = VoiceMode.Speaking;
            _interrupt?.StartListening();
            EdithEvents.Publish(EventKinds.ResponseSpoken, text, session,
                new Dictionary<string, string> { ["route"] = "proactive" });

            await Dispatcher.InvokeAsync(() =>
            {
                if (IsSessionActive(session))
                    _overlay?.SetState(Edith.Core.AssistantState.Speaking, "Proactive", session);
            });

            if (_tts is not null && IsSessionActive(session))
                await _tts.SpeakAsync(VoiceText.ForSpeech(text), GetInteractionToken(session), session);

            // A reminder waits for "хорошо": the answer needs no "Эдит". Silence repeats it later.
            if (reminder is not null && _runtime is not null && IsSessionActive(session))
            {
                _runtime.Reminders.BeginAwaitingAck(reminder);
                _interrupt?.StopListening();
                BeginWaitingForCommand(session, 7000, "reminder-ack");
                ShowListening();
                keepSessionOpen = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("proactive-error.txt", ex.ToString());
        }
        finally
        {
            if (!keepSessionOpen)
                EndSession(session);
            Interlocked.Exchange(ref _proactiveSpeaking, 0);
        }
    }


    /// <summary>
    /// The pause is short (about half a second); ask Smart Turn whether the phrase is finished.
    /// Yes: end now (faster than waiting a long pause). No: wait up to the hold time for more.
    /// </summary>
    private void BeginTurnDecision(long session)
    {
        int version = Volatile.Read(ref _turnHoldVersion);
        float[] audio = _recentAudio.Last(SmartTurnDetector.WindowSamples);
        SmartTurnDetector? detector = _turnDetector;
        double threshold = _conversationSettings.TurnCompleteThreshold;
        TimeSpan hold = _conversationSettings.TurnMaxHold;

        _ = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            float probability;
            try
            {
                probability = detector!.Predict(audio);
            }
            catch
            {
                probability = 1f;
            }

            bool complete = probability >= threshold;
            EdithEvents.Publish("voice.turn",
                complete ? $"договорил (p={probability:F2})" : $"не договорил, жду до {hold.TotalSeconds:0.0} с (p={probability:F2})",
                session,
                new Dictionary<string, string>
                {
                    ["p"] = probability.ToString("F3", System.Globalization.CultureInfo.InvariantCulture),
                    ["ms"] = clock.ElapsedMilliseconds.ToString(),
                });

            if (version != Volatile.Read(ref _turnHoldVersion) || _voiceMode != VoiceMode.CapturingCommand || !IsSessionActive(session))
                return; // the user spoke again meanwhile

            if (complete)
            {
                HandleSpeechEnded(forced: true);
                return;
            }

            _turnHoldTimer?.Dispose();
        _bargeScanTimer?.Dispose();
        _micWatchdog?.Dispose();
        _echoReference?.Dispose();
        s_embedder?.Dispose();
            _turnHoldTimer = new Timer(_ =>
            {
                if (version == Volatile.Read(ref _turnHoldVersion) && _voiceMode == VoiceMode.CapturingCommand && IsSessionActive(session))
                    HandleSpeechEnded(forced: true);
            }, null, hold, Timeout.InfiniteTimeSpan);
        });
    }


    private void ShowStatusPanel()
    {
        if (_runtime is null)
            return;

        _statusWindow ??= new StatusWindow(_runtime, _response?.Memory as Edith.Core.Memory.SqliteMemoryStore, Restart);
        _statusWindow.Show();
        _statusWindow.Activate();
    }


    /// <summary>One interface: settings are a tab of the main window.</summary>
    private void ShowSettings()
    {
        if (_runtime is null)
            return;

        ShowStatusPanel();
        _statusWindow?.Open("Настройки");
    }


    /// <summary>Starts a fresh EDITH that waits for this one to exit, then exits.</summary>
    private void Restart()
    {
        try
        {
            if (Environment.ProcessPath is { } exe)
                Process.Start(new ProcessStartInfo(exe, "--restart") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! });
        }
        catch (Exception ex)
        {
            EdithEvents.Publish(EventKinds.Error, "Не удалось перезапустить: " + ex.Message);
            return;
        }

        Shutdown();
    }


    private void ToggleStatusPanel()
    {
        if (_runtime is null)
            return;

        if (_statusWindow is { IsVisible: true })
        {
            _statusWindow.Hide();
            return;
        }

        _statusWindow ??= new StatusWindow(_runtime, _response?.Memory as Edith.Core.Memory.SqliteMemoryStore);
        _statusWindow.Show();
        _statusWindow.Activate();
    }


    /// <summary>GigaAM (config/stt.json) with Vosk as the safety net; Vosk alone if GigaAM cannot load.</summary>
    private ICommandRecognizer CreateCommandRecognizer(CommandRecognizer vosk)
    {
        SttSettings settings = SttSettings.Load(@"C:\EDITH\config\stt.json");
        if (!settings.Engine.Equals("gigaam", StringComparison.OrdinalIgnoreCase))
            return vosk;

        try
        {
            var giga = new GigaAmCommandRecognizer(settings);
            _sttEngine = GigaAmCommandRecognizer.EngineName;
            _giga = giga;
            _confirmWake = settings.ConfirmWake;
            return new HybridCommandRecognizer(giga, vosk);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("stt-error.txt", ex.ToString());
            EdithEvents.Publish(EventKinds.Error, "GigaAM не загрузился, распознаю через Vosk: " + ex.Message);
            return vosk;
        }
    }


    private IEnumerable<HealthCheck> CreateHealthChecks()
    {
        yield return new HealthCheck("stt", "Распознавание речи", _ => Task.FromResult(
            _command is null ? HealthResult.Failed("не создано")
            : _sttEngine == GigaAmCommandRecognizer.EngineName
                ? (_command is HybridCommandRecognizer { LastError: { } error }
                    ? HealthResult.Degraded("GigaAM: " + error + "; работает Vosk")
                    : HealthResult.Ok("GigaAM v3"))
                : HealthResult.Degraded("только Vosk")));

        yield return new HealthCheck("microphone", "Микрофон", _ => Task.FromResult(ProbeMicrophone()));

        yield return new HealthCheck("wake", "Слово «Эдит»", _ => Task.FromResult(
            _wake?.DetectorStatus is not { } status
                ? HealthResult.Failed("детектор не создан")
                : status.State switch
                {
                    WakeDetectorState.Running => HealthResult.Ok(status.Engine),
                    WakeDetectorState.Faulted => HealthResult.Failed(status.Engine + ": сбой"),
                    _ => HealthResult.Failed(status.Engine + ": остановлен"),
                }));

        yield return new HealthCheck("brain", "Локальная модель", async ct =>
        {
            Edith.Core.Brain.BrainSettings settings = LoadBrainSettings();
            return settings.Enabled
                ? await SystemProbes.ProbeOllamaAsync(settings.Endpoint, ct)
                : HealthResult.Degraded("выключена в настройках");
        });

        yield return new HealthCheck("secrets", "Секреты", _ => Task.FromResult(
            Edith.Speech.LocalSecrets.IsPlainText(@"C:\EDITH\secrets\yandex-speechkit-api-key.txt")
                ? HealthResult.Degraded("ключ Yandex лежит открытым текстом — запусти scripts\\Protect-Secrets.ps1")
                : HealthResult.Ok()));

        yield return new HealthCheck("disk", "Диск", _ => Task.FromResult(SystemProbes.ProbeDisk(@"C:\EDITH")));

        yield return new HealthCheck("models", "Файлы моделей", _ => Task.FromResult(SystemProbes.ProbeFiles(
            @"C:\EDITH\models\wake\vosk-model-small-ru-0.22",
            @"C:\EDITH\config\wake.json",
            @"C:\EDITH\config\brain.json")));

        yield return new HealthCheck("journal", "Журнал действий", _ => Task.FromResult(
            _runtime?.Journal is not { } journal
                ? HealthResult.Failed("не открылся")
                : journal.FailedCount > 0
                    ? HealthResult.Degraded($"не записано событий: {journal.FailedCount}")
                    : HealthResult.Ok()));
    }


    private HealthResult ProbeMicrophone()
    {
        if (_microphone is null)
            return HealthResult.Failed("не запущен");

        long frames = Interlocked.Read(ref _audioFramesReceived);
        long previous = Interlocked.Exchange(ref _healthLastAudioFrames, frames);

        return previous >= 0 && frames == previous
            ? HealthResult.Failed("нет звука с " + _microphone.DeviceName)
            : HealthResult.Ok(_microphone.DeviceName);
    }


    private static Edith.Core.Brain.BrainSettings LoadBrainSettings()
    {
        string path = Environment.GetEnvironmentVariable("EDITH_BRAIN_CONFIG") is { Length: > 0 } custom
            ? custom
            : @"C:\EDITH\config\brain.json";

        try
        {
            return Edith.Core.Brain.BrainSettingsLoader.Parse(File.Exists(path) ? File.ReadAllText(path) : null);
        }
        catch
        {
            return new Edith.Core.Brain.BrainSettings();
        }
    }


    protected override void OnExit(
        ExitEventArgs e)
    {
        // A proper exit: the watchdog must not take it for a crash.
        WriteRunState("clean");
        CancelCommandTimeout();


        Interlocked.Exchange(
            ref _activeSession,
            0);

        lock (_interactionGate)
        {
            _interactionCancellation?.Cancel();
            _interactionCancellation?.Dispose();
            _interactionCancellation = null;
            _interactionCancellationSession = 0;
        }


        _tts?.Stop();


        if (_microphone is not null)
        {
            _microphone.SamplesReady -=
                OnSamplesReady;

            _microphone.SpeechStarted -=
                OnSpeechStarted;

            _microphone.SpeechEnded -=
                OnSpeechEnded;
        }


        if (_wake is not null)
        {
            _wake.WakeDetected -=
                OnWakeDetected;
        }


        if (_interrupt is not null)
        {
            _interrupt.InterruptDetected -=
                OnInterruptDetected;
        }


        _interrupt?.Dispose();

        _microphone?.Dispose();
        _wake?.Dispose();
        _command?.Dispose();

        _speechModel?.Dispose();

        _tts?.Dispose();

        _proactiveTimer?.Dispose();
        _distillerTimer?.Dispose();
        _pulseService?.Dispose();
        _librarian?.Dispose();
        _tray?.Dispose();
        _turnHoldTimer?.Dispose();
        _turnDetector?.Dispose();
        _settingsWindow?.Close();
        _statusWindow?.Shutdown();
        _runtime?.Dispose();
        AppInstance.Release();

        // A stuck foreground thread must not keep a "closed" EDITH holding its files.
        new Thread(() => { Thread.Sleep(5000); Environment.Exit(0); }) { IsBackground = true }.Start();


        base.OnExit(e);
    }
}
