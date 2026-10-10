using Edith.Audio;

namespace Edith.Audio.Tests;

public sealed class InterruptTriggerTests
{
    private static readonly IReadOnlySet<string> Echo =
        new HashSet<string> { "погода", "в", "самаре", "сегодня", "днём", "двадцать", "хватит", "сил" };

    [Theory]
    // The mic hears EDITH first, the user's word comes last in the same phrase.
    [InlineData("погода в самаре сегодня днём стоп", "стоп")]
    [InlineData("погода в самаре сегодня стой", "стой")]
    [InlineData("двадцать замолчи", "замолчи")]
    [InlineData("стоп", "стоп")]
    public void StopWord_AfterHerOwnSpeech_Interrupts(string heard, string trigger)
    {
        Assert.True(InterruptRecognizer.TryFindTrigger(heard, Echo, out string found));
        Assert.Equal(trigger, found);
    }

    [Theory]
    // "хватит" is in her own answer: the mic hearing it is not the user.
    [InlineData("сегодня хватит сил")]
    [InlineData("погода в самаре сегодня днём двадцать")]
    public void HerOwnWords_DoNotInterrupt(string heard) =>
        Assert.False(InterruptRecognizer.TryFindTrigger(heard, Echo, out _));
}
