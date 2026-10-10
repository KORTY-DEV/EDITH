using System.Text.RegularExpressions;

namespace Edith.Core.Memory;

public static class MemoryPolicy
{
    /// <summary>Preference key holding how EDITH addresses the user ("сэр", a name, ...).</summary>
    public const string AddressKey = "обращение";

    private static readonly string[] RecallPhrases =
    [
        "что ты помнишь обо мне",
        "что ты знаешь обо мне",
        "что я просил запомнить",
        "что я просила запомнить",
        "расскажи что ты обо мне помнишь",
        "расскажи что ты обо мне знаешь",
        "что ты помнишь"
    ];

    // "мою маму зовут Ольга" → key "мама". Accusative/genitive forms as they are spoken.
    private static readonly (string Pattern, string Key)[] NamedRelations =
    [
        ("мою девушку", "девушка"),
        ("моего парня", "парень"),
        ("мою жену", "жена"),
        ("моего мужа", "муж"),
        ("мою маму", "мама"),
        ("мою мать", "мама"),
        ("моего папу", "папа"),
        ("моего отца", "папа"),
        ("моего брата", "брат"),
        ("мою сестру", "сестра"),
        ("моего сына", "сын"),
        ("мою дочь", "дочь"),
        ("моего друга", "друг"),
        ("мою подругу", "подруга"),
        ("моего кота", "кот"),
        ("мою кошку", "кошка"),
        ("мою собаку", "собака"),
        ("моего пса", "собака"),
        ("меня", "пользователь"),
    ];

    public static bool IsRecallQuery(string text)
    {
        string normalized = Regex.Replace(LocalMemoryStore.Normalize(text), @"[,?!.;:]", " ");
        normalized = Regex.Replace(normalized, @"\s+", " ").Trim();
        return RecallPhrases.Any(normalized.Contains);
    }

    public static bool TryExtractSave(string text, out MemoryType type, out string key, out string value)
    {
        string input = LocalMemoryStore.Normalize(text)
            .Replace("нет, ", "", StringComparison.Ordinal);
        type = MemoryType.UserFact; key = value = "";

        Match favorite = Regex.Match(input, @"(?:мой|моя)?\s*любим(?:ый|ая) (?<key>[^ ]+)\s+(?:это|[-—])?\s*(?<value>.+)$");
        if (favorite.Success) { type = MemoryType.Preference; key = "любимый " + favorite.Groups["key"].Value; value = favorite.Groups["value"].Value; return true; }

        if (TryExtractNamed(input, out key, out value)) { type = MemoryType.Person; return true; }

        Match address = Regex.Match(input, @"^(?:запомни,?\s+)?(?:называй меня|обращайся ко мне|зови меня)\s+(?<value>.+)$");
        if (address.Success) { type = MemoryType.Preference; key = AddressKey; value = CleanValue(address.Groups["value"].Value); return value.Length > 0; }

        Match birthday = Regex.Match(input, @"^(?:запомни,?\s+(?:что\s+)?)?(?:мой день рождения|у меня день рождения)\s+(?:—\s*|-\s*)?(?<value>.+)$");
        if (birthday.Success) { key = "день рождения"; value = CleanValue(birthday.Groups["value"].Value); return value.Length > 0; }

        Match remember = Regex.Match(input, @"(?:запомни,? что|запомни)\s+(?<value>.+)$");
        if (remember.Success)
        {
            value = remember.Groups["value"].Value;

            // Explicit "запомни" makes these everyday phrases safe to store as structured facts.
            Match lives = Regex.Match(value, @"^я живу (?:в|во)\s+(?<value>.+)$");
            if (lives.Success) { key = "город"; value = CleanValue(lives.Groups["value"].Value); return true; }
            Match works = Regex.Match(value, @"^я работаю\s+(?<value>.+)$");
            if (works.Success) { key = "работа"; value = CleanValue(works.Groups["value"].Value); return true; }

            key = value.Length > 72 ? value[..72] : value;
            return true;
        }

        Match prefer = Regex.Match(input, @"я предпочитаю\s+(?<value>.+)$");
        if (prefer.Success) { type = MemoryType.Preference; value = prefer.Groups["value"].Value; key = "предпочтение"; return true; }
        return false;
    }

    public static bool TryExtractForget(string text, out string query)
    {
        Match match = Regex.Match(LocalMemoryStore.Normalize(text), @"(?:забудь|удали из памяти)\s+(?<query>.+)$");
        query = match.Success ? match.Groups["query"].Value.Trim() : "";
        return query.Length > 0;
    }

    private static bool TryExtractNamed(string input, out string key, out string value)
    {
        key = value = "";
        foreach ((string pattern, string relation) in NamedRelations)
        {
            Match match = Regex.Match(input, @"(?:^|\s)" + Regex.Escape(pattern) + @"\s+зовут\s+(?<value>.+)$");
            if (match.Success)
            {
                key = relation;
                value = CleanValue(match.Groups["value"].Value);
                return value.Length > 0;
            }
        }

        return false;
    }

    private static string CleanValue(string value) => value.Trim().TrimEnd('.', '!', '?').Trim();
}
