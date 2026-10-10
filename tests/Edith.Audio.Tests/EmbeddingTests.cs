using System.Text.Json;
using Edith.Audio.Embedding;

namespace Edith.Audio.Tests;

public sealed class EmbeddingTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string ModelFolder = @"C:\EDITH\models\embedding";

    private sealed record Case(string Text, int[] Ids, float[] Vec);

    private static List<Case> Cases()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Support", "embedding-expected.json"));
        using JsonDocument doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => new Case(
            e.GetProperty("text").GetString()!,
            e.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()).ToArray(),
            e.GetProperty("vec").EnumerateArray().Select(x => (float)x.GetDouble()).ToArray())).ToList();
    }

    [Fact]
    public void Tokenizer_MatchesHuggingFace()
    {
        string path = Path.Combine(ModelFolder, "tokenizer.json");
        if (!File.Exists(path))
            return;

        UnigramTokenizer tokenizer = UnigramTokenizer.Load(path);
        var wrong = new List<string>();
        foreach (Case c in Cases())
        {
            int[] ids = tokenizer.Encode(c.Text);
            if (!ids.SequenceEqual(c.Ids))
                wrong.Add($"{c.Text}\n  expected {string.Join(',', c.Ids)}\n  actual   {string.Join(',', ids)}");
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }

    [Fact]
    public void Embedder_MatchesReferenceVectors_AndRanksByMeaning()
    {
        using E5Embedder? embedder = E5Embedder.TryCreate(ModelFolder);
        if (embedder is null)
            return;

        foreach (Case c in Cases())
        {
            float[] vector = embedder.Embed(c.Text);
            double worst = c.Vec.Select((v, i) => Math.Abs(v - vector[i])).Max();
            Assert.True(worst < 0.01, $"{c.Text}: largest difference {worst}");
        }

        // Meaning, not shared words: the question about tasks lands on the task note, not the recipe.
        float[] question = embedder.EmbedQuery("что мне ещё надо сделать по проекту Улей");
        float task = Core.Memory.Vectors.Dot(question, embedder.EmbedPassage("Задачи ULEY: проверить зависимости и составить план приёмки."));
        float cooking = Core.Memory.Vectors.Dot(question, embedder.EmbedPassage("Рецепт борща: свёкла, капуста, картофель и мясо."));
        output.WriteLine($"task {task:F3} vs cooking {cooking:F3}");
        Assert.True(task > cooking + 0.03f);
    }
}
