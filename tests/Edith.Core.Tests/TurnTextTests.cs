using Edith.Core;

namespace Edith.Core.Tests;

public sealed class TurnTextTests
{
    [Theory]
    [InlineData("открой и", true)]
    [InlineData("найди мне в", true)]
    [InlineData("напомни мне через час что", true)]
    [InlineData("какая погода в самаре на", true)]
    [InlineData("а", true)]
    [InlineData("Открой телеграм.", false)]
    [InlineData("какая погода в самаре", false)]
    [InlineData("что делаешь", false)]
    [InlineData("спасибо", false)]
    [InlineData("давай ещё", false)]
    [InlineData("", false)]
    public void Detects(string text, bool midSentence) =>
        Assert.Equal(midSentence, TurnText.EndsMidSentence(text));
}
