using Edith.Speech;

namespace Edith.Speech.Tests;

public sealed class SpokenNumbersEdgeTests
{
    [Theory]
    [InlineData("будет -5 ночью", "будет минус пять ночью")]
    [InlineData("от -3 до +2", "от минус три до плюс два")]
    [InlineData("12.10.2026 вторник", "двенадцатого октября две тысячи двадцать шестого года вторник")]
    [InlineData("8 192 МБ", "восемь тысяч сто девяносто два мегабайта")]
    [InlineData("в 1990-х годах", "в тысяча девятьсот девяностых годах")]
    [InlineData("на 2-м этаже", "на втором этаже")]
    [InlineData("15-го числа", "пятнадцатого числа")]
    [InlineData("через 1,5 часа", "через полтора часа")]
    [InlineData("через 2,5 часа", "через два с половиной часа")]
    [InlineData("1,5 минуты", "полторы минуты")]
    [InlineData("стоит 199 999 ₽", "стоит сто девяносто девять тысяч девятьсот девяносто девять рублей")]
    public void Reads(string written, string spoken) => Assert.Equal(spoken, SpokenNumbers.Apply(written));

    [Theory]
    [InlineData("RTX 5090", "RTX пятьдесят девяносто")]
    [InlineData("GTX 1070 Ti", "GTX десять семьдесят Ti")]
    [InlineData("8b модель", "восемь b модель")]
    public void VideoCardsAndModelLetters(string written, string spoken) => Assert.Equal(spoken, SpokenNumbers.Apply(written));

    [Theory]
    [InlineData("5-7 штук")]
    [InlineData("версия v1.0.7")]
    [InlineData("что-то 3-4 раза")]
    public void RangesAndVersions_AreNotSigns(string text) => Assert.DoesNotContain("минус", SpokenNumbers.Apply(text));
}
