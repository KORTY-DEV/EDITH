namespace Edith.Audio.Wake;

// Существующий Vosk wake (WakeWordDetector + WakePhraseMatcher),
// завёрнутый в общий контракт IWakeWordDetector.
// Логика распознавания не менялась.
public sealed class VoskWakeDetector : IWakeWordDetector
{
    public const string EngineName =
        "vosk";

    private readonly IWakeUtterance _utterance;

    private WakeDetectorState _state =
        WakeDetectorState.Stopped;

    private long _frames;
    private DateTimeOffset? _lastDetectionAt;
    private bool _disposed;

    public event Action<WakeDetection>? WakeDetected;

    // Vosk-детектор не имеет собственного fault-режима:
    // исключения Vosk обрабатывает вызывающий код как раньше.
    public event Action<Exception>? Faulted
    {
        add { }
        remove { }
    }

    public VoskWakeDetector(
        IWakeUtterance utterance)
    {
        _utterance =
            utterance ??
            throw new ArgumentNullException(
                nameof(utterance));

        _utterance.WakeWordDetected +=
            OnUtteranceWake;
    }

    public string Engine =>
        EngineName;

    public WakeDetectorStatus Status =>
        new(
            EngineName,
            _state,
            null,
            Interlocked.Read(ref _frames),
            0,
            _lastDetectionAt);


    public void Start()
    {
        if (_disposed)
            return;

        _utterance.WakeEventsEnabled =
            true;

        _state =
            WakeDetectorState.Running;
    }

    public void Stop()
    {
        if (_state ==
            WakeDetectorState.Running)
        {
            _state =
                WakeDetectorState.Stopped;
        }
    }

    public void Reset()
    {
        if (_disposed)
            return;

        _utterance.Reset();
    }

    public void AcceptAudio(
        ReadOnlySpan<float> samples)
    {
        if (_disposed ||
            _state !=
            WakeDetectorState.Running)
        {
            return;
        }

        Interlocked.Increment(ref _frames);

        _utterance.AcceptAudio(
            samples);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        _state =
            WakeDetectorState.Stopped;

        // Сам _utterance принадлежит владельцу
        // (WakeRuntime), здесь только отписка.
        _utterance.WakeWordDetected -=
            OnUtteranceWake;
    }


    private void OnUtteranceWake(
        string observed)
    {
        if (_disposed ||
            _state !=
            WakeDetectorState.Running)
        {
            return;
        }

        DateTimeOffset now =
            DateTimeOffset.Now;

        _lastDetectionAt =
            now;

        WakeDetected?.Invoke(
            new WakeDetection(
                EngineName,
                1.0f,
                observed,
                now));
    }
}
