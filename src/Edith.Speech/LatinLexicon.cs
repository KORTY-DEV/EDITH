using System.Text.RegularExpressions;

namespace Edith.Speech;

/// <summary>
/// How English names sound in Russian ("Haiku" → "хайку", "ChatGPT" → "чат джи пи ти"). Defaults are built in;
/// config/pronounce.json ({"слово": "как читать"}) adds or overrides, and is re-read when it changes.
/// </summary>
public static class LatinLexicon
{
    public const string DefaultPath = @"C:\EDITH\config\pronounce.json";

    public static string Path { get; set; } = DefaultPath;

    /// <summary>The owner's own words (not part of the repository), next to <see cref="Path"/>.</summary>
    private static string LocalPath => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path) ?? "", "pronounce.local.json");

    private static readonly Dictionary<string, string> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ChatGPT"] = "чат джи пи ти", ["GPT"] = "джи пи ти",
        ["OpenAI"] = "оупен эй ай", ["Gemini"] = "джемини", ["Obsidian"] = "обсидиан", ["Telegram"] = "телеграм", ["Python"] = "пайтон", ["GitHub"] = "гитхаб",
        ["Git"] = "гит", ["Tailscale"] = "тейлскейл", ["Qwen"] = "квен", ["Qwen3"] = "квен три", ["Ollama"] = "олама", ["GigaChat"] = "гигачат",
        ["GigaAM"] = "гига эй эм", ["Silero"] = "силеро", ["Vosk"] = "воск", ["Playwright"] = "плейрайт", ["Windows"] = "виндовс",
        ["Google"] = "гугл", ["YouTube"] = "ютуб", ["Wi-Fi"] = "вай фай", ["WiFi"] = "вай фай", ["USB"] = "юэсби", ["API"] = "эй пи ай",
        ["AI"] = "эй ай", ["Chrome"] = "хром", ["Edge"] = "эдж", ["Steam"] = "стим", ["Discord"] = "дискорд", ["Docker"] = "докер",
        ["Linux"] = "линукс", ["Android"] = "андроид", ["iPhone"] = "айфон", ["JSON"] = "джейсон", ["SQL"] = "эс кью эл", ["PDF"] = "пи ди эф",
        ["Opera GX"] = "опера джи икс", ["Opera"] = "опера", ["World of Tanks"] = "ворлд оф танкс", ["WoT"] = "вот", ["Lesta"] = "леста",
        ["EDITH"] = "эдит", ["Jarvis"] = "джарвис", ["Tesla"] = "тесла", ["Intel"] = "интел", ["AMD"] = "эй эм ди", ["Ryzen"] = "райзен",
        ["Kingston"] = "кингстон", ["Nvidia"] = "энвидиа", ["Hyper-V"] = "хайпер ви", ["WSL"] = "дабл ю эс эл", ["SSH"] = "эс эс эйч",
        ["Wikipedia"] = "википедия", ["Habr"] = "хабр", ["Playwright"] = "плейрайт", ["Visual Studio"] = "визуал студио", ["VS Code"] = "ви эс код",
    };

    private static readonly object Gate = new();
    private static DateTime _stamp = DateTime.MinValue;
    private static Regex? _pattern;
    private static Dictionary<string, string> _map = new(Defaults, StringComparer.OrdinalIgnoreCase);

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))
            return text;

        (Regex pattern, Dictionary<string, string> map) = Current();
        return pattern.Replace(text, m => map.TryGetValue(m.Value, out string? spoken) ? spoken : m.Value);
    }

    private static (Regex, Dictionary<string, string>) Current()
    {
        lock (Gate)
        {
            try
            {
                DateTime stamp = new[] { Path, LocalPath }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(DateTime.MinValue).Max();
                if (_pattern is not null && stamp == _stamp)
                    return (_pattern, _map);

                _stamp = stamp;
                var map = new Dictionary<string, string>(Defaults, StringComparer.OrdinalIgnoreCase);
                foreach (string file in new[] { Path, LocalPath }.Where(File.Exists))
                {
                    var extra = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                        File.ReadAllText(file),
                        new System.Text.Json.JsonSerializerOptions { ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true });
                    foreach ((string key, string value) in extra ?? [])
                        if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                            map[key.Trim()] = value.Trim();
                }

                _map = map;
                _pattern = new Regex(
                    @"(?<![\p{L}\d])(?:" + string.Join("|", map.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape)) + @")(?![\p{L}\d])",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch
            {
                _pattern ??= new Regex("(?!)");
            }

            return (_pattern!, _map);
        }
    }
}
