using System.Globalization;
using System.Text;
using System.Text.Json;
using Edith.Core.Memory;

namespace Edith.Core.Persona;

/// <summary>
/// Who EDITH is: character, tone, manners, how she addresses the user. Lives in
/// config/persona.json so the character can be tuned without a rebuild; the defaults below
/// are the character EDITH had before the file existed.
/// </summary>
public sealed record PersonaProfile
{
    public string Name { get; init; } = "E.D.I.T.H.";

    public string Character { get; init; } =
        "спокойный, уверенный и слегка тёплый женский голосовой ассистент Windows, личный дворецкий хозяина: " +
        "внимательная, тактичная, с лёгким сухим юмором, без подобострастия.";

    /// <summary>Used until the user says "называй меня ...".</summary>
    public string DefaultAddress { get; init; } = "сэр";

    public IReadOnlyList<string> Rules { get; init; } =
    [
        "Отвечай по-русски, коротко (1-3 предложения), без markdown, не повторяя вопрос.",
        "Не начинай ответ с «конечно» и не вставляй обращение в каждую фразу.",
        "Говори о себе только в женском роде: «готова», «нашла», «проверила», «запомнила», «не смогла», «не уверена». Никогда не используй «готов», «нашёл», «проверил» или «запомнил» о себе.",
        "Технические сокращения SSD, HDD, RTX, NVIDIA и GeForce считай именно названиями компьютерных технологий, а не похожими словами.",
        "Контекст предыдущих реплик используй только для явного продолжения темы («она», «этот», «сколько стоит», «характеристики»). Если пользователь задал новый самостоятельный вопрос, отвечай по нему и не переноси сущности из старой темы.",
    ];

    /// <summary>Short sample lines in EDITH's voice. Every line costs prompt tokens on CPU: keep few.</summary>
    public IReadOnlyList<string> Examples { get; init; } = [];

    /// <summary>Max characters of user facts put into every prompt.</summary>
    public int ProfileBudgetChars { get; init; } = 280;
}

public static class PersonaLoader
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PersonaProfile Parse(string? json, ICollection<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new PersonaProfile();

        try
        {
            PersonaProfile parsed = JsonSerializer.Deserialize<PersonaProfile>(json, Options) ?? new PersonaProfile();
            var defaults = new PersonaProfile();

            // Empty values in the file fall back to the defaults instead of producing a blank character.
            return parsed with
            {
                Name = Blank(parsed.Name) ? defaults.Name : parsed.Name.Trim(),
                Character = Blank(parsed.Character) ? defaults.Character : parsed.Character.Trim(),
                DefaultAddress = Blank(parsed.DefaultAddress) ? defaults.DefaultAddress : parsed.DefaultAddress.Trim(),
                Rules = parsed.Rules is { Count: > 0 } ? parsed.Rules : defaults.Rules,
                Examples = parsed.Examples ?? [],
                ProfileBudgetChars = parsed.ProfileBudgetChars > 0 ? parsed.ProfileBudgetChars : defaults.ProfileBudgetChars,
            };
        }
        catch (JsonException ex)
        {
            warnings?.Add("persona.json: " + ex.Message);
            return new PersonaProfile();
        }
    }

    public static PersonaProfile Load(string path, ICollection<string>? warnings = null)
    {
        try
        {
            return Parse(File.Exists(path) ? File.ReadAllText(path) : null, warnings);
        }
        catch (IOException ex)
        {
            warnings?.Add(ex.Message);
            return new PersonaProfile();
        }
    }

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);
}

/// <summary>Builds the persona part of the system prompt and persona-aware short phrases.</summary>
public static class PersonaPrompt
{
    private static readonly HashSet<string> TitleWords =
        ["сэр", "босс", "шеф", "хозяин", "господин", "мэм", "мадам", "капитан", "командир"];

    /// <summary>How to address the user: the stored preference, else the persona default.</summary>
    public static string Address(PersonaProfile persona, IEnumerable<MemoryItem> memories)
    {
        MemoryItem? stored = memories.FirstOrDefault(item =>
            item.Active &&
            item.Type == MemoryType.Preference &&
            item.Key == MemoryPolicy.AddressKey);

        string address = stored?.Content.Trim() is { Length: > 0 } value ? value : persona.DefaultAddress;
        return TitleWords.Contains(LocalMemoryStore.Normalize(address)) ? LocalMemoryStore.Normalize(address) : ToNameCase(address);
    }

    /// <summary>"тимур" → "Тимур", "анна мария" → "Анна Мария".</summary>
    public static string ToNameCase(string text) =>
        string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpper(word[0], CultureInfo.GetCultureInfo("ru-RU")) + word[1..]));

    public static string Greeting(DateTimeOffset now, string address)
    {
        string phrase = now.Hour switch
        {
            >= 5 and < 12 => "Доброе утро",
            >= 12 and < 18 => "Добрый день",
            >= 18 and < 23 => "Добрый вечер",
            _ => "Доброй ночи",
        };

        return $"{phrase}, {address}.";
    }

    /// <summary>Profile budget when the brain runs on the CPU, where every prompt token costs ~20 ms.</summary>
    public const int CompactProfileBudgetChars = 120;

    /// <summary>
    /// Persona block for the system prompt: character, address, rules, what EDITH knows about
    /// the user (bounded by <see cref="PersonaProfile.ProfileBudgetChars"/>), sample lines.
    /// <paramref name="compact"/> drops the sample lines and shrinks the profile for CPU inference.
    /// </summary>
    public static string Build(PersonaProfile persona, IReadOnlyList<MemoryItem> memories, bool compact = false, string? recentTalks = null)
    {
        var text = new StringBuilder();
        string address = Address(persona, memories);

        text.Append("Ты ").Append(persona.Name).Append(" — ").Append(persona.Character.TrimEnd('.')).Append('.').AppendLine();
        // Addressing by name in every reply grated in live use: rare, never as an opener.
        text.Append("Хозяина зовут/называй «").Append(address)
            .Append("», но обращайся так редко — не чаще раза в несколько ответов и никогда не начинай ответ с обращения или приветствия. Сразу по делу, как живой собеседник.")
            .AppendLine();

        foreach (string rule in persona.Rules.Where(rule => !string.IsNullOrWhiteSpace(rule)))
            text.AppendLine(rule.Trim());

        string profile = UserProfileLine(
            memories,
            compact ? Math.Min(CompactProfileBudgetChars, persona.ProfileBudgetChars) : persona.ProfileBudgetChars);
        if (profile.Length > 0)
            text.Append("Что ты знаешь о хозяине (используй к месту, не пересказывай): ").Append(profile).AppendLine();

        if (!compact && !string.IsNullOrWhiteSpace(recentTalks))
        {
            text.Append("Недавние разговоры с хозяином (для контекста: вспоминай к месту, сам не пересказывай): ")
                .Append(recentTalks).AppendLine();
        }

        if (!compact && persona.Examples.Count > 0)
        {
            text.AppendLine("Так звучит твой тон:");
            foreach (string example in persona.Examples.Where(e => !string.IsNullOrWhiteSpace(e)))
                text.Append("- ").AppendLine(example.Trim());
        }

        return text.ToString().TrimEnd();
    }

    /// <summary>Core user facts, most important first: name, address, people, then the rest.</summary>
    public static string UserProfileLine(IReadOnlyList<MemoryItem> memories, int budgetChars)
    {
        IEnumerable<MemoryItem> ordered = memories
            .Where(item => item.Active && item.Key != MemoryPolicy.AddressKey)
            .OrderBy(item => item.Type switch
            {
                MemoryType.Person when item.Key == "пользователь" => 0,
                MemoryType.Person => 1,
                MemoryType.Preference => 2,
                MemoryType.UserFact => 3,
                _ => 4,
            })
            // Stable order (by when it was learned): the prompt must be byte-identical between
            // requests so the model can reuse its cached prefix (3.5 s -> 0.1 s per answer).
            .ThenBy(item => item.CreatedAt)
            .ThenBy(item => item.Key, StringComparer.Ordinal);

        var parts = new List<string>();
        int used = 0;
        foreach (MemoryItem item in ordered)
        {
            string part = MemorySummaryFormatter.FormatItem(item);
            // An oversized fact is skipped, not allowed to push out the shorter ones after it.
            if (used + part.Length + 2 > budgetChars)
                continue;

            parts.Add(part);
            used += part.Length + 2;
        }

        return string.Join("; ", parts);
    }
}
