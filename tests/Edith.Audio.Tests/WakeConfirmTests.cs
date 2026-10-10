using Edith.Audio;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public sealed class WakeTranscriptConfirmerTests
{
    [Theory]
    [InlineData("эдит")]
    [InlineData("Эдит, открой блокнот")]
    [InlineData("эдиз")]
    [InlineData("эди")]
    [InlineData("едит")]
    [InlineData("слушай эдита")]
    public void ConfirmsWakeLikeWords(string heard)
    {
        Assert.True(WakeTranscriptConfirmer.Confirms(heard));
    }

    [Theory]
    [InlineData("")]
    [InlineData("едет дождь")]
    [InlineData("у лукоморья дуб зеленый")]
    [InlineData("что дева с трепетом любви")]
    [InlineData("это эти эдем")]
    [InlineData("видит")]
    public void RejectsOtherSpeech(string heard)
    {
        Assert.False(WakeTranscriptConfirmer.Confirms(heard));
    }
}

public sealed class AudioRingBufferTests
{
    [Fact]
    public void Last_ReturnsNewestSamplesInOrder_AcrossWrap()
    {
        var ring = new AudioRingBuffer(5);
        ring.Add([1, 2, 3], 3);
        ring.Add([4, 5, 6, 7], 4);

        Assert.Equal([3f, 4, 5, 6, 7], ring.Last(10));
        Assert.Equal([6f, 7], ring.Last(2));
    }
}

public sealed class WakeConfirmRealDataTests
{
    private static float[] Read(string wav)
    {
        using var reader = new NAudio.Wave.AudioFileReader(wav);
        var audio = new List<float>();
        var chunk = new float[4096];
        int read;
        while ((read = ((NAudio.Wave.ISampleProvider)reader).Read(chunk.AsSpan())) > 0)
            audio.AddRange(chunk.Take(read));
        return audio.ToArray();
    }

    [Fact]
    public void UserWakeClipsConfirm_PoemWindowsDoNot()
    {
        var settings = new SttSettings();
        string positives = @"C:\EDITH\data\wake-replacements\session-pos";
        string poem = Path.Combine(settings.ModelDirectory, "test_wavs", "example.wav");
        if (!Directory.Exists(positives) || !File.Exists(poem))
            return; // local data, not in git

        using var giga = new GigaAmCommandRecognizer(settings);

        foreach (string wav in Directory.GetFiles(positives, "*.wav"))
            Assert.True(WakeTranscriptConfirmer.Confirms(giga.Decode(Read(wav).Take(32000).ToArray()).NormalizedText), wav);

        float[] audio = Read(poem);
        for (int start = 0; start + 16000 < audio.Length; start += 8000)
        {
            string heard = giga.Decode(audio.Skip(start).Take(32000).ToArray()).NormalizedText;
            Assert.False(WakeTranscriptConfirmer.Confirms(heard), $"@{start}: {heard}");
        }
    }
}

public sealed class VerifiedRunPolicyTests
{
    [Fact]
    public void UnverifiedHitsCount_FirstVerifiedFrameFires()
    {
        var policy = new WakeDetectionPolicy(0.7f, 2, 850);

        Assert.False(policy.Evaluate(0.9f, 0, mayFire: false));
        Assert.True(policy.Evaluate(0.9f, 80, mayFire: true));
    }

    [Fact]
    public void NeverVerified_NeverFires_AndDropResetsTheRun()
    {
        var policy = new WakeDetectionPolicy(0.7f, 2, 850);

        Assert.False(policy.Evaluate(0.9f, 0, mayFire: false));
        Assert.False(policy.Evaluate(0.9f, 80, mayFire: false));
        Assert.False(policy.Evaluate(0.1f, 160));
        Assert.False(policy.Evaluate(0.9f, 240, mayFire: true));
        Assert.True(policy.Evaluate(0.9f, 320, mayFire: true));
    }
}

public sealed class CommandAfterWakeTests
{
    [Theory]
    [InlineData("эдит открой телеграм", "открой телеграм")]
    [InlineData("слушай эдит, закрой дискорд", "закрой дискорд")]
    [InlineData("эдит эдит какая погода", "какая погода")]
    [InlineData("эдиз напомни через час", "напомни через час")]
    [InlineData("эдит", "")]
    [InlineData("открой телеграм", "")]
    public void TakesWhatFollowsTheLastWakeWord(string heard, string command)
    {
        Assert.Equal(command, WakeTranscriptConfirmer.CommandAfterWake(heard));
    }
}

public sealed class UtteranceWakeTests
{
    [Theory]
    [InlineData("видет", true)]
    [InlineData("эдид", true)]
    [InlineData("эдит открой вк", true)]
    [InlineData("видит", false)]
    [InlineData("едет", false)]
    [InlineData("это", false)]
    [InlineData("видет машину", false)]
    [InlineData("смотри", false)]
    [InlineData("нет", false)]
    [InlineData("будет", false)]
    [InlineData("сидит", false)]
    [InlineData("что как в жизни делал эрди", false)]
    [InlineData("я вчера с эдит говорил про работу", false)]
    [InlineData("привет эдит", true)]
    [InlineData("слушай эдит открой музыку", true)]
    [InlineData("эди", true)]
    public void SingleNearWord_IsACall(string heard, bool expected)
    {
        Assert.Equal(expected, WakeTranscriptConfirmer.ConfirmsUtterance(heard));
    }
}
