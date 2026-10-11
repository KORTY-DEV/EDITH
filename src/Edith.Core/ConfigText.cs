using System.Globalization;
using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Reads and changes single values in EDITH's hand-edited JSON configs without touching their
/// comments and layout. A key path like "gaming.enabled" finds "enabled" after "gaming".
/// </summary>
public static class ConfigText
{
    private const string Value = @"(?<v>""(?:[^""\\]|\\.)*""|-?\d+(?:\.\d+)?|true|false|null)";

    public static string? Get(string json, string keyPath)
    {
        Match? match = Find(json, keyPath);
        if (match is null)
            return null;

        string raw = match.Groups["v"].Value;
        // JSON escapes, not regex ones: "C:\\EDITH\\secrets" is a Windows path.
        if (!raw.StartsWith('"'))
            return raw;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<string>(raw);
        }
        catch (System.Text.Json.JsonException)
        {
            // Hand-edited path with single backslashes: take it as written.
            return raw[1..^1];
        }
    }

    public static double? GetNumber(string json, string keyPath) =>
        double.TryParse(Get(json, keyPath), NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    /// <summary>Replaces an existing value; returns the text unchanged when the key is missing.</summary>
    public static string Set(string json, string keyPath, object value)
    {
        Match? match = Find(json, keyPath);
        if (match is null)
            return json;

        Group v = match.Groups["v"];
        return json[..v.Index] + Format(value) + json[(v.Index + v.Length)..];
    }

    public static string Format(object value) => value switch
    {
        bool b => b ? "true" : "false",
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => "\"" + value.ToString()!.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
    };

    private static Match? Find(string json, string keyPath)
    {
        int start = 0;
        // A key mentioned in a comment line ("// \"enabled\": false by default") is not the key: blank comments out, keeping every index.
        json = Regex.Replace(json, @"(?m)^[ 	]*//.*$", m => new string(' ', m.Length));
        string[] keys = keyPath.Split('.');
        for (int i = 0; i < keys.Length; i++)
        {
            bool last = i == keys.Length - 1;
            var regex = new Regex("\"" + Regex.Escape(keys[i]) + "\"\\s*:\\s*" + (last ? Value : ""));
            Match match = regex.Match(json, start);
            if (!match.Success)
                return null;
            if (last)
                return match;
            start = match.Index + match.Length;
        }

        return null;
    }
}
