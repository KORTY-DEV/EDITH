namespace Edith.Core.Memory;

/// <summary>Turns structured memory into a short, natural spoken summary.</summary>
public static class MemorySummaryFormatter
{
    private static readonly Dictionary<string, string> RelationPhrases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["девушка"] = "твою девушку зовут",
        ["парень"] = "твоего парня зовут",
        ["жена"] = "твою жену зовут",
        ["муж"] = "твоего мужа зовут",
        ["мама"] = "твою маму зовут",
        ["папа"] = "твоего папу зовут",
        ["брат"] = "твоего брата зовут",
        ["сестра"] = "твою сестру зовут",
        ["сын"] = "твоего сына зовут",
        ["дочь"] = "твою дочь зовут",
        ["друг"] = "твоего друга зовут",
        ["подруга"] = "твою подругу зовут",
        ["кот"] = "твоего кота зовут",
        ["кошка"] = "твою кошку зовут",
        ["собака"] = "твою собаку зовут",
    };

    private static readonly Dictionary<string, string> RelationNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["девушка"] = "девушка", ["парень"] = "парень", ["жена"] = "жена", ["муж"] = "муж",
        ["мама"] = "мама", ["папа"] = "папа", ["брат"] = "брат", ["сестра"] = "сестра",
        ["сын"] = "сын", ["дочь"] = "дочь", ["друг"] = "друг", ["подруга"] = "подруга",
        ["кот"] = "кот", ["кошка"] = "кошка", ["собака"] = "собака",
    };

    /// <summary>
    /// A short human summary instead of a list: "Знаю, что ты Павел. Из близких — мама Татьяна.
    /// Ещё помню: твой любимый цвет — синий." Used when the brain cannot retell it itself.
    /// </summary>
    public static string Natural(IReadOnlyList<MemoryItem> items)
    {
        List<MemoryItem> active = items.Where(i => i.Active).ToList();
        if (active.Count == 0)
            return "Пока почти ничего о тебе не знаю. Расскажи что-нибудь — запомню.";

        string? name = active.FirstOrDefault(i => i.Type == MemoryType.Person && i.Key == "пользователь")?.Content;
        List<string> relatives = active
            .Where(i => i.Type == MemoryType.Person && RelationNames.ContainsKey(i.Key))
            .Select(i => $"{RelationNames[i.Key]} {NameCase(i.Content)}")
            .ToList();
        List<string> rest = active
            .Where(i => !(i.Type == MemoryType.Person && (i.Key == "пользователь" || RelationNames.ContainsKey(i.Key))))
            .Where(i => !(i.Key == MemoryPolicy.AddressKey && name is not null &&
                          LocalMemoryStore.Normalize(i.Content) == LocalMemoryStore.Normalize(name)))
            .Take(4)
            .Select(FormatItem)
            .ToList();

        var sentences = new List<string>();
        if (name is not null)
            sentences.Add($"Знаю, что ты {NameCase(name)}.");
        if (relatives.Count > 0)
            sentences.Add($"Из близких — {string.Join(", ", relatives)}.");
        if (rest.Count > 0)
            sentences.Add((sentences.Count == 0 ? "Помню, что " : "Ещё помню: ") + string.Join("; ", rest) + ".");

        return string.Join(" ", sentences);
    }

    private static string NameCase(string text) =>
        string.Join(' ', text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    public static string Format(IReadOnlyList<MemoryItem> items, bool hasMore = false)
    {
        if (items.Count == 0)
            return "Пока ничего важного не сохранила.";

        string body = string.Join("; ", items.Select(FormatItem));
        string result = $"Помню, что {body}.";
        return hasMore ? result + " Могу перечислить остальное." : result;
    }

    public static string FormatItem(MemoryItem item)
    {
        string key = item.Key.Trim();
        string content = item.Content.Trim().TrimEnd('.', '!', '?');
        if (key.StartsWith("любимый ", StringComparison.OrdinalIgnoreCase))
            return $"твой {key} — {content}";

        if (item.Type == MemoryType.Person &&
            key.Equals("пользователь", StringComparison.OrdinalIgnoreCase))
            return $"тебя зовут {content}";

        if (item.Type == MemoryType.Person &&
            RelationPhrases.TryGetValue(key, out string? relation))
            return $"{relation} {content}";

        if (item.Type == MemoryType.Preference &&
            key.Equals("предпочтение", StringComparison.OrdinalIgnoreCase))
            return $"ты предпочитаешь {content}";

        if (item.Type == MemoryType.Preference &&
            key.Equals(MemoryPolicy.AddressKey, StringComparison.OrdinalIgnoreCase))
            return $"к тебе нужно обращаться «{content}»";

        if (item.Type == MemoryType.UserFact)
        {
            switch (key)
            {
                case "город":
                    return $"ты живёшь в {content}";
                case "работа":
                    return $"ты работаешь {content}";
                case "день рождения":
                    return $"твой день рождения — {content}";
            }
        }

        return string.IsNullOrWhiteSpace(key) || key == LocalMemoryStore.Normalize(content)
            ? content
            : $"{key} — {content}";
    }
}
