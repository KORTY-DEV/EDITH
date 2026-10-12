using System.Runtime.CompilerServices;

namespace Edith.App.Tests;

/// <summary>Tests must not reach the owner's real cloud brain (key and cloud.json of this machine): "brain off" has to mean off.</summary>
internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void KeepCloudOff()
    {
        // As the app does at start: the speech layer's richer number rules.
        Edith.Core.Brain.VoiceText.NumberWords = Edith.Speech.SpokenNumbers.Apply;

        if (Environment.GetEnvironmentVariable("EDITH_CLOUD_CONFIG") is not { Length: > 0 })
            Environment.SetEnvironmentVariable("EDITH_CLOUD_CONFIG", Path.Combine(Path.GetTempPath(), "edith-tests-no-cloud.json"));

        if (Environment.GetEnvironmentVariable("EDITH_BRAIN_CONFIG") is not { Length: > 0 })
        {
            string off = Path.Combine(Path.GetTempPath(), "edith-tests-brain-off.json");
            File.WriteAllText(off, "{ \"enabled\": false }");
            Environment.SetEnvironmentVariable("EDITH_BRAIN_CONFIG", off);
        }
    }
}
