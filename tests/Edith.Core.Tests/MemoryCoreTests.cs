using Edith.Core;
using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class MemoryCoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "edith-memory-tests-" + Guid.NewGuid());
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }

    [Fact]
    public void Preference_survives_a_new_store_instance()
    {
        string path = Path.Combine(_directory, "memory.json");
        var first = new LocalMemoryStore(path);
        first.Upsert(MemoryType.Preference, "любимый цвет", "зелёный");

        var restarted = new LocalMemoryStore(path);
        MemoryItem item = Assert.Single(restarted.Search("какой мой любимый цвет"));
        Assert.Equal("зелёный", item.Content);
    }

    [Fact]
    public void Forget_makes_memory_unavailable_to_retrieval()
    {
        var store = new LocalMemoryStore(Path.Combine(_directory, "memory.json"));
        store.Upsert(MemoryType.Preference, "любимый цвет", "зелёный");
        Assert.Equal(1, store.Forget("любимый цвет"));
        Assert.Empty(store.Search("какой мой любимый цвет"));
    }

    [Fact]
    public void Correction_replaces_the_previous_preference()
    {
        string path = Path.Combine(_directory, "memory.json");
        var store = new LocalMemoryStore(path);
        Assert.True(MemoryPolicy.TryExtractSave("мой любимый цвет зелёный", out MemoryType firstType, out string firstKey, out string firstValue));
        store.Upsert(firstType, firstKey, firstValue);
        Assert.True(MemoryPolicy.TryExtractSave("нет, любимый цвет синий", out MemoryType secondType, out string secondKey, out string secondValue));
        store.Upsert(secondType, secondKey, secondValue);
        MemoryItem item = Assert.Single(store.Search("какой мой любимый цвет"));
        Assert.Equal("синий", item.Content);
    }

    [Theory]
    [InlineData("запомни, что я работаю дома")]
    [InlineData("я предпочитаю короткие ответы")]
    [InlineData("мою девушку зовут Анна")]
    public void Stable_voice_phrases_are_recognized(string phrase)
    {
        Assert.True(MemoryPolicy.TryExtractSave(phrase, out _, out string key, out string value));
        Assert.NotEmpty(key);
        Assert.NotEmpty(value);
    }

    [Fact]
    public void Conversation_context_is_bounded_and_keeps_recent_turn()
    {
        var context = new ConversationContext();
        for (int i = 0; i < 6; i++) context.Add("вопрос " + i, "ответ " + i);
        string prompt = context.BuildForPrompt();
        Assert.DoesNotContain("вопрос 0", prompt);
        Assert.Contains("вопрос 5", prompt);
    }
}
