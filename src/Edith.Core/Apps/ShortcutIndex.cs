namespace Edith.Core.Apps;

public sealed record Shortcut(string Name, string Path);

/// <summary>
/// Start-menu and desktop shortcuts (.lnk/.url), found by name. Uninstallers are skipped so
/// "открой Мир танков" can never pick "Удалить Мир танков".
/// </summary>
public sealed class ShortcutIndex
{
    private static readonly string[] UninstallMarkers = ["удалить", "деинсталл", "uninstall", "remove"];

    private readonly IReadOnlyList<Shortcut> _shortcuts;

    public ShortcutIndex(IEnumerable<Shortcut> shortcuts)
    {
        _shortcuts = shortcuts
            .Where(s => !UninstallMarkers.Any(m => s.Name.Contains(m, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public IReadOnlyList<Shortcut> All => _shortcuts;

    public static ShortcutIndex Scan(IEnumerable<string> directories)
    {
        var found = new List<Shortcut>();
        foreach (string directory in directories.Where(Directory.Exists))
        {
            try
            {
                // One locked subfolder (e.g. "Administrative Tools") must not hide the rest of the menu.
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
                foreach (string file in Directory.EnumerateFiles(directory, "*.*", options))
                {
                    string extension = Path.GetExtension(file);
                    if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                        extension.Equals(".url", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(new Shortcut(Path.GetFileNameWithoutExtension(file), file));
                    }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }
        }

        return new ShortcutIndex(found);
    }

    public static IEnumerable<string> DefaultDirectories()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    /// <summary>Exact name first (any of <paramref name="names"/>), then a name that contains it.</summary>
    public Shortcut? Find(IEnumerable<string> names)
    {
        List<string> wanted = names.Select(AppCatalog.NormalizeName).Where(n => n.Length > 0).ToList();

        foreach (string name in wanted)
        {
            Shortcut? exact = _shortcuts.FirstOrDefault(s => AppCatalog.NormalizeName(s.Name) == name);
            if (exact is not null)
                return exact;
        }

        foreach (string name in wanted.Where(n => n.Length >= 4))
        {
            Shortcut? partial = _shortcuts
                .Where(s => AppCatalog.NormalizeName(s.Name).Contains(name, StringComparison.Ordinal))
                .OrderBy(s => s.Name.Length)
                .FirstOrDefault();
            if (partial is not null)
                return partial;
        }

        // Latin app names said in Russian: "лэм студио" → "LM Studio", "стим" → "Steam".
        foreach (string name in wanted.Where(n => n.Any(c => c is >= 'а' and <= 'я')))
        {
            string heard = Skeleton(Translit(name));
            Shortcut? close = _shortcuts
                .Select(s => (Shortcut: s, Distance: Math.Min(
                    Distance(heard, Skeleton(AppCatalog.NormalizeName(s.Name))),
                    Distance(heard, Skeleton(AppCatalog.NormalizeName(s.Name).Split(' ')[0])))))
                .Where(x => heard.Length >= 3 && x.Distance <= Math.Max(1, heard.Length / 4))
                .OrderBy(x => x.Distance)
                .ThenBy(x => x.Shortcut.Name.Length)
                .Select(x => x.Shortcut)
                .FirstOrDefault();
            if (close is not null)
                return close;
        }

        return null;
    }

    private static readonly Dictionary<char, string> Latin = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ж'] = "zh", ['з'] = "z",
        ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o", ['п'] = "p",
        ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "c", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "sh", ['ы'] = "y", ['э'] = "e", ['ю'] = "u", ['я'] = "a", ['ь'] = "", ['ъ'] = "",
    };

    public static string Translit(string text) =>
        string.Concat(text.Select(c => Latin.TryGetValue(c, out string? l) ? l : c.ToString()));

    /// <summary>Consonants only, no spaces: "lem studio" and "lm studio" both become "lmstd".</summary>
    private static string Skeleton(string text) =>
        new(text.Where(c => char.IsLetterOrDigit(c) && "aeiouy".IndexOf(c) < 0).ToArray());

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
}
