using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class SpeechNormalizerTests
{
    [Theory]
    [InlineData("На диске C: свободно 439 ГБ, на D: — 25 ГБ.", "На диске Си свободно четыреста тридцать девять гигабайт, на диск Ди — двадцать пять гигабайт.")]
    [InlineData("RAM — около 1,5 ГБ.", "оперативная память — около полтора гигабайта.")]
    [InlineData("Edith.App и Telegram используют больше ресурсов", "Эдит и Телеграм используют больше ресурсов")]
    [InlineData("Открываю VK Мессенджер.", "Открываю вэка Мэсэнджер.")]
    [InlineData("Температура 65°C", "Температура шестьдесят пять градусов")]
    [InlineData("Проверить READY FOR IG-1 и A-G", "Проверить реди фор ай джи один и эй джи")]
    [InlineData("Откройте Playwright и GitHub", "Откройте плейрайт и гитхаб")]
    [InlineData("свободно 2 ГБ", "Свободно два гигабайта")]
    [InlineData("открыла Opera GX", "Открыла Опера джи икс")]
    public void ForSpeech_SaysEverything(string text, string spoken)
    {
        Assert.Equal(spoken, VoiceText.ForSpeech(text));
    }

    [Theory]
    [InlineData("Palworld", "палворлд")]
    [InlineData("Termius", "термиус")]
    public void UnknownLatin_IsTransliterated(string word, string spoken)
    {
        Assert.Equal(spoken, SpeechNormalizer.Normalize(word));
    }

    [Fact]
    public void ShortCaps_AreSpelled()
    {
        Assert.Equal("эй ви", SpeechNormalizer.Normalize("AV"));
    }
}
