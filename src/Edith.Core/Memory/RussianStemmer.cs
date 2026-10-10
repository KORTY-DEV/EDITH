using System.Text;

namespace Edith.Core.Memory;

/// <summary>
/// Very light Russian stemmer for memory search: strips one inflection ending so that
/// "девушку" and "девушка" meet at "девушк". Not linguistically complete on purpose:
/// it only has to make full-text search forgiving until embeddings arrive.
/// </summary>
public static class RussianStemmer
{
    private static readonly string[] Endings =
    [
        "иями", "ями", "ами", "иях", "ого", "его", "ому", "ему", "ыми", "ими",
        "ях", "ах", "ой", "ей", "ий", "ый", "ая", "яя", "ое", "ее", "ые", "ие",
        "ую", "юю", "ом", "ем", "ам", "ям", "ов", "ев", "ью",
        "а", "я", "у", "ю", "ы", "и", "е", "о", "ь", "й",
    ];

    private static readonly HashSet<string> StopWords =
    [
        "и", "в", "во", "на", "с", "со", "по", "к", "ко", "у", "о", "об", "от", "до", "из", "за", "для", "про",
        "а", "но", "или", "ли", "же", "бы", "не", "ни", "да", "нет", "то", "это", "как", "так", "что", "чем",
        "я", "ты", "мы", "вы", "он", "она", "они", "меня", "мне", "мной", "тебя", "тебе", "нас", "нам",
        "мой", "моя", "мое", "мои", "моего", "моей", "мою", "моих", "твой", "твоя", "твое", "твои",
        "какой", "какая", "какое", "какие", "кто", "где", "когда", "почему", "зачем", "сколько",
        "эдит", "скажи", "помнишь", "знаешь", "есть", "был", "была", "было", "были",
    ];

    public static string Stem(string word)
    {
        word = Normalize(word);
        if (word.Length <= 3)
            return word;

        foreach (string ending in Endings)
        {
            if (word.Length - ending.Length >= 3 && word.EndsWith(ending, StringComparison.Ordinal))
                return word[..^ending.Length];
        }

        return word;
    }

    /// <summary>Meaningful stems of <paramref name="text"/>: stop words and 1-2 letter tokens dropped.</summary>
    public static IReadOnlyList<string> Stems(string text) =>
        Words(text)
            .Where(word => word.Length > 2 && !StopWords.Contains(word))
            .Select(Stem)
            .Distinct()
            .ToList();

    public static IEnumerable<string> Words(string text)
    {
        var current = new StringBuilder();
        foreach (char c in Normalize(text))
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                yield return current.ToString();
                current.Clear();
            }
        }

        if (current.Length > 0)
            yield return current.ToString();
    }

    public static string Normalize(string text) =>
        text.Trim().ToLowerInvariant().Replace('ё', 'е');
}
