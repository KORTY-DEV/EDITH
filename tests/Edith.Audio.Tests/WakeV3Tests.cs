using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public sealed class WakeV3Tests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private OnnxWakeDetector Create(float verifierPivot)
    {
        OnnxTestModels.WriteValidSet(_dir.Path);
        File.WriteAllBytes(_dir.File("verifier.onnx"), OnnxTestModels.Classifier(pivot: verifierPivot));
        WakeModelSetReport report = WakeModelLoader.TryLoad(
            new WakeModelPaths(_dir.File("melspectrogram.onnx"), _dir.File("embedding_model.onnx"), _dir.File("edith.onnx")),
            out LoadedWakeModels? models);
        Assert.True(report.AllValid, report.Describe());
        return new OnnxWakeDetector(models!, new WakeDetectionPolicy(0.7f, 2, 800), null, null,
            new OnnxWakeVerifier(_dir.File("verifier.onnx"), 16, 0.75f));
    }

    [Fact]
    public void VerifierRejectsStage1Candidate_WithoutOpeningWake()
    {
        using var detector = Create(verifierPivot: 100_000);
        int wakes = 0;
        detector.WakeDetected += _ => wakes++;
        detector.Start();
        Sig.Feed(detector, Sig.Concat(Sig.Noise(1), Sig.Tone(1.0), Sig.Noise(1)));

        WakeV3Status status = Assert.IsType<WakeV3Status>(detector.V3Status);
        Assert.Equal(0, wakes);
        Assert.True(status.Stage1Candidates > 0);
        Assert.Equal(0, status.VerifierAccepted);
        Assert.True(status.VerifierRejected > 0);
    }

    [Fact]
    public void VerifierAccepts_AndRetainsNoDuplicateCooldownContract()
    {
        using var detector = Create(verifierPivot: 100);
        int wakes = 0;
        detector.WakeDetected += _ => wakes++;
        detector.Start();
        Sig.Feed(detector, Sig.Concat(Sig.Noise(1), Sig.Tone(2.0), Sig.Noise(2)));

        WakeV3Status status = Assert.IsType<WakeV3Status>(detector.V3Status);
        Assert.Equal(1, wakes);
        Assert.True(status.VerifierAccepted >= 1); // verified once per stage-1 run, not per frame
        Assert.Equal("accepted", status.LastReason);
    }

    [Fact]
    public void ParitySnapshot_IsRestoredByReset()
    {
        using var detector = Create(verifierPivot: 100_000);
        detector.Start();
        WakeParitySnapshot baseline = detector.CaptureParitySnapshot();

        Sig.Feed(detector, new float[WakeContract.ChunkSamples]);
        WakeParitySnapshot changed = detector.CaptureParitySnapshot();
        Assert.Equal(1, changed.FramesProcessed);
        Assert.NotEqual(baseline.Score, changed.Score);

        detector.Reset();
        WakeParitySnapshot restored = detector.CaptureParitySnapshot();
        Assert.Equal(baseline.Score, restored.Score);
        Assert.Equal(baseline.RawWindow, restored.RawWindow);
        Assert.Equal(baseline.MelBuffer, restored.MelBuffer);
        Assert.Equal(baseline.Features, restored.Features);
    }
}
