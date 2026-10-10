using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class EmbeddingSearchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-emb-" + Guid.NewGuid().ToString("N"));

    /// <summary>Stand-in model: three "topics" as axes; words of one topic lean the vector to its axis.</summary>
    private sealed class FakeEmbedder : ITextEmbedder
    {
        private static readonly string[][] Topics =
        [
            ["улей", "uley", "проект", "приёмк", "план"],
            ["борщ", "рецепт", "свёкл", "кухн"],
            ["погод", "дождь", "самар"],
        ];

        public int Dimensions => 3;
        public float[] EmbedQuery(string text) => Embed(text);
        public float[] EmbedPassage(string text) => Embed(text);

        private static float[] Embed(string text)
        {
            string t = text.ToLowerInvariant();
            var v = new float[3];
            for (int i = 0; i < Topics.Length; i++)
                v[i] = Topics[i].Count(w => t.Contains(w));
            if (v.All(x => x == 0))
                v[0] = v[1] = v[2] = 0.58f;
            double norm = Math.Sqrt(v.Sum(x => x * x));
            return v.Select(x => (float)(x / norm)).ToArray();
        }
    }

    public EmbeddingSearchTests() => Directory.CreateDirectory(Path.Combine(_dir, "vault", "20"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Notes_FoundByMeaning_WhenNoWordIsShared()
    {
        File.WriteAllText(Path.Combine(_dir, "vault", "20", "a.md"), "# Приёмка\n\nСоставить план приёмки пакетов.\n");
        File.WriteAllText(Path.Combine(_dir, "vault", "20", "b.md"), "# Кухня\n\nРецепт борща: свёкла и капуста.\n");
        var index = new NotesIndex(Path.Combine(_dir, "notes.db"));
        index.Refresh(Path.Combine(_dir, "vault"), ["20"]);

        // Words alone: "Uley" is not in the first note, nothing matches.
        Assert.Empty(index.Search("uley", 2));

        var fake = new FakeEmbedder();
        Assert.Equal(0, index.EmbedPending(fake));
        index.Embedder = fake;

        IReadOnlyList<NoteChunk> found = index.Search("uley", 2);

        Assert.Equal("Приёмка", found.First().Heading);
        Assert.DoesNotContain(found, c => c.Heading == "Кухня");
    }

    [Fact]
    public void Notes_EmbedPending_IsIncremental()
    {
        File.WriteAllText(Path.Combine(_dir, "vault", "20", "a.md"), "# Один\n\nтекст про погоду\n\n# Два\n\nещё про дождь\n");
        var index = new NotesIndex(Path.Combine(_dir, "notes.db"));
        index.Refresh(Path.Combine(_dir, "vault"), ["20"]);

        Assert.Equal(1, index.EmbedPending(new FakeEmbedder(), batch: 1));
        Assert.Equal(0, index.EmbedPending(new FakeEmbedder(), batch: 5));
    }

    [Fact]
    public void Memory_FindsFactByMeaning()
    {
        var store = new SqliteMemoryStore(Path.Combine(_dir, "memory.db"));
        store.Upsert(MemoryType.Project, "задача", "составить план приёмки");
        store.Upsert(MemoryType.Preference, "любимое блюдо", "борщ");

        Assert.Empty(store.Search("uley", 3));

        store.Embedder = new FakeEmbedder();
        IReadOnlyList<MemoryItem> found = store.Search("uley", 3);

        Assert.Equal("задача", found.Single().Key);
    }

    [Fact]
    public void Fuse_PrefersItemsBothListsLike()
    {
        IReadOnlyList<string> merged = Vectors.Fuse<string>([["a", "b", "c"], ["c", "d", "b"]], 3);

        Assert.Equal(["c", "b", "a"], merged);
    }
}
