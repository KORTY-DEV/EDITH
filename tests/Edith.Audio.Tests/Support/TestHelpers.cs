using Edith.Audio.Wake;

namespace Edith.Audio.Tests.Support;

public sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "edith-tests-" +
            Guid.NewGuid().ToString("N"));

    public TempDir() =>
        Directory.CreateDirectory(Path);

    public string File(string name) =>
        System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, true);
        }
        catch
        {
        }
    }
}


public static class Sig
{
    public static float[] Noise(
        double seconds,
        float amplitude = 1e-3f,
        int seed = 1)
    {
        var random = new Random(seed);

        var data =
            new float[(int)(seconds * 16000)];

        for (int i = 0; i < data.Length; i++)
        {
            data[i] =
                (float)(random.NextDouble() * 2 - 1) *
                amplitude;
        }

        return data;
    }

    // Громкий тон: для синтетических моделей это "wake".
    public static float[] Tone(
        double seconds,
        float amplitude = 0.5f)
    {
        var data =
            new float[(int)(seconds * 16000)];

        for (int i = 0; i < data.Length; i++)
        {
            data[i] =
                amplitude *
                (float)Math.Sin(
                    2 * Math.PI * 440 * i / 16000.0);
        }

        return data;
    }

    public static float[] Concat(
        params float[][] parts) =>
        parts.SelectMany(p => p).ToArray();

    // Подаёт аудио порциями по 1280 samples
    // (как pump микрофона, но с фиксированным размером).
    public static void Feed(
        IWakeWordDetector detector,
        float[] audio,
        int block = 1280)
    {
        for (int i = 0; i < audio.Length; i += block)
        {
            detector.AcceptAudio(
                audio.AsSpan(
                    i,
                    Math.Min(block, audio.Length - i)));
        }
    }
}


public sealed class SampleClock
{
    public long Samples;

    public long Ms =>
        Samples * 1000 / 16000;

    public Func<long> AsFunc() =>
        () => Ms;
}


public sealed class FakeUtterance : IWakeUtterance
{
    public bool WakeEventsEnabled { get; set; } = true;

    public event Action<string>? WakeWordDetected;

    public List<int> Accepted { get; } = [];

    public int Resets;
    public int Disposed;

    public string Phrase = "";

    public int TotalAcceptedSamples =>
        Accepted.Sum();

    public void AcceptAudio(
        ReadOnlySpan<float> samples) =>
        Accepted.Add(samples.Length);

    public string FinishUtterance() =>
        Phrase;

    public void Reset() =>
        Resets++;

    // Как настоящий WakeWordDetector: событие только
    // при включённых wake events.
    public void RaiseWake(string text)
    {
        if (WakeEventsEnabled)
            WakeWordDetected?.Invoke(text);
    }

    public void Dispose() =>
        Disposed++;
}


public sealed class RecordingDiagnostics : IWakeDiagnostics
{
    public List<string> Engines { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Statuses { get; } = [];
    public List<WakeDetection> Detections { get; } = [];
    public int Scores;

    public void ReportEngine(
        string engineLabel,
        string requested,
        string reason,
        IReadOnlyList<string> notes) =>
        Engines.Add(engineLabel);

    public void ReportModelStatus(string text) =>
        Statuses.Add(text);

    public void ReportScore(
        float score,
        float threshold) =>
        Scores++;

    public void ReportDetection(
        WakeDetection detection) =>
        Detections.Add(detection);

    public void ReportError(
        string context,
        string message) =>
        Errors.Add(context + ": " + message);

    public void Dispose()
    {
    }
}
