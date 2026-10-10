using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// "какая погода на ближайшие семь дней в самаре" → city "самаре", 7 days, from tomorrow no.
/// Days: 0 = now; "завтра" = 1 day starting tomorrow; "неделя" = 7; "N дней" = N (max 16).
/// </summary>
public sealed record WeatherQuery(string City, int Days, bool FromTomorrow)
{
    private static readonly Dictionary<string, int> Numerals = new()
    {
        ["один"] = 1, ["одного"] = 1, ["два"] = 2, ["двух"] = 2, ["три"] = 3, ["трех"] = 3, ["четыре"] = 4, ["четырех"] = 4,
        ["пять"] = 5, ["пяти"] = 5, ["шесть"] = 6, ["шести"] = 6, ["семь"] = 7, ["семи"] = 7, ["восемь"] = 8,
        ["девять"] = 9, ["десять"] = 10, ["десяти"] = 10, ["четырнадцать"] = 14,
    };

    private static readonly HashSet<string> NotCity =
    [
        "ближайшие", "ближайшую", "следующие", "следующую", "дней", "дня", "день", "неделю", "неделя", "недели", "выходные",
        "сейчас", "сегодня", "завтра", "послезавтра", "там", "какая", "какую", "будет", "погода", "погоду", "погоды", "прогноз",
        "скажи", "покажи", "расскажи", "а", "и", "на", "в", "во", "для", "по", "мне", "пожалуйста", "эдит", "ну",
    ];

    public static WeatherQuery Parse(string command, string defaultCity = "Самара")
    {
        string text = Regex.Replace(command.ToLowerInvariant().Replace('ё', 'е'), @"[?!,.]+", " ");
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        int days = 0;
        bool fromTomorrow = false;
        Match n = Regex.Match(text, @"(\d+|" + string.Join("|", Numerals.Keys) + @")\s+(?:дн|день|сут)");
        if (n.Success)
            days = int.TryParse(n.Groups[1].Value, out int d) ? d : Numerals[n.Groups[1].Value];
        else if (text.Contains("недел"))
            days = 7;
        else if (text.Contains("выходн"))
            days = 7;
        else if (text.Contains("послезавтра"))
            (days, fromTomorrow) = (2, true);
        else if (text.Contains("завтра"))
            (days, fromTomorrow) = (1, true);
        days = Math.Clamp(days, 0, 16);

        // City: the word(s) after "в/во", else the first word that is not about time.
        string city = "";
        for (int i = 0; i < words.Length - 1 && city.Length == 0; i++)
        {
            if (words[i] is not ("в" or "во"))
                continue;
            // "в нижнем новгороде на неделю" → "нижнем новгороде"
            city = string.Join(' ', words.Skip(i + 1).TakeWhile(w => !NotCity.Contains(w) && !Numerals.ContainsKey(w) && !w.All(char.IsDigit)).Take(3));
        }

        if (city.Length == 0)
        {
            int weather = Array.FindIndex(words, w => w.StartsWith("погод") || w == "прогноз");
            city = words.Skip(weather + 1)
                .FirstOrDefault(w => !NotCity.Contains(w) && !Numerals.ContainsKey(w) && !w.All(char.IsDigit) && !w.StartsWith("погод")) ?? "";
        }

        return new WeatherQuery(city.Length > 0 ? city : defaultCity, days, fromTomorrow);
    }

    /// <summary>
    /// Spellings to try with the geocoder, which wants the nominative: "самаре" → "самара",
    /// "казани" → "казань", "нижнем новгороде" → "нижний новгород".
    /// </summary>
    public static IReadOnlyList<string> CityCandidates(string city)
    {
        var result = new List<string> { city };
        string[] parts = city.Split(' ');
        string Each(Func<string, string> f) => string.Join(' ', parts.Select(f));

        result.Add(Each(w => Regex.Replace(w, "е$", "а")));
        result.Add(Each(w => Regex.Replace(w, "и$", "ь")));
        result.Add(Each(w => Regex.Replace(Regex.Replace(w, "ем$", "ий"), "е$", "")));
        result.Add(Each(w => Regex.Replace(w, "е$", "")));
        return result.Distinct().ToList();
    }
}
