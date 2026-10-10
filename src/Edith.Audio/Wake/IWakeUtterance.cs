namespace Edith.Audio.Wake;

// Vosk-распознаватель начала фразы ("эдит открой блокнот").
//
// Vosk-режим: он же и есть wake-детектор (WakeEventsEnabled = true).
// Custom-режим: только транскрибирует произнесение, которое
//               уже подтвердила ML-модель (WakeEventsEnabled = false).
public interface IWakeUtterance : IDisposable
{
    bool WakeEventsEnabled { get; set; }

    event Action<string>? WakeWordDetected;

    void AcceptAudio(
        ReadOnlySpan<float> samples);

    string FinishUtterance();

    void Reset();
}
