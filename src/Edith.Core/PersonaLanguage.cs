using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>Guards the first-person EDITH voice against masculine model slips.</summary>
public static class PersonaLanguage
{
    private static readonly (string Pattern, string Replacement)[] Corrections =
    [
        ("\\bя готов помочь\\b", "я готова помочь"),
        ("\\bя готов\\b", "я готова"),
        ("\\bя наш[её]л\\b", "я нашла"),
        ("\\bя проверил\\b", "я проверила"),
        ("\\bя запомнил\\b", "я запомнила"),
        ("\\bя смог\\b", "я смогла"),
        ("\\bя уверен\\b", "я уверена"),
        ("\\bя собрал\\b", "я собрала"),
        ("\\bя ответил\\b", "я ответила"),
        ("\\bя рад\\b", "я рада"),
        ("\\bбуду рад\\b", "буду рада"),
        ("\\bя понял\\b", "я поняла"),
        ("\\bя сделал\\b", "я сделала"),
        ("\\bя открыл\\b", "я открыла"),
        ("\\bя закрыл\\b", "я закрыла"),
        ("\\bя нашел\\b", "я нашла")
    ];

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        string result = text;
        foreach ((string pattern, string replacement) in Corrections)
        {
            // Keep the case of the first letter: "Я готов" → "Я готова", not "я готова".
            result = Regex.Replace(
                result,
                pattern,
                match => char.IsUpper(match.Value[0])
                    ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
                    : replacement,
                RegexOptions.IgnoreCase);
        }

        // "Рад быть твоим помощником": a bare "рад" is EDITH about herself unless someone
        // else is named right before it ("он рад", "ты рад"). Case is kept.
        result = Regex.Replace(
            result,
            @"(?<!\b(?i:он|ты|вы|они|будет|будешь)\s)\b([Рр])ад\b",
            "$1ада");

        return result;
    }
}
