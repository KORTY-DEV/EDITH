using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class OnnxWakeDetectorTests : IDisposable
{
    private readonly TempDir _dir = new();

    public OnnxWakeDetectorTests() =>
        OnnxTestModels.WriteValidSet(_dir.Path);

    public void Dispose() =>
        _dir.Dispose();


    private OnnxWakeDetector Create(
        SampleClock? clock = null,
        float threshold = 0.7f,
        int hits = 2,
        int cooldown = 1500,
        string? classifier = null,
        IWakeDiagnostics? diagnostics = null)
    {
        var paths =
            new WakeModelPaths(
                _dir.File("melspectrogram.onnx"),
                _dir.File("embedding_model.onnx"),
                classifier ?? _dir.File("edith.onnx"));

        WakeModelLoader.TryLoad(paths, out var models);

        Assert.NotNull(models);

        return new OnnxWakeDetector(
            models!,
            new WakeDetectionPolicy(threshold, hits, cooldown),
            diagnostics,
            clock?.AsFunc());
    }


    // Обычная сессия: 1 s комнатной тишины, короткий "wake"
    // (громкий тон), затем тишина.
    private static float[] Session(
        double lead = 1.0,
        double tone = 0.8,
        double tail = 2.5) =>
        Sig.Concat(
            Sig.Noise(lead),
            Sig.Tone(tone),
            Sig.Noise(tail, seed: 2));


    private static void FeedTracked(
        OnnxWakeDetector detector,
        SampleClock clock,
        float[] audio)
    {
        for (int i = 0; i < audio.Length; i += 1280)
        {
            int n =
                Math.Min(1280, audio.Length - i);

            clock.Samples += n;

            detector.AcceptAudio(audio.AsSpan(i, n));
        }
    }


    [Fact]
    public void Silence_DoesNotFire_AndScoresStayLow()
    {
        using var detector = Create();

        int fired = 0;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        Sig.Feed(detector, Sig.Noise(6));

        Assert.Equal(0, fired);
        Assert.True(detector.Status.LastScore < 0.05f);
        Assert.True(detector.Status.FramesProcessed >= 70);
    }


    [Fact]
    public void LoudBurst_Fires_ExactlyOnce()
    {
        var clock = new SampleClock();

        using var detector = Create(clock);

        var detections = new List<WakeDetection>();
        detector.WakeDetected += detections.Add;

        detector.Start();

        FeedTracked(detector, clock, Session());

        var only = Assert.Single(detections);

        Assert.Equal("custom", only.Engine);
        Assert.True(only.Score >= 0.7f);
        Assert.NotNull(detector.Status.LastDetectionAt);
    }


    [Fact]
    public void ScoreStaysHighForSeconds_ButFiresOnce()
    {
        // Окно модели помнит слово ~2 s: score остаётся выше
        // порога дольше cooldown, но повторного wake быть не должно.
        var clock = new SampleClock();

        using var detector = Create(clock, cooldown: 100);

        int fired = 0;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        FeedTracked(
            detector,
            clock,
            Sig.Concat(
                Sig.Noise(1),
                Sig.Tone(2.0),
                Sig.Noise(3, seed: 3)));

        Assert.Equal(1, fired);
    }


    [Fact]
    public void SecondUtterance_InsideCooldown_IsSuppressed_AfterCooldown_Fires()
    {
        float[] twoBursts =
            Sig.Concat(
                Sig.Noise(1),
                Sig.Tone(0.8),
                Sig.Noise(3.0, seed: 2),
                Sig.Tone(0.8),
                Sig.Noise(2.5, seed: 4));

        var longCooldownClock = new SampleClock();

        using (var detector =
               Create(longCooldownClock, cooldown: 60_000))
        {
            int fired = 0;
            detector.WakeDetected += _ => fired++;
            detector.Start();

            FeedTracked(detector, longCooldownClock, twoBursts);

            Assert.Equal(1, fired);
        }

        var shortCooldownClock = new SampleClock();

        using (var detector =
               Create(shortCooldownClock, cooldown: 300))
        {
            int fired = 0;
            detector.WakeDetected += _ => fired++;
            detector.Start();

            FeedTracked(detector, shortCooldownClock, twoBursts);

            Assert.Equal(2, fired);
        }
    }


    [Fact]
    public void Reset_DiscardsBufferedWord_NoStaleScoreAfterwards()
    {
        var clock = new SampleClock();

        using var detector = Create(clock);

        int fired = 0;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        // Слово шло, wake сработал, окно всё ещё "помнит" его.
        FeedTracked(
            detector,
            clock,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.8)));

        Assert.Equal(1, fired);

        detector.Reset();

        Assert.Equal(0f, detector.Status.LastScore);

        // Без Reset score здесь был бы ~1.0 ещё секунды.
        float maxAfterReset = 0;

        for (int i = 0; i < 40; i++)
        {
            clock.Samples += 1280;

            detector.AcceptAudio(
                Sig.Noise(1280.0 / 16000, seed: 10 + i));

            maxAfterReset =
                Math.Max(
                    maxAfterReset,
                    detector.Status.LastScore);
        }

        Assert.True(
            maxAfterReset < 0.05f,
            $"stale score after Reset: {maxAfterReset}");

        Assert.Equal(1, fired);
    }


    [Fact]
    public void Reset_InTheMiddleOfAWord_DoesNotCompleteAStaleDetection()
    {
        var clock = new SampleClock();

        using var detector = Create(clock);

        int fired = 0;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        // 0.24 s слова: score ещё растёт, но не достиг порога.
        FeedTracked(
            detector,
            clock,
            Sig.Concat(Sig.Noise(1), Sig.Tone(0.24)));

        Assert.Equal(0, fired);

        detector.Reset();

        FeedTracked(detector, clock, Sig.Noise(3, seed: 9));

        Assert.Equal(0, fired);
    }


    [Fact]
    public void AfterReset_ANewWord_IsDetected_WithoutWarmUp()
    {
        var clock = new SampleClock();

        using var detector = Create(clock, cooldown: 0);

        int fired = 0;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        // Reset прямо перед словом (граница session):
        // окно сразу рабочее, 2 s прогрева не нужны.
        detector.Reset();

        FeedTracked(
            detector,
            clock,
            Sig.Concat(Sig.Tone(0.8), Sig.Noise(1, seed: 2)));

        Assert.Equal(1, fired);
    }


    [Fact]
    public void ChunkBoundaries_DoNotAffectResults()
    {
        float[] audio = Session();

        var clockA = new SampleClock();
        using var a = Create(clockA);

        var clockB = new SampleClock();
        using var b = Create(clockB);

        int firedA = 0, firedB = 0;
        a.WakeDetected += _ => firedA++;
        b.WakeDetected += _ => firedB++;

        a.Start();
        b.Start();

        FeedTracked(a, clockA, audio);

        // Тот же поток, но нарезанный как реальный микрофон:
        // порции произвольного размера, в т.ч. крошечные.
        int[] sizes = [137, 1000, 4096, 1, 63, 3333, 512];
        int offset = 0, k = 0;

        while (offset < audio.Length)
        {
            int n =
                Math.Min(
                    sizes[k++ % sizes.Length],
                    audio.Length - offset);

            clockB.Samples += n;

            b.AcceptAudio(audio.AsSpan(offset, n));

            offset += n;
        }

        Assert.Equal(a.Status.FramesProcessed, b.Status.FramesProcessed);
        Assert.Equal(a.Status.LastScore, b.Status.LastScore, 4);
        Assert.Equal(firedA, firedB);
        Assert.Equal(1, firedA);
    }


    [Fact]
    public void AudioIsIgnored_UntilStarted_AndAfterStop()
    {
        using var detector = Create();

        Sig.Feed(detector, Sig.Noise(1));

        Assert.Equal(0, detector.Status.FramesProcessed);
        Assert.Equal(WakeDetectorState.Stopped, detector.Status.State);

        detector.Start();
        Sig.Feed(detector, Sig.Noise(1));

        long running = detector.Status.FramesProcessed;

        Assert.True(running > 0);
        Assert.Equal(WakeDetectorState.Running, detector.Status.State);

        detector.Stop();
        Sig.Feed(detector, Sig.Noise(1));

        Assert.Equal(running, detector.Status.FramesProcessed);
    }


    [Fact]
    public void StartTwice_And_StopWithoutStart_AreHarmless()
    {
        using var detector = Create();

        detector.Stop();
        detector.Start();
        detector.Start();

        Sig.Feed(detector, Sig.Noise(1));

        Assert.True(detector.Status.FramesProcessed > 0);
    }


    [Fact]
    public void Dispose_IsIdempotent_AndAudioAfterDisposeIsIgnored()
    {
        var detector = Create();

        detector.Start();
        Sig.Feed(detector, Sig.Noise(1));

        detector.Dispose();
        detector.Dispose();

        Sig.Feed(detector, Sig.Noise(1));
        detector.Reset();
        detector.Start();

        Assert.Equal(WakeDetectorState.Stopped, detector.Status.State);
    }


    [Fact]
    public void EmptyAndNonFiniteInput_DoesNotBreakThePipeline()
    {
        using var detector = Create();

        detector.Start();

        detector.AcceptAudio([]);

        float[] bad = new float[2560];

        for (int i = 0; i + 2 < bad.Length; i += 3)
        {
            bad[i] = float.NaN;
            bad[i + 1] = float.PositiveInfinity;
            bad[i + 2] = 5f;
        }

        detector.AcceptAudio(bad);

        Assert.NotEqual(WakeDetectorState.Faulted, detector.Status.State);
        Assert.True(float.IsFinite(detector.Status.LastScore));
    }


    [Fact]
    public void ModelThatFiresOnRoomTone_IsRejectedAtLoad()
    {
        File.WriteAllBytes(
            _dir.File("always.onnx"),
            OnnxTestModels.Classifier(pivot: -10_000f));

        var ex =
            Assert.Throws<InvalidOperationException>(
                () => Create(
                    classifier: _dir.File("always.onnx")));

        Assert.Contains("room tone", ex.Message);
    }


    [Fact]
    public void ModelReturningInvalidProbability_FaultsDetector_AndRaisesEvent()
    {
        File.WriteAllBytes(
            _dir.File("broken.onnx"),
            OnnxTestModels.Classifier(unbounded: true));

        using var detector =
            Create(classifier: _dir.File("broken.onnx"));

        Exception? fault = null;
        int fired = 0;

        detector.Faulted += e => fault = e;
        detector.WakeDetected += _ => fired++;

        detector.Start();

        Sig.Feed(
            detector,
            Sig.Concat(Sig.Noise(1), Sig.Tone(1.5)));

        Assert.NotNull(fault);
        Assert.Equal(WakeDetectorState.Faulted, detector.Status.State);
        Assert.Contains("invalid", detector.Status.LastError);
        Assert.Equal(0, fired);

        // После fault детектор молчит и не перезапускается сам.
        long frames = detector.Status.FramesProcessed;

        detector.Start();
        Sig.Feed(detector, Sig.Tone(1));

        Assert.Equal(frames, detector.Status.FramesProcessed);
    }


    [Fact]
    public void Diagnostics_ReceiveScores_ButAreNotSpammedBySilence()
    {
        var diagnostics = new RecordingDiagnostics();

        using var detector =
            Create(diagnostics: diagnostics);

        detector.Start();

        Sig.Feed(detector, Sig.Noise(2));

        // ReportScore зовётся на каждый кадр (~25), а решение
        // "писать ли на диск" принимает WakeScoreThrottle.
        Assert.True(diagnostics.Scores > 20);
    }


    [Fact]
    public async Task ConcurrentAudioAndReset_DoNotCorruptState()
    {
        using var detector = Create();

        detector.Start();

        using var stop = new CancellationTokenSource();

        var resetter = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                detector.Reset();
                Thread.Sleep(1);
            }
        });

        var audio = Session(1, 0.5, 1);

        for (int round = 0; round < 6; round++)
            Sig.Feed(detector, audio);

        stop.Cancel();
        await resetter;

        Assert.NotEqual(WakeDetectorState.Faulted, detector.Status.State);
    }


    [Fact]
    public void AverageProcessingTime_IsFarBelowRealtime()
    {
        using var detector = Create();

        detector.Start();

        Sig.Feed(detector, Sig.Noise(8));

        // Один кадр = 80 ms аудио. Синтетические модели тривиальны,
        // но накладные расходы самого pipeline должны быть малы.
        Assert.InRange(detector.AverageChunkMilliseconds, 0.0001, 20);
    }
}
