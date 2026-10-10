using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Does the recognised phrase stop mid-sentence? "открой и" / "найди мне в" / "напомни что" are
/// cut off: no Russian command ends on a preposition, a conjunction or "что/как".
/// </summary>
public static class TurnText
{
    private static readonly HashSet<string> Dangling =
    [
        "в", "во", "на", "по", "за", "из", "от", "до", "к", "ко", "с", "со", "о", "об", "обо", "у", "для", "про",
        "без", "над", "под", "при", "через", "между", "после", "перед", "из-за", "из-под",
        "и", "а", "но", "или", "да", "что", "чтобы", "как", "если", "когда", "потому", "тогда", "пока", "ну", "ещё", "еще",
        "мне", "мой", "моя", "мои", "моё", "мою", "твой", "этот", "эту", "эти", "тот", "тех", "который", "которая", "которые",
        "самый", "самая", "самое", "какой", "какая", "какое", "какие", "сколько", "где", "куда", "откуда", "почему",
    ];

    public static bool EndsMidSentence(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string[] words = Regex.Split(text.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\-]+")
            .Where(w => w.Length > 0)
            .ToArray();
        if (words.Length == 0)
            return false;

        string last = words[^1];
        // A lone "да" is an answer ("Хотите услышать продолжение?" — "да"), not the start of "да и ...".
        if (words.Length == 1 && last == "да")
            return false;

        // "ещё" ends a phrase as often as it opens one ("давай ещё"): only the clear cases count.
        return last != "еще" && last != "ну" && Dangling.Contains(last);
    }
}
