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
    public static string ForSpeech(string text, int maxChars = 900)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // URLs and raw JSON are useful to a UI, but are noise when spoken.
        string cleaned = System.Text.RegularExpressions.Regex.Replace(
            ReplyPolish.Apply(text), @"(?:https?://|www\.)\S+", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\{[^{}]*\}", " ");
        cleaned = cleaned.Replace("{", " ").Replace("}", " ").Replace("\"", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\b(?:type|text|answer|source|url)\s*:\s*", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // List markers go before numbers become words ("1. пункт" must not turn into "один. пункт").
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(?m)^\s*(?:[-•]|\d+[.)])\s+", "");
        return Sanitize(RussianNumbers.Verbalize(SpeechNormalizer.Normalize(cleaned)), maxChars);
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
            if (c is '*' or '`' or '#' or '_' or '~' or '|')
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
