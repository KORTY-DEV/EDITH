using System.Text;
using System.Text.Json;

namespace Edith.Core.Brain;

public sealed record BrainDecision(
    string Type,
    string Text,
    string Tool,
    JsonElement Arguments);

/// <summary>
/// Turns the raw model output into a decision. Local models sometimes wrap the JSON in a code
/// fence or add a sentence around it; anything that is not a decision object becomes a plain answer.
/// </summary>
public static class BrainDecisionParser
{
    private static readonly JsonElement EmptyObject =
        JsonDocument.Parse("{}").RootElement.Clone();

    public static BrainDecision Parse(string raw)
    {
        raw = (raw ?? "").Trim();

        string candidate = StripFence(raw);

        if (TryParseObject(candidate, out BrainDecision? direct))
        {
            return direct!;
        }

        string? embedded = ExtractFirstObject(candidate);

        if (embedded is not null && TryParseObject(embedded, out BrainDecision? inner))
        {
            return inner!;
        }

        return new BrainDecision("answer", raw, "", EmptyObject);
    }

    private static bool TryParseObject(string json, out BrainDecision? decision)
    {
        decision = null;

        if (json.Length == 0 || json[0] != '{')
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            string type = ReadString(root, "type").Trim().ToLowerInvariant();
            string text = ReadString(root, "text");
            string tool = ReadString(root, "tool");

            // {"answer":"..."} — the model dropped the envelope.
            if (type.Length == 0 && tool.Length == 0)
            {
                string bare = ReadString(root, "answer");

                if (bare.Length == 0)
                {
                    bare = text;
                }

                if (bare.Length > 0)
                {
                    decision = new BrainDecision("answer", bare, "", EmptyObject);
                    return true;
                }

                return false;
            }

            // Tolerate {"tool":"x"} with a missing type.
            if (type.Length == 0)
            {
                type = "tool";
            }

            JsonElement arguments =
                root.TryGetProperty("arguments", out JsonElement args) &&
                args.ValueKind == JsonValueKind.Object
                    ? args.Clone()
                    : EmptyObject;

            decision = new BrainDecision(type, text, tool, arguments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() ?? ""
            : "";

    private static string StripFence(string raw)
    {
        if (!raw.StartsWith("```", StringComparison.Ordinal))
        {
            return raw;
        }

        int firstNewLine = raw.IndexOf('\n');

        if (firstNewLine >= 0)
        {
            raw = raw[(firstNewLine + 1)..];
        }

        int lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);

        if (lastFence >= 0)
        {
            raw = raw[..lastFence];
        }

        return raw.Trim();
    }

    private static string? ExtractFirstObject(string text)
    {
        int start = text.IndexOf('{');

        if (start < 0)
        {
            return null;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return text[start..(i + 1)];
        }

        return null;
    }
}

/// <summary>Makes a model reply fit for TTS: no markdown, one short paragraph.</summary>
public static class VoiceText
{
    /// <summary>
    /// Digits to words with the richer rules of the speech layer (dates, signs, video cards, fractions); set by the app at start.
    /// The plain <see cref="RussianNumbers"/> still runs after it as a safety net for whatever digits are left.
    /// </summary>
    public static Func<string, string> NumberWords { get; set; } = t => t;

    private static readonly (string Pattern, string Spoken)[] Abbreviations =
    [
        (@"(?<![\p{L}])и\s+т\.\s?д\.", "и так далее"),
        (@"(?<![\p{L}])и\s+т\.\s?п\.", "и тому подобное"),
        (@"(?<![\p{L}])т\.\s?е\.", "то есть"),
        (@"(?<![\p{L}])т\.\s?к\.", "так как"),
        (@"(?<![\p{L}])т\.\s?д\.", "так далее"),
        (@"(?<=\d)\s*тыс\.?(?![\p{L}])", " тысяч"),
        (@"(?<=\d)\s*млн\.?(?![\p{L}])", " миллионов"),
        (@"(?<=\d)\s*млрд\.?(?![\p{L}])", " миллиардов"),
        (@"(?<![\p{L}])руб\.", "рублей"),
    ];

    private static string Units(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\d.,])(?<n>\d+)\s*(?<u>км/ч|м/с|(?:тыс|млн|млрд)\.?)(?![\p{L}])", m =>
        {
            long n = long.TryParse(m.Groups["n"].Value, out long v) ? v : 0;
            string words = m.Groups["u"].Value.TrimEnd('.') switch
            {
                "км/ч" => RussianNumbers.Plural(n, "километр", "километра", "километров") + " в час",
                "м/с" => RussianNumbers.Plural(n, "метр", "метра", "метров") + " в секунду",
                "тыс" => RussianNumbers.Plural(n, "тысяча", "тысячи", "тысяч"),
                "млн" => RussianNumbers.Plural(n, "миллион", "миллиона", "миллионов"),
                _ => RussianNumbers.Plural(n, "миллиард", "миллиарда", "миллиардов"),
            };
            return m.Groups["n"].Value + " " + words;
        });


    private static readonly Dictionary<char, string> DriveNames = new()
    {
        ['C'] = "си", ['D'] = "ди", ['E'] = "и", ['F'] = "эф", ['G'] = "джи", ['H'] = "эйч", ['Z'] = "зет", ['A'] = "эй", ['B'] = "би",
    };

    /// <summary>"C:\EDITH\config\a.json" is "диск си, EDITH, config, a точка json": drives and slashes are not read as symbols.</summary>
    private static string Paths(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}\d])(?<d>[A-Za-z]):[\\/](?<rest>[^\s,;:()«»""]*)", m =>
        {
            char drive = char.ToUpperInvariant(m.Groups["d"].Value[0]);
            string[] parts = m.Groups["rest"].Value.TrimEnd('.').Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            string tail = string.Join(", ", parts.Select(part => part.Replace(".", " точка ").Trim()));
            string end = m.Groups["rest"].Value.EndsWith('.') ? "." : "";
            return "диск " + (DriveNames.TryGetValue(drive, out string? name) ? name : drive.ToString()) + (tail.Length > 0 ? ", " + tail.TrimEnd('.', ' ') : "") + end;
        });

    /// <summary>"в г. Самара" → "в городе Самара"; "ул. Мира, д. 5, кв. 12" → "улица Мира, дом 5, квартира 12". A year ("2026 г.") is not touched.</summary>
    private static string PlaceAbbreviations(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<prep>(?<![\p{L}])(?:в|на|о|об|из|от|до|для|около|возле|к|по)\s+)?(?<![\d])(?<![\d]\s)г\.\s*(?=[А-ЯЁ])", m =>
        {
            string prep = m.Groups["prep"].Value.Trim().ToLowerInvariant();
            string city = prep switch
            {
                "в" or "на" or "о" or "об" => "городе",
                "из" or "от" or "до" or "для" or "около" or "возле" => "города",
                "к" or "по" => "городу",
                _ => "город",
            };
            return m.Groups["prep"].Value + city + " ";
        });
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}])ул\.\s*(?=[А-ЯЁ\d])", "улица ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}])д\.\s*(?=\d)", "дом ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}])кв\.\s*(?=\d)", "квартира ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<![\p{L}])им\.\s*(?=[А-ЯЁ])", "имени ");
        return text;
    }

    public static string ForSpeech(string text, int maxChars = 900)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var rx = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        string cleaned = ReplyPolish.Apply(text);
        // Reasoning tags and code are not for the ear.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"<think>.*?</think>", " ", System.Text.RegularExpressions.RegexOptions.Singleline | rx);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"```.*?```", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"</?[A-Za-z][^>]*>", " ");
        // "[здесь](https://...)" keeps its words.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\[([^\]]+)\]\((?:https?://|www\.)[^)\s]*\)", "$1", rx);
        // URLs and raw JSON are useful to a UI, but are noise when spoken.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?:https?://|www\.)\S+", " ", rx);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\{[^{}]*\}", " ");
        cleaned = cleaned.Replace("{", " ").Replace("}", " ").Replace("\"", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\b(?:type|text|answer|source|url)\s*:\s*", " ", rx);
        // Emoji and pictographs: the voice would skip or stumble on them.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\uD800-\uDBFF][\uDC00-\uDFFF]|[\u2190-\u21FF\u2300-\u23FF\u2600-\u27BF\u2B00-\u2BFF\uFE00-\uFE0F\u200D]", " ");
        // List markers go before numbers become words ("1. пункт" must not turn into "один. пункт"); each item becomes a sentence.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?m)^\s*(?:[-•]|\d+[.)])\s+", "");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?<=[\p{L}\d\)])[ \t]*\r?\n+(?=\s*\S)", ". ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+([!?.,;:…])", "$1");
        cleaned = Paths(cleaned);
        cleaned = PlaceAbbreviations(cleaned);
        cleaned = Units(cleaned);
        foreach ((string pattern, string spoken) in Abbreviations)
            cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, pattern, spoken, rx);

        return Sanitize(RussianNumbers.Verbalize(SpeechNormalizer.Normalize(NumberWords(cleaned))), maxChars);
    }

    public static string Sanitize(string text, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var sb = new StringBuilder(text.Length);

        foreach (char c in text)
        {
            if (c == '_')
            {
                sb.Append(' '); // snake_case is two words, _emphasis_ costs a space
                continue;
            }

            if (c is '*' or '`' or '#' or '~' or '|')
            {
                continue;
            }

            sb.Append(c);
        }

        string s = sb.ToString();

        // "- item" / "1. item" list markers at line starts read badly aloud.
        s = System.Text.RegularExpressions.Regex.Replace(
            s, @"(?m)^\s*(?:[-•]|\d+[.)])\s+", "");

        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s*\r?\n\s*", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ").Trim();

        if (s.Length <= maxChars)
        {
            return LimitWords(s, 150);
        }

        // Cut at the last sentence end that fits, else at a word boundary.
        string head = s[..maxChars];
        int end = head.LastIndexOfAny(new[] { '.', '!', '?' });

        if (end >= maxChars / 3)
        {
            return LimitWords(head[..(end + 1)], 150);
        }

        int space = head.LastIndexOf(' ');
        return LimitWords((space > 0 ? head[..space] : head).TrimEnd(',', ';', ':') + "…", 150);
    }

    private static string LimitWords(string text, int maxWords)
    {
        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maxWords ? text : string.Join(' ', words.Take(maxWords)) + "…";
    }
}
