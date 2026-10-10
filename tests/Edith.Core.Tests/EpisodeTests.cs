using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class EpisodeTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"edith-episodes-{Guid.NewGuid():N}.db");
    private readonly DateTimeOffset _now = new(2026, 10, 2, 20, 0, 0, TimeSpan.FromHours(4));

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
    }

    private static ConversationTurn Turn(DateTimeOffset at, string user, string answer = "ответ") =>
        new(at, 1, user, answer, "AiBrain");

    [Fact]
    public async Task FinishedConversation_BecomesAnEpisode_Once()
    {
        var store = new SqliteMemoryStore(_db);
        DateTimeOffset start = _now.AddHours(-3);
        foreach (int minute in new[] { 0, 2, 5 })
            store.AddTurn(Turn(start.AddMinutes(minute), "сколько стоит rtx 5090"));

        int calls = 0;
        var summarizer = new EpisodeSummarizer(store, (_, prompt, _) =>
        {
            calls++;
            Assert.Contains("rtx 5090", prompt);
            return Task.FromResult<string?>("""{"summary":"Хозяин спрашивал про цену RTX 5090, получил около 655 тысяч рублей.","topics":["цены на видеокарты"]}""");
        }, clock: () => _now);

        Assert.Equal(1, await summarizer.RunOnceAsync(default));
        Assert.Equal(0, await summarizer.RunOnceAsync(default));
        Assert.Equal(1, calls);

        Episode episode = store.Episodes(_now.AddDays(-1), _now).Single();
        Assert.Contains("RTX 5090", episode.Summary);
        Assert.Equal("цены на видеокарты", episode.Topics);
        Assert.Equal(3, episode.Turns);
    }

    [Fact]
    public async Task ConversationStillGoing_IsLeftAlone()
    {
        var store = new SqliteMemoryStore(_db);
        for (int i = 0; i < 4; i++)
            store.AddTurn(Turn(_now.AddMinutes(-4 + i), "расскажи про погоду"));

        var summarizer = new EpisodeSummarizer(store, (_, _, _) => throw new InvalidOperationException("too early"), clock: () => _now);

        Assert.Equal(0, await summarizer.RunOnceAsync(default));
    }

    [Fact]
    public async Task BrainBusy_KeepsTheTurnsForNextTime()
    {
        var store = new SqliteMemoryStore(_db);
        for (int i = 0; i < 3; i++)
            store.AddTurn(Turn(_now.AddHours(-3).AddMinutes(i), "что нового"));

        Assert.Equal(0, await new EpisodeSummarizer(store, (_, _, _) => Task.FromResult<string?>(null), clock: () => _now).RunOnceAsync(default));

        int saved = await new EpisodeSummarizer(store, (_, _, _) =>
            Task.FromResult<string?>("""{"summary":"Хозяин интересовался новостями.","topics":[]}"""), clock: () => _now).RunOnceAsync(default);
        Assert.Equal(1, saved);
    }

    [Fact]
    public void Split_StartsNewEpisodeAfterAHalfHourGap()
    {
        DateTimeOffset t = _now.AddHours(-5);
        List<List<ConversationTurn>> parts = EpisodeSummarizer.Split(
        [
            Turn(t, "а"), Turn(t.AddMinutes(10), "б"), Turn(t.AddMinutes(90), "в"),
        ]).ToList();

        Assert.Equal([2, 1], parts.Select(p => p.Count));
    }

    [Theory]
    [InlineData("не json")]
    [InlineData("""{"summary":"коротко"}""")]
    [InlineData("""{"summary":"Хозяин назвал пароль от почты и номер карты 4276 1234 5678 9012."}""")]
    public void Parse_RejectsGarbageAndSensitive(string reply) =>
        Assert.Null(EpisodeSummarizer.Parse(reply, [Turn(_now, "x")]));

    [Fact]
    public void Prompt_SkipsSensitiveTurns()
    {
        string prompt = EpisodeSummarizer.BuildPrompt([Turn(_now, "мой пароль 12345"), Turn(_now, "открой телеграм")]);

        Assert.DoesNotContain("12345", prompt);
        Assert.Contains("телеграм", prompt);
    }

    [Fact]
    public void PromptLine_NewestLast_WithinBudget_AndDaysInWords()
    {
        var episodes = new List<Episode>
        {
            new(1, _now.AddDays(-1).AddHours(-1), _now.AddDays(-1), "Обсуждали погоду на неделю.", "", 4),
            new(2, _now.AddHours(-3), _now.AddHours(-2), "Хозяин узнавал цену RTX 5090.", "", 3),
        };

        string line = EpisodeRecall.PromptLine(episodes, _now);

        Assert.Equal("вчера вечером — Обсуждали погоду на неделю; сегодня вечером — Хозяин узнавал цену RTX 5090", line);
        Assert.True(EpisodeRecall.PromptLine(episodes, _now, 40).Length <= 40);
        Assert.Equal("", EpisodeRecall.PromptLine([], _now));
    }

    [Fact]
    public void ForDay_JoinsSummaries() =>
        Assert.Equal("Вчера: Первое. Второе.", EpisodeRecall.ForDay(
        [
            new(2, _now.AddHours(-2), _now.AddHours(-1), "Второе.", "", 3),
            new(1, _now.AddHours(-9), _now.AddHours(-8), "Первое", "", 3),
        ], "вчера"));
}
