namespace Edith.Audio.Wake;

public enum WakeDetectorState
{
    Stopped,
    Running,
    Faulted
}


public readonly record struct WakeDetection(
    string Engine,
    float Score,
    string Text,
    DateTimeOffset At)
{
    // Для hybrid: vosk | custom | both.  Engine сохранён для
    // обратной совместимости существующих consumer-ов.
    public string Source { get; init; } = Engine;
}


public sealed record WakeHybridStatus(
    string Source,
    long VoskAccepted,
    long CustomAccepted,
    long WeakVoskCustomAccepted,
    long CustomOnlyRejected,
    long BothObserved,
    long DuplicateSuppressed,
    float CustomMaxScore,
    int CustomConsecutiveHits,
    bool VadSpeechSeen,
    string VoskToken,
    string Reason);


public sealed record WakeDetectorStatus(
    string Engine,
    WakeDetectorState State,
    string? LastError,
    long FramesProcessed,
    float LastScore,
    DateTimeOffset? LastDetectionAt);


public sealed record WakeV3Status(
    long Stage1Candidates,
    long VerifierAccepted,
    long VerifierRejected,
    float LastStage1Score,
    float LastVerifierScore,
    bool LastDecision,
    string LastReason,
    double LastVerifierMilliseconds);

// Opt-in diagnostic copy of the streaming pipeline state for offline
// Python/C# parity checks. Production callers never request this snapshot.
public sealed record WakeParitySnapshot(
    long FramesProcessed,
    int PendingSamples,
    float[] RawWindow,
    float[] MelBuffer,
    float[] Features,
    float[] MelOutput,
    float[] EmbeddingOutput,
    float Score,
    float VerifierScore,
    bool VerifierCandidate);


// Общий контракт wake-детектора.
//
// Аудио: mono float [-1..1], 16 kHz, последовательные samples
// (тот же поток MicrophoneMonitor, что и у остального EDITH).
// AcceptAudio НЕ хранит ссылку на переданный span.
//
// WakeDetected вызывается синхронно из AcceptAudio
// (в потоке аудио-pump), вне внутренних lock.
public interface IWakeWordDetector : IDisposable
{
    string Engine { get; }

    WakeDetectorStatus Status { get; }

    event Action<WakeDetection>? WakeDetected;

    // Неисправимая ошибка в рабочем режиме
    // (детектор переходит в Faulted и больше не работает).
    event Action<Exception>? Faulted;

    void Start();

    void Stop();

    // Сбрасывает накопленное аудио-состояние и hit-счётчики.
    // Не сбрасывает cooldown (защита от повтора одного произнесения).
    void Reset();

    void AcceptAudio(
        ReadOnlySpan<float> samples);
}
