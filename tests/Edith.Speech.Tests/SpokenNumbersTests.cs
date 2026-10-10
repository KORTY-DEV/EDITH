using Edith.Speech;

namespace Edith.Speech.Tests;

public sealed class SpokenNumbersTests
{
    [Theory]
    [InlineData("в 2026 году", "в две тысячи двадцать шестом году")]
    [InlineData("с 2026 года", "с две тысячи двадцать шестого года")]
    [InlineData("10 октября", "десятого октября")]
    [InlineData("в 21:30", "в двадцать один тридцать")]
    [InlineData("Сейчас 00:55.", "Сейчас ноль ноль пятьдесят пять.")]
    [InlineData("+14 градусов", "плюс четырнадцать градусов")]
    [InlineData("-5 °C", "минус пять градусов")]
    [InlineData("14,5 градуса", "четырнадцать целых пять десятых градуса")]
    [InlineData("загрузка 5%", "загрузка пять процентов")]
    [InlineData("32 ГБ", "тридцать два гигабайта")]
    [InlineData("через 2 минуты", "через две минуты")]
    [InlineData("1 минуту", "одна минуту")]
    [InlineData("осталось 3 задачи", "осталось три задачи")]
    [InlineData("1250 штук", "одна тысяча двести пятьдесят штук")]
    public void Digits_BecomeSpeech(string input, string expected) =>
        Assert.Equal(expected, SpokenNumbers.Apply(input));

    [Theory]
    [InlineData("Hytale и Obsidian", "хайтейл и обсидиан")]
    [InlineData("в ChatGPT", "в чат джи пи ти")]
    [InlineData("Obsidian", "обсидиан")]
    public void LatinNames_ReadInRussian(string input, string expected) =>
        Assert.Equal(expected, LatinLexicon.Apply(input));
}
