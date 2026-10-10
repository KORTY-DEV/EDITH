using Edith.Core;

namespace Edith.Core.Tests;

public sealed class ConversationConfigTests
{
    [Fact]
    public void ShippedConfig_WithComments_IsReadNotSilentlyDefaulted()
    {
        string root = AppContext.BaseDirectory;
        string? path = null;
        for (string? dir = root; dir is not null && path is null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "config", "conversation.json")))
                path = Path.Combine(dir, "config", "conversation.json");
        if (path is null)
            return;

        ConversationSettings settings = ConversationSettingsLoader.Load(path);

        Assert.True(File.ReadAllText(path).Contains("//"), "the shipped file keeps its comments");
        Assert.Equal(6, settings.SpeechStartTimeoutSeconds);
        Assert.Equal(550, settings.SpeechEndSilenceMilliseconds);
    }

    [Fact]
    public void ListeningAfterTheName_IsNeverShorterThanFiveSeconds()
    {
        string file = Path.GetTempFileName();
        File.WriteAllText(file, "{ // short\n \"speechStartTimeoutSeconds\": 2, }");
        Assert.Equal(TimeSpan.FromSeconds(5), ConversationSettingsLoader.Load(file).SpeechStartTimeout);
        Assert.Equal(TimeSpan.FromSeconds(6), new ConversationSettings().SpeechStartTimeout);
    }
}
