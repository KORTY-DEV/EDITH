using Edith.App;

namespace Edith.App.Tests;

public sealed class VoiceCommandTextTests
{
    [Theory]
    [InlineData("стой а какая погода", "какая погода")]
    [InlineData("Стой а открой блокнот", "открой блокнот")]
    [InlineData("Стой, а открой блокнот.", "открой блокнот")]
    [InlineData("Эдит! Нет, лучше калькулятор", "лучше калькулятор")]
    [InlineData("ну стоп тогда открой браузер", "открой браузер")]
    [InlineData("эдит нет лучше калькулятор", "лучше калькулятор")]
    [InlineData("  хватит  ", "")]
    [InlineData("стой", "")]
    [InlineData("какая погода", "")]
    [InlineData("я сказал стой а потом", "")]
    [InlineData("", "")]
    public void ExtractInterruptTail_KeepsOnlyTheNewCommand(string phrase, string expected)
    {
        Assert.Equal(expected, VoiceCommandText.ExtractInterruptTail(phrase));
    }

    [Theory]
    [InlineData("эдит открой блокнот", "открой блокнот")]
    [InlineData("Едит, привет", "привет")]
    [InlineData("еди включи музыку", "включи музыку")]
    [InlineData("открой блокнот", "открой блокнот")]
    [InlineData("эдита позови", "эдита позови")]
    [InlineData("Эдит, открой «Блокнот».", "открой блокнот")]
    [InlineData("эдит поставь громкость 1.5 раза и что-то ещё", "поставь громкость 1.5 раза и что-то еще")]
    public void CleanDirectCommand_StripsOnlyAWakePrefix(string text, string expected)
    {
        Assert.Equal(expected, VoiceCommandText.CleanDirectCommand(text));
    }

    [Theory]
    [InlineData("стоп", true)]
    [InlineData(" Стоп! ", true)]
    [InlineData("стоп.", true)]
    [InlineData("стоп игру", false)]
    [InlineData("стой", false)]
    [InlineData(null, false)]
    public void IsGlobalStop_MatchesOnlyTheBareWord(string? text, bool expected)
    {
        Assert.Equal(expected, VoiceCommandText.IsGlobalStop(text));
    }

    [Theory]
    [InlineData("повтори", true)]
    [InlineData("Ещё раз", true)]
    [InlineData("скажи это еще раз", true)]
    [InlineData("повтори за мной", false)]
    [InlineData("Повтори!", true)]
    public void IsRepeatCommand_MatchesKnownPhrases(string text, bool expected)
    {
        Assert.Equal(expected, VoiceCommandText.IsRepeatCommand(text));
    }
}
