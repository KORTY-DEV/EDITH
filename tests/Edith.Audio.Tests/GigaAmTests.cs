using Edith.Audio;

namespace Edith.Audio.Tests;

public sealed class HybridCommandRecognizerTests
{
    private sealed class Fake(string text, bool throws = false) : ICommandRecognizer
    {
        public int Samples;
        public void StartSession() { }
        public void CancelSession() { }
        public void AcceptAudio(float[] samples, int count) => Samples += count;
        public CommandTranscript FinishSessionDetailed() =>
            throws ? throw new InvalidOperationException("onnx failed") : new CommandTranscript(text, text, "fake", null);
        public void Dispose() { }
    }

    [Fact]
    public void PrimaryWins_BothHearAudio()
    {
        var giga = new Fake("открой дискорд");
        var vosk = new Fake("открой диско");
        var hybrid = new HybridCommandRecognizer(giga, vosk);

        hybrid.AcceptAudio(new float[160], 160);

        Assert.Equal("открой дискорд", hybrid.FinishSessionDetailed().NormalizedText);
        Assert.Equal((160, 160), (giga.Samples, vosk.Samples));
    }

    [Fact]
    public void EmptyOrFailingPrimary_FallsBackToVosk()
    {
        Assert.Equal("привет", new HybridCommandRecognizer(new Fake(""), new Fake("привет")).FinishSessionDetailed().NormalizedText);

        var failing = new HybridCommandRecognizer(new Fake("x", throws: true), new Fake("привет"));
        Assert.Equal("привет", failing.FinishSessionDetailed().NormalizedText);
        Assert.Equal("onnx failed", failing.LastError);
    }
}

public sealed class GigaAmModelTests
{
    [Fact]
    public void RealModel_TranscribesRussian_WhenInstalled()
    {
        var settings = new SttSettings();
        string wav = Path.Combine(settings.ModelDirectory, "test_wavs", "example.wav");
        if (!File.Exists(wav))
            return; // models/ is not in git; the check runs on the machine that has the model

        using var recognizer = new GigaAmCommandRecognizer(settings);
        using var reader = new NAudio.Wave.AudioFileReader(wav);
        var audio = new List<float>();
        var chunk = new float[4096];
        int read;
        while ((read = ((NAudio.Wave.ISampleProvider)reader).Read(chunk.AsSpan())) > 0)
            audio.AddRange(chunk.Take(read));

        recognizer.StartSession();
        float[] samples = audio.ToArray();
        recognizer.AcceptAudio(samples, samples.Length);
        CommandTranscript transcript = recognizer.FinishSessionDetailed();

        Assert.Equal(GigaAmCommandRecognizer.EngineName, transcript.Engine);
        Assert.Contains("у лукоморья дуб зеленый", transcript.NormalizedText);
    }

    [Fact]
    public void TooShortAudio_IsEmpty_NoDecode()
    {
        var settings = new SttSettings();
        if (!File.Exists(Path.Combine(settings.ModelDirectory, "model.int8.onnx")))
            return;

        using var recognizer = new GigaAmCommandRecognizer(settings);
        recognizer.StartSession();
        recognizer.AcceptAudio(new float[100], 100);

        Assert.Equal("", recognizer.FinishSessionDetailed().NormalizedText);
    }
}
