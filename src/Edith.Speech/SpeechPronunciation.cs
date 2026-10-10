using System.Text.RegularExpressions;

namespace Edith.Speech;

/// <summary>Provider-specific speech spelling; display/context text remains unchanged.</summary>
public static class SpeechPronunciation
{
    public static string Apply(string text, string provider, int ratePercent = 100, bool pauses = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        // Currency words are provider-independent: no provider should receive
        // a large price as bare digits and leave its pronunciation to chance.
        string spoken = RussianSpeechText.NormalizeCurrency(text);
        string marked = Regex.Replace(spoken, @"(?<![\p{L}])Эдит(?![\p{L}])", "+Эдит", RegexOptions.IgnoreCase);
        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase) || provider.Equals("silero", StringComparison.OrdinalIgnoreCase))
        {
            // Silero's Russian model benefits from explicit Russian spell-outs
            // for Latin technical abbreviations and the soft "тест" vowel.
            marked = Regex.Replace(marked, @"(?<![\p{L}])NVIDIA(?![\p{L}])", "энвидиа", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])GeForce(?![\p{L}])", "джи форс", RegexOptions.IgnoreCase);
            // Keep the model number attached to RTX: saying only "эр ти икс"
            // makes long GPU answers sound truncated and loses the key product.
            marked = Regex.Replace(marked, @"(?<![\p{L}])RTX\s+5070\s+Ti(?![\p{L}])", "эр ти икс пятьдесят семьдесят ти-ай", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])RTX\s+5090(?![\p{L}])", "эр ти икс пятьдесят девяносто", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])RTX\s+5080(?![\p{L}])", "эр ти икс пятьдесят восемьдесят", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])RTX(?![\p{L}])", "эр ти икс", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])SSD(?![\p{L}])", "эс эс ди", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])HDD(?![\p{L}])", "эйч ди ди", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])RAM(?![\p{L}])", "рам", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])CPU(?![\p{L}])", "си пи ю", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])GPU(?![\p{L}])", "джи пи ю", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])тестами(?![\p{L}])", "т+естами", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])тестам(?![\p{L}])", "т+естам", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])тесты(?![\p{L}])", "т+есты", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])тестах(?![\p{L}])", "т+естах", RegexOptions.IgnoreCase);
            marked = Regex.Replace(marked, @"(?<![\p{L}])тест(?![\p{L}])", "т+ест", RegexOptions.IgnoreCase);
        }

        if (provider.Equals("local", StringComparison.OrdinalIgnoreCase) || provider.Equals("silero", StringComparison.OrdinalIgnoreCase))
        {
            marked = SpokenNumbers.Apply(marked);
            marked = LatinLexicon.Apply(marked);
            marked = StressDictionary.Apply(marked);
        }

        if (pauses && (provider.Equals("local", StringComparison.OrdinalIgnoreCase) || provider.Equals("silero", StringComparison.OrdinalIgnoreCase)))
            marked = Pauses(marked);
        return provider.Equals("yandex", StringComparison.OrdinalIgnoreCase)
            ? marked
            : provider.Equals("local", StringComparison.OrdinalIgnoreCase) || provider.Equals("silero", StringComparison.OrdinalIgnoreCase)
                ? (ratePercent == 100 ? $"<speak>{marked}</speak>" : $"<speak><prosody rate=\"{ratePercent}%\">{marked}</prosody></speak>")
                : spoken;
    }

    /// <summary>
    /// A person breathes between thoughts: short pauses after a comma, longer after a dash or colon,
    /// a beat after an opener ("Хм", "Ну", "Слушайте") and a real pause at an ellipsis.
    /// </summary>
    public static string Pauses(string text)
    {
        // Silero already breathes at every comma: extra pauses there made speech jerky. Only what it does not do by itself.
        text = Regex.Replace(text, @"(?<![\p{L}])(Хм+|Ну|Так|Слушайте|Знаете|Итак|Видите ли|Кстати|Впрочем)(,?)\s+", m => m.Groups[1].Value + ",<break time=\"180ms\"/> ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+[—–-]\s+", " —<break time=\"140ms\"/> ");
        text = Regex.Replace(text, @"(?<=\p{L}):\s+", ":<break time=\"120ms\"/> ");
        text = Regex.Replace(text, @"(?:\.{3}|…)\s*", "…<break time=\"420ms\"/> ");
        return text;
    }
}

/// <summary>
/// Stress fixes for words Silero gets wrong ("виденье" → "вид+енье"), from config/stress.json:
/// { "виденье": "вид+енье" }. Re-read when the file changes, so a word is fixed without a rebuild.
/// </summary>
public static class StressDictionary
{
    public const string DefaultPath = @"C:\EDITH\config\stress.json";

    private static readonly object Gate = new();
    private static DateTime _stamp = DateTime.MinValue;
    private static (Regex Pattern, string Stressed)[] _rules = [];

    public static string Path { get; set; } = DefaultPath;

    public static string Apply(string text)
    {
        foreach ((Regex pattern, string stressed) in Rules())
            text = pattern.Replace(text, m => char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(stressed[0]) + stressed[1..] : stressed);
        return text;
    }

    private static (Regex, string)[] Rules()
    {
        lock (Gate)
        {
            try
            {
                DateTime stamp = File.Exists(Path) ? File.GetLastWriteTimeUtc(Path) : DateTime.MinValue;
                if (stamp == _stamp)
                    return _rules;

                _stamp = stamp;
                var map = stamp == DateTime.MinValue
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                          File.ReadAllText(Path),
                          new System.Text.Json.JsonSerializerOptions { ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
                      ?? new Dictionary<string, string>();

                _rules = map
                    .Where(p => !string.IsNullOrWhiteSpace(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
                    .OrderByDescending(p => p.Key.Length)
                    .Select(p => (new Regex(@"(?<![\p{L}+])" + Regex.Escape(p.Key.Trim()) + @"(?![\p{L}])", RegexOptions.IgnoreCase | RegexOptions.Compiled), p.Value.Trim().ToLowerInvariant()))
                    .ToArray();
            }
            catch
            {
                // A broken file must not silence EDITH: keep the previous rules.
            }

            return _rules;
        }
    }
}
