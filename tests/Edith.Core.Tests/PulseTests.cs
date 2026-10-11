using Edith.Core.Activity;

namespace Edith.Core.Tests;

public sealed class PulseTests : IDisposable
{
    private readonly string _db = Path.Combine(Path.GetTempPath(), $"edith-pulse-{Guid.NewGuid():N}.db");
    // Wednesday evening.
    private readonly DateTimeOffset _now = new(2026, 10, 7, 21, 0, 0, TimeSpan.FromHours(4));

    public void Dispose()
    {
        foreach (string file in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(_db) + "*"))
            try { File.Delete(file); } catch { }
    }

    private RepoState Repo(string name, int daysAgo, int dirty = 0, int week = 0, string subject = "Что-то сделано") =>
        new(name, "main", _now.AddDays(-daysAgo), subject, week, dirty, [subject]);

    [Fact]
    public void UnsavedWork_AfterADay_BecomesACard_OncePerLastCommit()
    {
        var repos = new[] { Repo("ULEY", 2, dirty: 3, subject: "Slice 5") };

        PulseResult first = PulseBuilder.Build(repos, [], _now);
        PulseCard card = Assert.Single(first.Cards);
        Assert.Contains("3 файла", card.Body);
        Assert.Contains("2 дня назад", card.Body);

        var store = new ActivityStore(_db);
        Assert.True(store.AddCard(card));
        Assert.False(store.AddCard(PulseBuilder.Build(repos, [], _now.AddHours(1)).Cards.Single()));
    }

    [Fact]
    public void TodaysWork_NeedsNoCard_ButOldProjectDoes()
    {
        var repos = new[] { Repo("EDITH", 0, dirty: 5), Repo("orbita", 6), Repo("Ancient", 40) };

        PulseResult result = PulseBuilder.Build(repos, [], _now);

        PulseCard card = Assert.Single(result.Cards);
        Assert.Contains("orbita", card.Title);
        Assert.Contains("6 дней", card.Body);
    }

    [Fact]
    public void Dossier_NamesProjectsAndTheGamingEvening()
    {
        var usage = new List<UsageRow>();
        for (int d = 1; d <= 6; d++)
            for (int hour = 19; hour <= 22; hour++)
                usage.Add(new UsageRow(DateOnly.FromDateTime(_now.AddDays(-d).Date), hour, "WorldOfTanks", 2400));
        usage.Add(new UsageRow(DateOnly.FromDateTime(_now.Date), 14, "opera", 5000));

        PulseResult result = PulseBuilder.Build([Repo("EDITH", 0, week: 12), Repo("ULEY", 3)], usage, _now);

        Assert.Contains("EDITH (сегодня)", result.Dossier);
        Assert.Contains("ULEY (3 дня назад)", result.Dossier);
        Assert.Contains("World of Tanks", result.Dossier);
        Assert.Contains("вечером (19–23 ч)", result.Dossier);
        Assert.True(result.Dossier.Length <= 321);
    }

    [Fact]
    public void WeekReview_OnMonday_Once()
    {
        DateTimeOffset monday = new(2026, 10, 5, 10, 0, 0, TimeSpan.FromHours(4));
        var usage = new[] { new UsageRow(DateOnly.FromDateTime(monday.AddDays(-2).Date), 20, "WorldOfTanks", 3 * 3600) };

        PulseResult result = PulseBuilder.Build([Repo("EDITH", 1, week: 25)], usage, monday);

        PulseCard week = Assert.Single(result.Cards, c => c.Kind == "week");
        Assert.Contains("EDITH — 25 коммитов", week.Body);
        Assert.Contains("World of Tanks", week.Body);
        Assert.DoesNotContain(PulseBuilder.Build([Repo("EDITH", 1)], [], _now).Cards, c => c.Kind == "week");
    }

    [Fact]
    public void Usage_Accumulates_PerHour_AndCardsCanBeDismissed()
    {
        var store = new ActivityStore(_db);
        store.AddUsage(_now, "opera", 30);
        store.AddUsage(_now.AddMinutes(5), "opera", 30);

        UsageRow row = Assert.Single(store.Usage(_now.AddDays(-1), _now));
        Assert.Equal(60, row.Seconds);
        Assert.Equal(21, row.Hour);

        store.AddCard(new PulseCard(0, _now, "project", "k1", "t", "b", 0));
        PulseCard card = Assert.Single(store.Cards());
        store.SetCardState(card.Id, ActivityStore.CardDismissed);
        Assert.Empty(store.Cards());
    }

    [Theory]
    [InlineData("explorer", null)]
    [InlineData("Edith.App", null)]
    [InlineData("WorldOfTanks", "World of Tanks")]
    [InlineData("msedge", "Edge")]
    [InlineData("SomeTool", "SomeTool")]
    public void FriendlyApp_HidesShell_NamesGames(string process, string? expected) =>
        Assert.Equal(expected, PulseBuilder.FriendlyApp(process));

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(3, "3 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(22, "22 файла")]
    public void Plural_Russian(int n, string expected) => Assert.Equal(expected, PulseBuilder.Files(n));

    [Fact]
    public void Brief_MentionsYesterdaysWorkAndWaitingNotes()
    {
        var usage = new[] { new UsageRow(DateOnly.FromDateTime(_now.AddDays(-1).Date), 20, "WorldOfTanks", 3 * 3600) };

        string brief = PulseBuilder.Brief([Repo("EDITH", 1, subject: "Voice pauses")], usage, _now, newCards: 2);

        Assert.Contains("EDITH", brief);
        Assert.Contains("Вчера", brief);
        Assert.Contains("World of Tanks", brief);
        Assert.Contains("Новых заметок: 2", brief);
        Assert.Equal("", PulseBuilder.Brief([], [], _now, 0));
    }

    [Fact]
    public void Brief_QuotesOnlyRussianCommitSubjects()
    {
        string english = PulseBuilder.Brief([Repo("EDITH", 0, subject: "Short-phrase follow-up threshold 3 words")], [], _now, 0);
        string russian = PulseBuilder.Brief([Repo("EDITH", 0, subject: "Починил шар при речи")], [], _now, 0);

        Assert.DoesNotContain("Short-phrase", english);
        Assert.Contains("EDITH", english);
        Assert.Contains("«Починил шар при речи»", russian);
    }
}
