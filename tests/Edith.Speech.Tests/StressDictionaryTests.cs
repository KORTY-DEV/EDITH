using Edith.Speech;

namespace Edith.Speech.Tests;

public sealed class StressDictionaryTests
{
    [Fact]
    public void ShippedDictionary_FixesViden_ForSilero()
    {
        StressDictionary.Path = @"C:\EDITH\config\stress.json";

        string spoken = SpeechPronunciation.Apply("как мимолётное виденье, Виденье", "local");

        Assert.Equal("<speak>как мимолётное вид+енье, Вид+енье</speak>", spoken);
    }

    [Fact]
    public void AlreadyStressedOrOtherWords_AreLeftAlone()
    {
        StressDictionary.Path = @"C:\EDITH\config\stress.json";

        Assert.Equal("<speak>вид+енье и видение</speak>", SpeechPronunciation.Apply("вид+енье и видение", "local"));
    }

    [Fact]
    public void ShippedDictionary_OnlyAddsStressMarks()
    {
        var options = new System.Text.Json.JsonSerializerOptions
        {
            ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var rules = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(@"C:\EDITH\config\stress.json"), options)!;

        Assert.True(rules.Count > 100);
        foreach (var (word, stressed) in rules)
        {
            Assert.Equal(word.Replace('ё', 'е'), stressed.Replace("+", "").Replace('ё', 'е'));
            Assert.True(stressed.Contains('+') || stressed.Contains('ё'), word);
        }
    }
}
