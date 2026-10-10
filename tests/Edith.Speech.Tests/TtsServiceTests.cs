using Edith.Speech;

namespace Edith.Speech.Tests;

public sealed class TtsServiceTests
{
    [Fact]
    public void SettingsLoader_ReadsJsonAndEnvironmentOverrides()
    {
        string path = Path.Combine(Path.GetTempPath(), $"edith-tts-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"provider\":\"windows\",\"voice\":\"alena\",\"speed\":0.8}");
        string? oldProvider = Environment.GetEnvironmentVariable("EDITH_TTS_PROVIDER");
        try
        {
            Environment.SetEnvironmentVariable("EDITH_TTS_PROVIDER", "yandex");
            TtsSettings settings = TtsSettingsLoader.Load(path);
            Assert.Equal("yandex", settings.Provider);
            Assert.Equal("alena", settings.Voice);
            Assert.Equal(0.8, settings.Speed);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EDITH_TTS_PROVIDER", oldProvider);
            File.Delete(path);
        }
    }

    [Fact]
    public void WindowsProvider_IsExplicitAndDoesNotClaimCloudVoice()
    {
        using var service = new HybridTtsService(new TtsSettings { Provider = "windows" });
        Assert.Contains("Windows fallback", service.SelectedVoice);
    }

    [Fact]
    public async Task Cancellation_IsPropagatedWithoutFallbackSpeech()
    {
        using var service = new HybridTtsService(new TtsSettings { Provider = "windows" });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SpeakAsync("тест", cancellation.Token));
    }

    [Fact]
    public void Pronunciation_IsProviderSpecific_AndDisplayWordStaysUntouched()
    {
        Assert.Contains("+Эдит", SpeechPronunciation.Apply("Позови Эдит.", "yandex"));
        Assert.Equal("Позови Эдит.", SpeechPronunciation.Apply("Позови Эдит.", "windows"));
        Assert.Contains("<speak>", SpeechPronunciation.Apply("Позови Эдит.", "local"));
        Assert.Contains("+Эдит", SpeechPronunciation.Apply("Позови Эдит.", "local"));
    }

    [Fact]
    public void SileroPronunciation_UsesRussianTechnicalSpellings()
    {
        string speech = SpeechPronunciation.Apply("NVIDIA GeForce RTX 5090, SSD и HDD. тестам.", "local");
        Assert.Contains("энвидиа джи форс эр ти икс пятьдесят девяносто", speech);
        Assert.Contains("эс эс ди", speech);
        Assert.Contains("эйч ди ди", speech);
        Assert.Contains("т+естам", speech);
        Assert.DoesNotContain("NVIDIA", speech);
    }

    [Theory]
    [InlineData("RTX 5090", "эр ти икс пятьдесят девяносто")]
    [InlineData("RTX 5080", "эр ти икс пятьдесят восемьдесят")]
    [InlineData("RTX 5070 Ti", "эр ти икс пятьдесят семьдесят ти-ай")]
    public void SileroPronunciation_KeepsGpuModelNumber(string display, string expected)
    {
        Assert.Contains(expected, SpeechPronunciation.Apply(display, "silero"));
    }

    [Fact]
    public void Chunker_PreservesOrder_AndDoesNotSplitWords()
    {
        IReadOnlyList<string> chunks = SileroTextChunker.Split("Первое предложение. Второе предложение! Третье; финал?", 24);
        Assert.Equal("Первое предложение.", chunks[0]);
        Assert.Equal("Второе предложение!", chunks[1]);
        Assert.Equal("Третье; финал?", chunks[2]);
        Assert.All(chunks, chunk => Assert.DoesNotContain("  ", chunk));
    }

    [Theory]
    [InlineData("1 000 рублей", "одна тысяча рублей")]
    [InlineData("45 990 рублей", "сорок пять тысяч девятьсот девяносто рублей")]
    [InlineData("150000 рублей", "сто пятьдесят тысяч рублей")]
    [InlineData("624 990 рублей", "шестьсот двадцать четыре тысячи девятьсот девяносто рублей")]
    [InlineData("1 250 000 рублей", "один миллион двести пятьдесят тысяч рублей")]
    public void CurrencyAmounts_AreSpelledForSpeechWithoutChangingDisplay(string display, string expected)
    {
        Assert.Equal(expected, RussianSpeechText.NormalizeCurrency(display));
        Assert.Contains(expected, SpeechPronunciation.Apply(display, "windows"));
    }

    [Theory]
    [InlineData("тест", "т+ест")]
    [InlineData("тесты", "т+есты")]
    [InlineData("тестам", "т+естам")]
    [InlineData("тестах", "т+естах")]
    [InlineData("тестами", "т+естами")]
    public void SileroPronunciation_UsesExplicitStressForTestWordForms(string display, string expected)
    {
        Assert.Contains(expected, SpeechPronunciation.Apply(display, "local"));
    }

    [Fact]
    public void Chunker_UsesOneRequestForShortReply()
    {
        string shortReply = new('а', 280);
        IReadOnlyList<string> chunks = SileroTextChunker.Split(shortReply);
        Assert.Single(chunks);
        Assert.Equal(shortReply, chunks[0]);
    }

    [Fact]
    public void Chunker_KeepsLongReplyInSentenceOrder()
    {
        string first = new string('а', 150) + ".";
        string second = new string('б', 150) + "!";
        string third = new string('в', 150) + "?";

        IReadOnlyList<string> chunks =
            SileroTextChunker.Split(first + " " + second + " " + third);

        Assert.Equal([first, second, third], chunks);
    }

    [Fact]
    public async Task ExplicitLocalProvider_DoesNotSelectYandex_AndFallsBackToWindowsWhenMissing()
    {
        using var service = new HybridTtsService(new TtsSettings
        {
            Provider = "local",
            LocalModelPath = Path.Combine(Path.GetTempPath(), "missing-silero-model.pt"),
            LocalPythonPath = Path.Combine(Path.GetTempPath(), "missing-python.exe")
        });

        await service.SpeakAsync("");
        Assert.Equal("windows", service.LastProvider);
        Assert.Contains("Windows fallback", service.SelectedVoice);
    }

    [Fact]
    public async Task MissingLocalWorker_WarmUpDoesNotBlockOrThrow()
    {
        using var service = new HybridTtsService(new TtsSettings
        {
            Provider = "local",
            LocalModelPath = Path.Combine(Path.GetTempPath(), "missing-silero-model.pt"),
            LocalPythonPath = Path.Combine(Path.GetTempPath(), "missing-python.exe")
        });

        await service.WarmUpAsync();
    }
}
