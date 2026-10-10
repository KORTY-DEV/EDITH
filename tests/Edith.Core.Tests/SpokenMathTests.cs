using Edith.Core;

namespace Edith.Core.Tests;

public sealed class SpokenMathTests
{
    [Theory]
    [InlineData("сколько будет две тысячи двадцать шесть минус тысяча девятьсот девяносто девять", "27")]
    [InlineData("сколько будет 2026 минус 1999", "27")]
    [InlineData("сколько будет пять плюс семь", "12")]
    [InlineData("посчитай сто двадцать пять умножить на восемь", "1000")]
    [InlineData("сколько будет десять разделить на четыре", "2,5")]
    [InlineData("сколько будет два плюс три умножить на четыре", "14")]
    [InlineData("сколько будет тысяча плюс две тысячи пятьсот", "3500")]
    [InlineData("чему равно 3,5 умножить на 2", "7")]
    [InlineData("сколько будет миллион минус один", "999999")]
    [InlineData("сколько будет семь разделить на ноль", "На ноль делить нельзя.")]
    public void Spoken_arithmetic_is_exact(string phrase, string expected)
    {
        Assert.True(SpokenMath.TryAnswer(phrase, out string answer));
        Assert.Equal(expected, answer);
    }

    [Theory]
    [InlineData("сколько оперативки свободно")]
    [InlineData("сколько стоит видеокарта")]
    [InlineData("сколько будет стоить доставка плюс сборка")]
    [InlineData("который час")]
    [InlineData("сколько будет пять")]
    public void Anything_else_is_left_to_the_brain(string phrase) =>
        Assert.False(SpokenMath.TryAnswer(phrase, out _));
}
