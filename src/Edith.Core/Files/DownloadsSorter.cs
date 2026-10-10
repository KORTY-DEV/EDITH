using System.Text.RegularExpressions;

namespace Edith.Core.Files;

/// <param name="Forms">"документ|документа|документов": 1, 2-4, 5+ (Russian plural).</param>
public sealed record SortCategory(string Name, string Forms, IReadOnlySet<string> Extensions, string TargetDescription)
{
    public string Count(int n)
    {
        string[] forms = Forms.Split('|');
        int mod10 = n % 10, mod100 = n % 100;
        string word = mod10 == 1 && mod100 != 11 ? forms[0]
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? forms[1]
            : forms[2];
        return $"{n} {word}";
    }
}

public sealed record SortPlan(
    string Source,
    IReadOnlyList<FileMove> Moves,
    IReadOnlyDictionary<string, int> CountByCategory,
    int Skipped)
{
    public bool IsEmpty => Moves.Count == 0;
}

/// <summary>
/// Plans "разбери загрузки": documents to Documents, pictures to Pictures, video, music;
/// archives and installers into subfolders of Downloads (never deleted here).
/// Files still downloading or changed in the last minutes are left alone.
/// </summary>
public sealed class DownloadsSorter
{
    public static readonly TimeSpan FreshFileAge = TimeSpan.FromMinutes(10);

    private static readonly HashSet<string> Partial = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".tmp", ".download", ".opdownload" };

    private readonly IReadOnlyList<(SortCategory Category, string Target)> _rules;
    private readonly Func<DateTimeOffset> _clock;

    public DownloadsSorter(string documents, string pictures, string videos, string music, string downloads, Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.Now);
        Downloads = downloads;
        _rules =
        [
            (new("документы", "документ|документа|документов", Set(".pdf", ".doc", ".docx", ".txt", ".rtf", ".odt", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".md", ".epub", ".djvu"), "в Документы"),
                Path.Combine(documents, "Из загрузок")),
            (new("картинки", "картинка|картинки|картинок", Set(".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp", ".heic", ".svg", ".tiff", ".ico"), "в Изображения"),
                Path.Combine(pictures, "Из загрузок")),
            (new("видео", "видео|видео|видео", Set(".mp4", ".mkv", ".avi", ".mov", ".webm", ".wmv"), "в Видео"),
                Path.Combine(videos, "Из загрузок")),
            (new("музыка", "аудиофайл|аудиофайла|аудиофайлов", Set(".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac"), "в Музыку"),
                Path.Combine(music, "Из загрузок")),
            (new("архивы", "архив|архива|архивов", Set(".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".xz"), "в папку Архивы в Загрузках"),
                Path.Combine(downloads, "Архивы")),
            (new("установщики", "установщик|установщика|установщиков", Set(".exe", ".msi", ".msix", ".appx"), "в папку Установщики в Загрузках"),
                Path.Combine(downloads, "Установщики")),
        ];
    }

    public static DownloadsSorter ForCurrentUser()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new DownloadsSorter(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(home, "Downloads"));
    }

    public string Downloads { get; }

    /// <summary>Installers in Downloads and its "Установщики" folder, except ones still fresh.</summary>
    public IReadOnlyList<string> Installers()
    {
        DateTimeOffset now = _clock();
        return new[] { Downloads, Path.Combine(Downloads, "Установщики") }
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateFiles)
            .Where(f => InstallerExtensions.Contains(Path.GetExtension(f)))
            .Where(f => now - new DateTimeOffset(File.GetLastWriteTime(f)) >= FreshFileAge)
            .ToList();
    }

    public IReadOnlySet<string> InstallerExtensions => _rules.Single(r => r.Category.Name == "установщики").Category.Extensions;

    /// <summary>Only top-level files of <paramref name="source"/>; folders the user made stay untouched.</summary>
    public SortPlan Plan(string source)
    {
        var moves = new List<FileMove>();
        var counts = new Dictionary<string, int>();
        int skipped = 0;
        DateTimeOffset now = _clock();

        if (!Directory.Exists(source))
            return new SortPlan(source, moves, counts, 0);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            string extension = Path.GetExtension(file);
            DateTimeOffset modified = new(File.GetLastWriteTime(file));

            if (Partial.Contains(extension) || now - modified < FreshFileAge || IsHidden(file))
            {
                skipped++;
                continue;
            }

            // FirstOrDefault on a tuple yields an empty tuple, not null: look the index up instead.
            int ruleIndex = -1;
            for (int i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Category.Extensions.Contains(extension))
                {
                    ruleIndex = i;
                    break;
                }
            }

            if (ruleIndex < 0)
            {
                skipped++;
                continue;
            }

            (SortCategory Category, string Target) match = _rules[ruleIndex];

            moves.Add(new FileMove(file, Path.Combine(match.Target, Path.GetFileName(file))));
            counts[match.Category.Name] = counts.GetValueOrDefault(match.Category.Name) + 1;
        }

        return new SortPlan(source, moves, counts, skipped);
    }

    /// <summary>"42 файла: 12 документов — в Документы, 20 картинок — в Изображения, …"</summary>
    public string Describe(SortPlan plan)
    {
        if (plan.IsEmpty)
            return "В загрузках нечего разбирать" + (plan.Skipped > 0 ? $" (не трогаю {plan.Skipped}: свежие, недокачанные или непонятного типа)." : ".");

        IEnumerable<string> parts = _rules
            .Where(r => plan.CountByCategory.ContainsKey(r.Category.Name))
            .Select(r => $"{r.Category.Count(plan.CountByCategory[r.Category.Name])} — {r.Category.TargetDescription}");

        string skipped = plan.Skipped > 0 ? $" Ещё {plan.Skipped} не трогаю." : "";
        return $"В загрузках {plan.Moves.Count} {FileOps.Files(plan.Moves.Count)}: {string.Join(", ", parts)}.{skipped}";
    }

    private static bool IsHidden(string file)
    {
        try
        {
            return (File.GetAttributes(file) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static HashSet<string> Set(params string[] extensions) => new(extensions, StringComparer.OrdinalIgnoreCase);
}

public enum FileCommandKind
{
    SortDownloads,
    RecycleInstallers,
    Undo
}

public static class FileCommandParser
{
    private static readonly Regex Sort = new(
        @"^(?:разбери|разложи|рассортируй|отсортируй|наведи порядок в|прибери(?:сь)? в)\s+(?:мои\s+)?(?:загруз\w*|папк\w* загруз\w*|скачанн\w*)",
        RegexOptions.Compiled);

    private static readonly Regex Installers = new(
        @"^(?:удали|убери|выкинь|почисти)\s+(?:все\s+|старые\s+)?(?:установщик\w*|инсталлятор\w*|exe\w*)(?:\s+(?:из|в)\s+загруз\w*)?$",
        RegexOptions.Compiled);

    private static readonly Regex Undo = new(
        @"^(?:верни (?:все |всё )?(?:как было|обратно|на место)|отмени (?:последнее )?(?:действие с файлами|перемещение|разбор(?: загрузок)?|удаление))$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out FileCommandKind kind)
    {
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[,.!?]", " ");
        input = Regex.Replace(Regex.Replace(input, @"\s+", " ").Trim(), @"^(?:эдит|пожалуйста)\s+", "");

        kind = Sort.IsMatch(input) ? FileCommandKind.SortDownloads
            : Installers.IsMatch(input) ? FileCommandKind.RecycleInstallers
            : Undo.IsMatch(input) ? FileCommandKind.Undo
            : (FileCommandKind)(-1);

        return (int)kind >= 0;
    }
}
