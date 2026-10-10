using System.Globalization;
using System.Text.RegularExpressions;

namespace Edith.Core.Files;

public sealed record FileQueryRequest(string Folder, string FolderName, IReadOnlySet<string> Extensions, string KindName);

/// <summary>
/// "найди в загрузках pdf-файлы и скажи, какой самый свежий", "какие картинки на рабочем столе":
/// answered from the file system directly — the model never saw these files and used to guess.
/// </summary>
public static class FileQuery
{
    private static readonly Regex Ask = new(@"найди|покажи|какие|какой|сколько|есть ли|перечисли|что (?:лежит|есть)", RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Name, string[] Extensions)[] Kinds =
    [
        (new(@"\bpdf|пдф", RegexOptions.Compiled), "PDF", [".pdf"]),
        (new(@"картин|фото|изображ|скриншот", RegexOptions.Compiled), "картинок", [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".heic"]),
        (new(@"видео|ролик|фильм", RegexOptions.Compiled), "видео", [".mp4", ".mkv", ".avi", ".mov", ".webm"]),
        (new(@"музык|песн|аудио|mp3", RegexOptions.Compiled), "аудио", [".mp3", ".flac", ".wav", ".ogg", ".m4a"]),
        (new(@"установщик|инсталлятор|\bexe", RegexOptions.Compiled), "установщиков", [".exe", ".msi"]),
        (new(@"архив|\bzip|\brar", RegexOptions.Compiled), "архивов", [".zip", ".rar", ".7z"]),
        (new(@"документ|ворд|word|таблиц|excel", RegexOptions.Compiled), "документов", [".pdf", ".doc", ".docx", ".txt", ".xls", ".xlsx", ".odt", ".rtf"]),
        (new(@"\bфайл", RegexOptions.Compiled), "файлов", []),
    ];

    public static bool TryParse(string text, out FileQueryRequest request)
    {
        request = null!;
        string input = text.ToLowerInvariant().Replace('ё', 'е');
        if (!Ask.IsMatch(input))
            return false;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (string Folder, string Name)? place =
            Regex.IsMatch(input, @"загрузк|загрузок|скачан") ? (Path.Combine(home, "Downloads"), "в загрузках")
            : Regex.IsMatch(input, @"рабоч\w* стол") ? (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "на рабочем столе")
            : Regex.IsMatch(input, @"в документах|в папке документ|мои документы") ? (Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "в документах")
            : null;
        if (place is null)
            return false;

        foreach ((Regex pattern, string name, string[] extensions) in Kinds)
        {
            if (pattern.IsMatch(input))
            {
                request = new FileQueryRequest(place.Value.Folder, place.Value.Name, new HashSet<string>(extensions, StringComparer.OrdinalIgnoreCase), name);
                return true;
            }
        }

        return false;
    }

    public static string Answer(FileQueryRequest request, DateTime now)
    {
        if (!Directory.Exists(request.Folder))
            return $"Не нашла папку {request.FolderName.Replace("в ", "").Replace("на ", "")}.";

        List<FileInfo> files = new DirectoryInfo(request.Folder)
            .EnumerateFiles("*", new EnumerationOptions { IgnoreInaccessible = true })
            .Where(f => request.Extensions.Count == 0 || request.Extensions.Contains(f.Extension))
            .Where(f => (f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();

        if (files.Count == 0)
            return $"{Capitalize(request.FolderName)} нет {request.KindName}.";

        string newest = string.Join(", ", files.Take(3).Select(f => $"«{Path.GetFileNameWithoutExtension(f.Name)}» ({When(f.LastWriteTime, now)})"));
        return $"{Capitalize(request.FolderName)} {files.Count} {FileOps.Files(files.Count)}. Самые свежие: {newest}.";
    }

    private static string When(DateTime time, DateTime now)
    {
        int days = (now.Date - time.Date).Days;
        return days switch
        {
            0 => "сегодня",
            1 => "вчера",
            < 7 => $"{days} {RussianPluralDays(days)} назад",
            _ => time.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU")),
        };
    }

    private static string RussianPluralDays(int n) => n is >= 2 and <= 4 ? "дня" : "дней";

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
