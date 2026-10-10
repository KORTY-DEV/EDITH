using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class WakePhraseExtractionTests
{
    [Theory]
    [InlineData("эдит открой блокнот", "открой блокнот")]
    [InlineData("едит открой блокнот", "открой блокнот")]
    [InlineData("эдит", "")]
    [InlineData("Эдит", "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    // Vosk ошибся в самом слове: wake подтверждён моделью.
    [InlineData("идет открой блокнот", "открой блокнот")]
    [InlineData("едет сколько времени", "сколько времени")]
    [InlineData("едет", "")]
    [InlineData("и открой блокнот", "открой блокнот")]
    // Vosk слово пропустил — команда сохраняется целиком.
    [InlineData("открой блокнот", "открой блокнот")]
    [InlineData("какая погода", "какая погода")]
    [InlineData("сколько оперативки", "сколько оперативки")]
    // Прочее
    [InlineData("слушай эдит открой блокнот", "открой блокнот")]
    [InlineData("эдит эдит открой блокнот", "открой блокнот")]
    [InlineData("ну эдит какая погода", "какая погода")]
    // Pre-roll захватил чужую речь перед wake.
    [InlineData("и там говорят эдит открой блокнот", "открой блокнот")]
    public void ConfirmedWake_SplitsWakeFromCommand(
        string phrase,
        string expected)
    {
        Assert.Equal(
            expected,
            WakePhraseMatcher
                .ExtractCommandAfterConfirmedWake(phrase));
    }


    [Fact]
    public void VoskTextMatcher_IsUnchanged()
    {
        Assert.True(
            WakePhraseMatcher.TryExtractCommand(
                "эдит открой блокнот",
                out string command));

        Assert.Equal("открой блокнот", command);

        // Weak text no longer opens a wake by itself; fusion permits only
        // the isolated live-observed token after custom confirmation.
        Assert.False(
            WakePhraseMatcher.TryFindWake(
                "едет",
                out _));

        Assert.False(
            WakePhraseMatcher.TryFindWake(
                "идет дождь",
                out _));

        Assert.True(WakePhraseMatcher.IsIsolatedWeakWake("едет"));
        Assert.False(WakePhraseMatcher.IsIsolatedWeakWake("едет дождь"));
    }
}


public class PreRollBufferTests
{
    private static float[] Ramp(int from, int count) =>
        Enumerable.Range(from, count)
            .Select(i => (float)i)
            .ToArray();


    [Fact]
    public void CopySince_ReturnsAudioFromAbsolutePosition()
    {
        var ring = new PreRollBuffer(100);

        ring.Write(Ramp(0, 60));

        var dest = new float[100];

        int n = ring.CopySince(20, dest);

        Assert.Equal(40, n);
        Assert.Equal(Ramp(20, 40), dest[..n]);
        Assert.Equal(60, ring.TotalWritten);
    }


    [Fact]
    public void WrapAround_KeepsOnlyNewestSamples_InOrder()
    {
        var ring = new PreRollBuffer(100);

        ring.Write(Ramp(0, 70));
        ring.Write(Ramp(70, 70));

        var dest = new float[100];

        int n = ring.CopySince(0, dest);

        Assert.Equal(100, n);
        Assert.Equal(Ramp(40, 100), dest[..n]);
    }


    [Fact]
    public void WriteLargerThanCapacity_KeepsTail()
    {
        var ring = new PreRollBuffer(50);

        ring.Write(Ramp(0, 130));

        var dest = new float[50];

        int n = ring.CopySince(0, dest);

        Assert.Equal(50, n);
        Assert.Equal(Ramp(80, 50), dest);
        Assert.Equal(130, ring.TotalWritten);
    }


    [Fact]
    public void SmallDestination_GetsNewestTail()
    {
        var ring = new PreRollBuffer(100);

        ring.Write(Ramp(0, 90));

        var dest = new float[10];

        int n = ring.CopySince(0, dest);

        Assert.Equal(10, n);
        Assert.Equal(Ramp(80, 10), dest);
    }


    [Fact]
    public void Clear_MakesOldAudioUnreachable_ButKeepsPositionsMonotonic()
    {
        var ring = new PreRollBuffer(100);

        ring.Write(Ramp(0, 60));

        ring.Clear();

        var dest = new float[100];

        Assert.Equal(0, ring.CopySince(0, dest));

        ring.Write(Ramp(60, 10));

        int n = ring.CopySince(0, dest);

        Assert.Equal(10, n);
        Assert.Equal(Ramp(60, 10), dest[..n]);
        Assert.Equal(70, ring.TotalWritten);
    }


    [Fact]
    public void StartInTheFuture_ReturnsNothing()
    {
        var ring = new PreRollBuffer(100);

        ring.Write(Ramp(0, 10));

        Assert.Equal(0, ring.CopySince(500, new float[10]));
    }


    [Fact]
    public void EmptyWrite_AndInvalidCapacity()
    {
        var ring = new PreRollBuffer(10);

        ring.Write([]);

        Assert.Equal(0, ring.TotalWritten);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PreRollBuffer(0));
    }


    [Fact]
    public async Task ConcurrentWriteAndClear_IsSafe()
    {
        var ring = new PreRollBuffer(1000);

        var writer = Task.Run(() =>
        {
            var block = new float[128];

            for (int i = 0; i < 5000; i++)
                ring.Write(block);
        });

        var clearer = Task.Run(() =>
        {
            var dest = new float[1000];

            for (int i = 0; i < 5000; i++)
            {
                ring.Clear();
                ring.CopySince(0, dest);
            }
        });

        await Task.WhenAll(writer, clearer);

        Assert.Equal(5000L * 128, ring.TotalWritten);
    }
}


public class WakeScoreThrottleTests
{
    [Fact]
    public void Silence_NeverWrites()
    {
        var throttle = new WakeScoreThrottle();

        for (int i = 0; i < 1000; i++)
        {
            Assert.False(
                throttle.TryReport(0.01f, i * 80L, out _));
        }
    }


    [Fact]
    public void Burst_WritesPeak_AtMostOncePerSecond_AndAtTheEnd()
    {
        var throttle = new WakeScoreThrottle();

        var written = new List<float>();

        long t = 0;

        // 3 s всплеска с пиком 0.95 посередине.
        for (int i = 0; i < 37; i++, t += 80)
        {
            float score =
                i == 18
                    ? 0.95f
                    : 0.4f;

            if (throttle.TryReport(score, t, out float peak))
                written.Add(peak);
        }

        // Конец всплеска.
        for (int i = 0; i < 5; i++, t += 80)
        {
            if (throttle.TryReport(0.01f, t, out float peak))
                written.Add(peak);
        }

        Assert.InRange(written.Count, 2, 5);
        Assert.Contains(0.95f, written);

        // После конца всплеска новых записей нет.
        for (int i = 0; i < 100; i++, t += 80)
        {
            Assert.False(
                throttle.TryReport(0.01f, t, out _));
        }
    }


    [Fact]
    public void PeakOfAFinishedBurst_DoesNotLeakIntoTheNextOne()
    {
        var throttle = new WakeScoreThrottle();

        long t = 0;

        // Всплеск с пиком 0.95, целиком записанный внутри всплеска.
        Assert.True(throttle.TryReport(0.95f, t, out float first));
        Assert.Equal(0.95f, first);

        // Конец всплеска.
        for (int i = 0; i < 10; i++)
            Assert.False(throttle.TryReport(0.01f, t += 80, out _));

        // Новый, слабый всплеск.
        t += 5000;

        Assert.True(throttle.TryReport(0.30f, t, out float second));
        Assert.Equal(0.30f, second);
    }


    [Fact]
    public void BurstEnd_WrittenLater_WhenHardLimitBlockedItFirst()
    {
        var throttle = new WakeScoreThrottle();

        Assert.True(throttle.TryReport(0.6f, 0, out _));

        // Пик внутри 250 ms после записи, затем сразу конец.
        Assert.False(throttle.TryReport(0.9f, 80, out _));
        Assert.False(throttle.TryReport(0.01f, 160, out _));

        // Как только лимит прошёл — итоговый пик записывается.
        Assert.True(throttle.TryReport(0.01f, 260, out float peak));
        Assert.Equal(0.9f, peak);
    }


    [Fact]
    public void HardLimit_NoMoreThanOneWritePer250ms()
    {
        var throttle = new WakeScoreThrottle();

        int writes = 0;

        // Дребезжащий score вокруг порога значимости.
        for (int i = 0; i < 200; i++)
        {
            float score =
                i % 2 == 0
                    ? 0.5f
                    : 0.02f;

            if (throttle.TryReport(score, i * 80L, out _))
                writes++;
        }

        // 200 * 80 ms = 16 s -> максимум 64 записи.
        Assert.True(writes <= 64, $"writes={writes}");
    }


    [Fact]
    public void NonFinite_IsIgnored()
    {
        var throttle = new WakeScoreThrottle();

        Assert.False(throttle.TryReport(float.NaN, 0, out _));
    }
}


public class WakeDiagnosticsTests
{
    [Fact]
    public void WritesAllFiles_WithExpectedContent()
    {
        using var dir = new TempDir();

        using (var diag = new WakeDiagnostics(dir.Path))
        {
            diag.ReportEngine(
                "vosk-fallback",
                "auto",
                "custom wake model not installed",
                ["config: x"]);

            diag.ReportModelStatus("models here");

            diag.ReportDetection(
                new WakeDetection(
                    "custom",
                    0.91f,
                    "edith",
                    DateTimeOffset.Now));

            diag.ReportError("ctx", "boom");

            diag.ReportScore(0.85f, 0.7f);
        }

        string engine =
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.Engine));

        Assert.Equal("vosk-fallback", engine.Split('\n')[0].Trim());
        Assert.Contains("requested=auto", engine);
        Assert.Contains("note=config: x", engine);

        Assert.Contains(
            "models here",
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.ModelStatus)));

        string detection =
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.LastDetection));

        Assert.Contains("engine=custom", detection);
        Assert.Contains("score=0", detection);

        Assert.Contains(
            "boom",
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.Error)));

        string score =
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.LastScore));

        Assert.Contains("threshold=0", score);
    }


    [Fact]
    public void ScoreWrites_AreThrottled_NotPerFrame()
    {
        using var dir = new TempDir();

        int ticks = 0;

        using var diag =
            new WakeDiagnostics(
                dir.Path,
                () => ticks * 80L);

        for (ticks = 0; ticks < 500; ticks++)
        {
            diag.ReportScore(0.02f, 0.7f);
        }

        diag.Flush();

        // Тишина не создаёт файл вообще.
        Assert.False(
            File.Exists(
                dir.File(WakeDiagnosticFiles.LastScore)));
    }


    [Fact]
    public void UnwritableDirectory_NeverThrows()
    {
        using var dir = new TempDir();

        // Путь под файлом: Directory.CreateDirectory упадёт.
        string blocker = dir.File("blocker");

        File.WriteAllText(blocker, "x");

        using var diag =
            new WakeDiagnostics(
                Path.Combine(blocker, "sub"));

        diag.ReportError("ctx", "boom");
        diag.ReportModelStatus("s");
        diag.Flush();
    }


    [Fact]
    public void Dispose_FlushesPendingWrites_AndIsIdempotent()
    {
        using var dir = new TempDir();

        var diag = new WakeDiagnostics(dir.Path);

        diag.ReportError("ctx", "last words");

        diag.Dispose();
        diag.Dispose();

        Assert.Contains(
            "last words",
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.Error)));

        // После Dispose Report* безопасны.
        diag.ReportError("ctx", "ignored");
    }


    [Fact]
    public void PendingQueue_IsBounded_OneEntryPerFile()
    {
        using var dir = new TempDir();

        using var diag = new WakeDiagnostics(dir.Path);

        for (int i = 0; i < 100_000; i++)
            diag.ReportError("ctx", "n=" + i);

        diag.Flush();

        Assert.Contains(
            "n=",
            File.ReadAllText(
                dir.File(WakeDiagnosticFiles.Error)));
    }
}
