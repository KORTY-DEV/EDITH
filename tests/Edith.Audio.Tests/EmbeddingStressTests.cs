using Edith.Audio.Embedding;

namespace Edith.Audio.Tests;

/// <summary>The notes index embeds chunks in the background while questions embed their own text: the embedder must survive both.</summary>
public sealed class EmbeddingStressTests
{
    private const string ModelFolder = @"C:\EDITH\models\embedding";

    private static readonly string[] Texts =
    [
        "", " ", "\n", "\t\t", "\u0000", "#### Часть 1 из 7", "**Сделано:** файл `Edith.App.dll` — 12 тестов ✅", "😀 эмодзи 🚀", "x",
        "Ъ", "ё", "𝔘𝔫𝔦𝔠𝔬𝔡𝔢", new string('а', 5000), string.Join(" ", Enumerable.Repeat("слово", 800)),
        "> цитата\n> ещё строка\n\n- пункт 1\n- пункт 2", "[[ссылка|подпись]] и https://example.com/path?x=1&y=2", @"C:\EDITH\data\library\ledger.json",
        "\u200b\u200bневидимые\u200b", "Ǆ ǅ ǆ ǈ", "ﬁ ligature", "a\u0301 combining", "العربية", "日本語のテキスト",
    ];

    [Fact]
    public void EdgeTexts_NeverThrow_AndGiveUnitVectors()
    {
        using E5Embedder? embedder = E5Embedder.TryCreate(ModelFolder);
        if (embedder is null)
            return;

        foreach (string text in Texts)
        {
            float[] vector = embedder.EmbedPassage(text);
            Assert.Equal(384, vector.Length);
            Assert.All(vector, v => Assert.False(float.IsNaN(v)));
        }
    }

    [Fact]
    public void ManyThreadsAtOnce_GiveTheSameVectorsAsOne()
    {
        using E5Embedder? embedder = E5Embedder.TryCreate(ModelFolder);
        if (embedder is null)
            return;

        float[][] single = Texts.Select(embedder.EmbedPassage).ToArray();
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 8 * Texts.Length, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            int k = i % Texts.Length;
            try
            {
                float[] vector = (i % 2 == 0 ? embedder.EmbedPassage(Texts[k]) : embedder.EmbedQuery(Texts[k]));
                if (i % 2 == 0 && vector.Zip(single[k], (a, b) => Math.Abs(a - b)).Max() > 1e-4)
                    failures.Add($"differs for text #{k}");
            }
            catch (Exception ex)
            {
                failures.Add($"text #{k}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace?.Split('\n').FirstOrDefault()}");
            }
        });

        Assert.True(failures.IsEmpty, string.Join("\n", failures.Distinct().Take(5)));
    }
}

public sealed class EmbeddingDisposeTests
{
    [Fact]
    public void AfterDispose_EmbedThrowsDisposed_NotNullReference()
    {
        using E5Embedder? embedder = E5Embedder.TryCreate(@"C:\EDITH\models\embedding");
        if (embedder is null)
            return;

        embedder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => embedder.EmbedPassage("после закрытия"));
        embedder.Dispose();
    }
}
