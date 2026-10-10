using System.Text.Json;
using Edith.Core.Memory;
using Edith.Core.Persona;

namespace Edith.Core.Tests;

public sealed class RussianStemmerTests
{
    [Theory]
    [InlineData("девушку", "девушка")]
    [InlineData("цвета", "цвет")]
    [InlineData("любимый", "любимая")]
    [InlineData("Анну", "анна")]
    [InlineData("Москве", "Москва")]
    public void InflectedForms_ShareAStem(string a, string b)
    {
        Assert.Equal(RussianStemmer.Stem(a), RussianStemmer.Stem(b));
    }

    [Fact]
    public void Stems_DropStopWordsAndShortTokens()
    {
        Assert.Equal(["любим", "цвет"], RussianStemmer.Stems("Какой мой любимый цвет?"));
    }
}

public sealed class SqliteMemoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-memory-v2-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "memory-v2.db");

    [Fact]
    public void Search_FindsInflectedForms_AndSurvivesReopen()
    {
        var store = new SqliteMemoryStore(Db);
        store.Upsert(MemoryType.Person, "девушка", "анна");
        store.Upsert(MemoryType.Preference, "любимый цвет", "зелёный");

        var reopened = new SqliteMemoryStore(Db);
        MemoryItem girl = Assert.Single(reopened.Search("как зовут мою девушку"));
        Assert.Equal("анна", girl.Content);
        Assert.NotNull(girl.LastUsedAt);
        Assert.Equal("зелёный", reopened.Search("какой мой любимый цвет")[0].Content);
    }

    [Fact]
    public void Upsert_ReplacesSameKey_AndForgetHidesFromSearch()
    {
        var store = new SqliteMemoryStore(Db);
        store.Upsert(MemoryType.Preference, "любимый цвет", "зелёный");
        store.Upsert(MemoryType.Preference, "Любимый цвет", "синий");

        MemoryItem only = Assert.Single(store.ActiveItems());
        Assert.Equal("синий", only.Content);

        Assert.Equal(1, store.Forget("любимый цвет"));
        Assert.Empty(store.Search("любимый цвет"));
        Assert.Empty(store.ActiveItems());
    }

    [Fact]
    public void Search_WithOnlyStopWords_ReturnsNothing()
    {
        var store = new SqliteMemoryStore(Db);
        store.Upsert(MemoryType.UserFact, "x", "что-то");

        Assert.Empty(store.Search("а что это"));
    }

    [Fact]
    public void FirstOpen_ImportsV1Json_Once()
    {
        Directory.CreateDirectory(_dir);
        string v1 = Path.Combine(_dir, "memory-v1.json");
        var legacy = new MemoryItem { Type = MemoryType.Preference, Key = "любимый цвет", Content = "синий" };
        File.WriteAllText(v1, JsonSerializer.Serialize(new[] { legacy }));

        var store = new SqliteMemoryStore(Db, v1);
        Assert.Equal(legacy.Id, Assert.Single(store.ActiveItems()).Id);

        store.Forget("любимый цвет");
        var reopened = new SqliteMemoryStore(Db, v1);
        Assert.Empty(reopened.ActiveItems());
    }

    [Fact]
    public void BrokenV1Json_DoesNotBlockTheStore()
    {
        Directory.CreateDirectory(_dir);
        string v1 = Path.Combine(_dir, "memory-v1.json");
        File.WriteAllText(v1, "{ not json");

        var store = new SqliteMemoryStore(Db, v1);
        store.Upsert(MemoryType.UserFact, "город", "москва");

        Assert.Single(store.ActiveItems());
    }

    [Fact]
    public void ConversationLog_ReturnsTurnsOfTheRequestedRange()
    {
        var store = new SqliteMemoryStore(Db);
        DateTimeOffset day = new(2026, 9, 22, 0, 0, 0, TimeSpan.FromHours(3));
        store.AddTurn(new ConversationTurn(day.AddHours(10), 1, "какая погода", "солнечно", "Brain"));
        store.AddTurn(new ConversationTurn(day.AddDays(1).AddHours(9), 2, "открой браузер", "Открываю.", "DirectFastPath"));

        IReadOnlyList<ConversationTurn> yesterday = store.Turns(day, day.AddDays(1));

        ConversationTurn turn = Assert.Single(yesterday);
        Assert.Equal("какая погода", turn.User);
        Assert.Equal(1, turn.Session);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}

public sealed class MemoryPolicyV2Tests
{
    [Theory]
    [InlineData("мою маму зовут Ольга", MemoryType.Person, "мама", "ольга")]
    [InlineData("моего кота зовут Барсик.", MemoryType.Person, "кот", "барсик")]
    [InlineData("меня зовут Тимур", MemoryType.Person, "пользователь", "тимур")]
    [InlineData("называй меня босс", MemoryType.Preference, MemoryPolicy.AddressKey, "босс")]
    [InlineData("обращайся ко мне Тимур", MemoryType.Preference, MemoryPolicy.AddressKey, "тимур")]
    [InlineData("мой день рождения 12 марта", MemoryType.UserFact, "день рождения", "12 марта")]
    [InlineData("запомни, что я живу в Казани", MemoryType.UserFact, "город", "казани")]
    [InlineData("запомни что я работаю дизайнером", MemoryType.UserFact, "работа", "дизайнером")]
    public void StructuredFacts_AreExtracted(string phrase, MemoryType type, string key, string value)
    {
        Assert.True(MemoryPolicy.TryExtractSave(phrase, out MemoryType actualType, out string actualKey, out string actualValue));
        Assert.Equal((type, key, value), (actualType, actualKey, actualValue));
    }

    [Theory]
    [InlineData("я живу в шоке от этой игры")]
    [InlineData("я работаю над проектом")]
    [InlineData("как зовут мою маму")]
    public void EverydaySpeech_IsNotSaved(string phrase)
    {
        Assert.False(MemoryPolicy.TryExtractSave(phrase, out _, out _, out _));
    }

    [Fact]
    public void Formatter_SpeaksAboutRelativesAndFacts()
    {
        MemoryItem[] items =
        [
            new() { Type = MemoryType.Person, Key = "мама", Content = "ольга" },
            new() { Type = MemoryType.UserFact, Key = "город", Content = "казани" },
            new() { Type = MemoryType.UserFact, Key = "я работаю дома", Content = "я работаю дома" },
        ];

        Assert.Equal(
            "Помню, что твою маму зовут ольга; ты живёшь в казани; я работаю дома.",
            MemorySummaryFormatter.Format(items));
    }
}

public sealed class NaturalMemorySummaryTests
{
    [Fact]
    public void Natural_GroupsNameRelativesAndFacts_SkipsRedundantAddress()
    {
        MemoryItem[] items =
        [
            new() { Type = MemoryType.Person, Key = "пользователь", Content = "павел" },
            new() { Type = MemoryType.Preference, Key = MemoryPolicy.AddressKey, Content = "павел" },
            new() { Type = MemoryType.Person, Key = "мама", Content = "татьяна" },
            new() { Type = MemoryType.Preference, Key = "любимый цвет", Content = "синий" },
        ];

        Assert.Equal(
            "Знаю, что ты Павел. Из близких — мама Татьяна. Ещё помню: твой любимый цвет — синий.",
            MemorySummaryFormatter.Natural(items));
    }

    [Fact]
    public void Natural_Empty()
    {
        Assert.StartsWith("Пока почти ничего о тебе не знаю", MemorySummaryFormatter.Natural([]));
    }
}

public sealed class ConversationRecallTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 15, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("о чём мы говорили вчера", "вчера", 22)]
    [InlineData("что мы обсуждали сегодня", "сегодня", 23)]
    [InlineData("о чем я тебя спрашивал позавчера", "позавчера", 21)]
    [InlineData("о чём мы с тобой разговаривали", "сегодня", 23)]
    public void TryParse_RecognizesDay(string command, string dayWord, int day)
    {
        Assert.True(ConversationRecall.TryParse(command, Now, out DateTimeOffset from, out DateTimeOffset to, out string word));
        Assert.Equal(dayWord, word);
        Assert.Equal(day, from.Day);
        Assert.Equal(TimeSpan.FromDays(1), to - from);
    }

    [Theory]
    [InlineData("что ты помнишь обо мне")]
    [InlineData("о чём этот фильм")]
    public void TryParse_IgnoresOtherQuestions(string command)
    {
        Assert.False(ConversationRecall.TryParse(command, Now, out _, out _, out _));
    }

    [Fact]
    public void Summarize_ListsRealTopics_SkippingSmallTalkAndMeta()
    {
        ConversationTurn[] turns =
        [
            new(Now, 1, "привет", "Добрый день.", "DirectFastPath"),
            new(Now, 2, "какая температура видеокарты", "65 градусов.", "Brain"),
            new(Now, 3, "что ты делала", "…", "ActivityReport"),
            new(Now, 4, "открой браузер", "Открываю.", "AppControl"),
            new(Now, 5, "напомни через час позвонить", "Хорошо.", "Reminder"),
            new(Now, 6, "Открой телеграм", "Открываю.", "AppControl"),
        ];

        Assert.Equal(
            "Вчера говорили про «какая температура видеокарты». Ещё были поручения: приложения, напоминания.",
            ConversationRecall.Summarize(turns, "вчера"));
        Assert.Equal(["какая температура видеокарты"], ConversationRecall.Topics(turns));
    }

    [Fact]
    public void Summarize_OnlyErrands()
    {
        ConversationTurn[] turns = [new(Now, 1, "открой дискорд", "Открываю.", "AppControl")];

        Assert.Equal("Сегодня в основном были поручения: приложения.", ConversationRecall.Summarize(turns, "сегодня"));
    }

    [Fact]
    public void Summarize_EmptyDay()
    {
        Assert.Equal("Вчера мы не разговаривали.", ConversationRecall.Summarize([], "вчера"));
        Assert.Equal("Сегодня мы ещё ничего толком не обсуждали.", ConversationRecall.Summarize([], "сегодня"));
    }
}

public sealed class PersonaTests
{
    [Fact]
    public void DefaultPersona_KeepsTheOriginalCharacterRules()
    {
        string prompt = PersonaPrompt.Build(new PersonaProfile(), []);

        Assert.StartsWith("Ты E.D.I.T.H. — спокойный, уверенный и слегка тёплый женский голосовой ассистент Windows", prompt);
        Assert.Contains("называй «сэр»", prompt);
        Assert.Contains("только в женском роде", prompt);
        Assert.DoesNotContain("Что ты знаешь о хозяине", prompt);
    }

    [Fact]
    public void Prompt_IncludesUserFacts_WithinBudget_NameFirst()
    {
        MemoryItem[] memories =
        [
            new() { Type = MemoryType.UserFact, Key = "город", Content = "казани" },
            new() { Type = MemoryType.Person, Key = "пользователь", Content = "тимур" },
            new() { Type = MemoryType.Preference, Key = MemoryPolicy.AddressKey, Content = "тимур" },
            new() { Type = MemoryType.UserFact, Key = "длинный факт", Content = new string('x', 500) },
        ];

        string prompt = PersonaPrompt.Build(new PersonaProfile(), memories);

        Assert.Contains("называй «Тимур»", prompt);
        Assert.Contains("Что ты знаешь о хозяине (используй к месту, не пересказывай): тебя зовут тимур; ты живёшь в казани", prompt);
        Assert.DoesNotContain(new string('x', 100), prompt);
        Assert.DoesNotContain("к тебе нужно обращаться", prompt);
    }

    [Theory]
    [InlineData(8, "Доброе утро, сэр.")]
    [InlineData(14, "Добрый день, сэр.")]
    [InlineData(20, "Добрый вечер, сэр.")]
    [InlineData(2, "Доброй ночи, сэр.")]
    public void Greeting_FollowsTimeOfDay(int hour, string expected)
    {
        Assert.Equal(expected, PersonaPrompt.Greeting(new DateTimeOffset(2026, 9, 23, hour, 0, 0, TimeSpan.Zero), "сэр"));
    }

    [Fact]
    public void Loader_FallsBackOnBlankOrBrokenFile()
    {
        var warnings = new List<string>();

        PersonaProfile broken = PersonaLoader.Parse("{ nope", warnings);
        PersonaProfile blank = PersonaLoader.Parse("""{ "name": "", "rules": [], "defaultAddress": "босс" }""");

        Assert.Equal(new PersonaProfile().Character, broken.Character);
        Assert.Single(warnings);
        Assert.Equal("E.D.I.T.H.", blank.Name);
        Assert.NotEmpty(blank.Rules);
        Assert.Equal("босс", blank.DefaultAddress);
    }

    [Fact]
    public void ShippedPersonaFile_ParsesWithoutWarnings()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "persona.json");
        path = File.Exists(path) ? path : @"C:\EDITH\config\persona.json";
        var warnings = new List<string>();

        PersonaProfile persona = PersonaLoader.Load(path, warnings);

        Assert.Empty(warnings);
        Assert.Equal("сэр", persona.DefaultAddress);
        Assert.Empty(persona.Examples); // examples were parroted verbatim by the model
    }
}

public sealed class CompactPersonaTests
{
    [Fact]
    public void Compact_DropsExamples_AndShrinksProfile()
    {
        var persona = new PersonaProfile { Examples = ["Готово."] };
        MemoryItem[] memories =
        [
            new() { Type = MemoryType.Person, Key = "пользователь", Content = "тимур" },
            new() { Type = MemoryType.UserFact, Key = "факт", Content = new string('я', 150) },
        ];

        string full = PersonaPrompt.Build(persona, memories);
        string compact = PersonaPrompt.Build(persona, memories, compact: true);

        Assert.Contains("Так звучит твой тон", full);
        Assert.DoesNotContain("Так звучит твой тон", compact);
        Assert.Contains("тебя зовут тимур", compact);
        Assert.DoesNotContain(new string('я', 150), compact);
        Assert.True(compact.Length < full.Length);
    }
}
