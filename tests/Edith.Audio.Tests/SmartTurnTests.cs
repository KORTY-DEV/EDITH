using System.Globalization;
using Edith.Audio;

namespace Edith.Audio.Tests;

public sealed class SmartTurnTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static float[] Signal()
    {
        var x = new float[64000];
        for (int n = 0; n < x.Length; n++)
        {
            double t = n / 16000.0;
            x[n] = (float)(0.25 * Math.Sin(2 * Math.PI * 200 * t) +
                           0.12 * Math.Sin(2 * Math.PI * 1234.5 * t) * Math.Abs(Math.Sin(2 * Math.PI * 2.5 * t)));
        }

        return x;
    }

    [Fact]
    public void LogMel_MatchesHuggingFaceWhisperExtractor()
    {
        // Reference: WhisperFeatureExtractor(chunk_length=8, do_normalize=True), every 16th value.
        float[] expected = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Support", "smartturn-expected.txt"))
            .Select(l => float.Parse(l, CultureInfo.InvariantCulture)).ToArray();

        float[] mel = WhisperLogMel.Compute(Signal(), SmartTurnDetector.WindowSamples);

        Assert.Equal(80 * 800, mel.Length);
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
            worst = Math.Max(worst, Math.Abs(mel[i * 16] - expected[i]));
        output.WriteLine($"largest difference {worst}");
        Assert.True(worst < 0.01, $"largest difference {worst}");
    }

    [Fact]
    public void MissingModel_GivesNull() =>
        Assert.Null(SmartTurnDetector.TryCreate(@"C:\nope\missing.onnx"));

    [Fact]
    public void ShippedModel_AnswersAProbability()
    {
        const string path = @"C:\EDITH\models\smart-turn\smart-turn-v3.2-cpu.onnx";
        if (!File.Exists(path))
            return;

        using SmartTurnDetector detector = SmartTurnDetector.TryCreate(path)!;
        detector.Predict(Signal());
        // The fastest of three: one slow run on a machine busy with builds is not a slow model.
        long best = long.MaxValue;
        float p = 0;
        for (int i = 0; i < 3; i++)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            p = detector.Predict(Signal());
            best = Math.Min(best, clock.ElapsedMilliseconds);
        }

        output.WriteLine($"p={p:F3} best {best} ms");
        Assert.InRange(p, 0f, 1f);
        Assert.True(best < 500, "end-of-turn check must stay far below a pause length");
    }
}
