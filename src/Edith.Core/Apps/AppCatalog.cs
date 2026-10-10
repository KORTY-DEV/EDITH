using System.Text.Json;
using System.Text.RegularExpressions;
using Edith.Core.Memory;

namespace Edith.Core.Apps;

/// <summary>
/// An application EDITH can open, close or switch to by its spoken name.
/// <see cref="Processes"/> are process names without ".exe"; <see cref="Shortcuts"/> are
/// Start-menu/desktop shortcut names; <see cref="Launch"/> is a fallback shell target.
/// </summary>
public sealed record AppEntry
{
    public string Id { get; init; } = "";

    /// <summary>Name EDITH says back ("Discord", "Мир танков").</summary>
    public string Title { get; init; } = "";

    public IReadOnlyList<string> Aliases { get; init; } = [];

    public IReadOnlyList<string> Processes { get; init; } = [];

    public IReadOnlyList<string> Shortcuts { get; init; } = [];

    public string? Launch { get; init; }

    /// <summary>False for things EDITH must never close (the shell, herself).</summary>
    public bool CanClose { get; init; } = true;

    /// <summary>Closing asks first: games mid-battle, editors that may host other work.</summary>
    public bool ConfirmClose { get; init; }
}

public static class AppCatalog
{
    /// <summary>Processes EDITH never closes, whatever they are called by voice.</summary>
    public static readonly IReadOnlySet<string> ProtectedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass", "smss", "svchost", "system",
        "Edith.App", "ollama", "ollama app", "python", "pythonw", "dotnet",
        "powershell", "pwsh", "cmd", "conhost", "WindowsTerminal", "OpenConsole", "bash", "wsl",
    };

    /// <summary>The built-in protected list plus the owner's own (config/protected-processes.txt).</summary>
    public static bool IsProtected(string process) =>
        ProtectedProcesses.Contains(process) || LocalLists.Read("protected-processes.txt").Contains(process, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<AppEntry> Defaults { get; } =
    [
        new() { Id = "tanks", Title = "Мир танков", Aliases = ["танки", "мир танков", "ворлд оф танкс", "world of tanks", "вот"],
                Processes = ["WorldOfTanks", "wot"], Shortcuts = ["Мир танков"], ConfirmClose = true },
        new() { Id = "most-tanki", Title = "Most Tanki", Aliases = ["мост танки", "most tanki"],
                Processes = ["MostTanki", "Tanki"], Shortcuts = ["Most Tanki"], ConfirmClose = true },
        new() { Id = "lesta", Title = "Lesta Game Center", Aliases = ["леста", "лесту", "гейм центр", "игровой центр", "лаунчер"],
                Processes = ["lgc", "lgc_api"], Shortcuts = ["Lesta Game Center"] },
        new() { Id = "discord", Title = "Discord", Aliases = ["дискорд", "discord", "диск орд"],
                Processes = ["Discord"], Shortcuts = ["Discord"] },
        new() { Id = "telegram", Title = "Telegram", Aliases = ["телеграм", "телеграмм", "телега", "телегу", "тг", "тэ гэ", "телеграма", "telegram"],
                Processes = ["Telegram"], Shortcuts = ["Telegram"] },
        new() { Id = "vk", Title = "VK Мессенджер", Aliases = ["вк", "вэка", "вконтакте", "в контакте", "вк мессенджер", "мессенджер вк", "вэка мессенджер", "вкашку", "мессенджер"],
                Processes = ["VK Мессенджер", "VK Messenger"], Shortcuts = ["VK Мессенджер"], Launch = @"C:\Program Files\VK Messenger\VK Мессенджер.exe" },
        new() { Id = "comet", Title = "Comet", Aliases = ["комет", "комета", "кометт", "comet", "перплексити", "перплексити комет", "браузер комет"],
                Processes = ["comet"], Shortcuts = ["Comet"], Launch = @"C:\Program Files\Perplexity\Comet\Application\comet.exe" },
        new() { Id = "browser", Title = "Opera GX", Aliases = ["браузер", "опера", "оперу", "оперы", "opera", "опера гх", "опера джи икс", "опера гикс", "opera gx"],
                Processes = ["opera"], Shortcuts = ["Браузер Opera GX"] },
        new() { Id = "edge", Title = "Edge", Aliases = ["эдж", "edge", "майкрософт эдж"],
                Processes = ["msedge"], Shortcuts = ["Microsoft Edge"] },
        new() { Id = "tor", Title = "Tor Browser", Aliases = ["тор", "тор браузер"],
                Processes = ["firefox", "tor"], Shortcuts = ["Tor Browser"] },
        new() { Id = "steam", Title = "Steam", Aliases = ["стим", "steam"],
                Processes = ["steam"], Shortcuts = ["Steam"] },
        new() { Id = "music", Title = "Яндекс Музыка", Aliases = ["яндекс музыка", "музыка", "музыку", "яндекс музыку", "музычку", "музон", "яндекс музыки"],
                Processes = ["Яндекс Музыка", "Yandex Music"], Shortcuts = ["Яндекс Музыка"] },
        new() { Id = "vscode", Title = "VS Code", Aliases = ["вс код", "vs code", "visual studio code", "визуал студио код", "код"],
                Processes = ["Code"], Shortcuts = ["Visual Studio Code"], ConfirmClose = true },
        new() { Id = "telemost", Title = "Телемост", Aliases = ["телемост", "яндекс телемост"],
                Processes = ["Yandex Telemost", "telemost"], Shortcuts = ["Yandex Telemost"] },
        new() { Id = "afterburner", Title = "MSI Afterburner", Aliases = ["афтербернер", "afterburner", "мси"],
                Processes = ["MSIAfterburner"], Shortcuts = ["MSI Afterburner"] },
        new() { Id = "notepad", Title = "Блокнот", Aliases = ["блокнот"], Processes = ["notepad"], Launch = "notepad.exe" },
        new() { Id = "calculator", Title = "Калькулятор", Aliases = ["калькулятор"], Processes = ["CalculatorApp", "calc"], Launch = "calc.exe" },
        new() { Id = "taskmgr", Title = "Диспетчер задач", Aliases = ["диспетчер задач"], Processes = ["Taskmgr"], Launch = "taskmgr.exe" },
        new() { Id = "explorer", Title = "Проводник", Aliases = ["проводник"], Processes = ["explorer"], Launch = "explorer.exe", CanClose = false },
    ];

    /// <summary>Defaults merged with config/apps.json (same id replaces, new ids are added).</summary>
    public static IReadOnlyList<AppEntry> Load(string? path, ICollection<string>? warnings = null)
    {
        if (path is null || !File.Exists(path))
            return Defaults;

        try
        {
            List<AppEntry> custom = JsonSerializer.Deserialize<List<AppEntry>>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) ?? [];

            var merged = Defaults.ToDictionary(entry => entry.Id, StringComparer.OrdinalIgnoreCase);
            foreach (AppEntry entry in custom.Where(e => !string.IsNullOrWhiteSpace(e.Id)))
                merged[entry.Id] = entry with { Title = string.IsNullOrWhiteSpace(entry.Title) ? entry.Id : entry.Title };

            return merged.Values.ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            warnings?.Add("apps.json: " + ex.Message);
            return Defaults;
        }
    }

    /// <summary>Finds the entry for a spoken name: exact alias first, then the same word stems.</summary>
    public static AppEntry? Resolve(IReadOnlyList<AppEntry> catalog, string spoken)
    {
        string name = NormalizeName(spoken);
        if (name.Length == 0)
            return null;

        AppEntry? exact = catalog.FirstOrDefault(entry =>
            entry.Aliases.Append(entry.Title).Any(alias => NormalizeName(alias) == name));
        if (exact is not null)
            return exact;

        string stems = StemKey(name);
        AppEntry? stemmed = catalog.FirstOrDefault(entry =>
            entry.Aliases.Append(entry.Title).Any(alias => StemKey(alias) == stems));
        if (stemmed is not null)
            return stemmed;

        // Misheard single words: "дискомфорт" → "дискорд", "телеграмма" → "телеграм".
        if (name.Contains(' '))
            return null;

        return catalog
            .SelectMany(entry => entry.Aliases.Where(a => !a.Contains(' ')).Select(alias => (entry, alias: NormalizeName(alias))))
            .Where(x => x.alias.Length >= 5 && IsNear(name, x.alias))
            .Select(x => x.entry)
            .FirstOrDefault();
    }

    private static bool IsNear(string heard, string alias)
    {
        int prefix = 0;
        while (prefix < Math.Min(heard.Length, alias.Length) && heard[prefix] == alias[prefix])
            prefix++;

        return prefix >= 5 || (alias.Length >= 6 && Distance(heard, alias) <= 2);
    }

    private static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    public static string NormalizeName(string text) =>
        Regex.Replace(Regex.Replace(LocalMemoryStore.Normalize(text), @"[«»""'.,!?]", " "), @"\s+", " ").Trim();

    private static string StemKey(string text) =>
        string.Join(' ', RussianStemmer.Words(text).Select(RussianStemmer.Stem));
}
