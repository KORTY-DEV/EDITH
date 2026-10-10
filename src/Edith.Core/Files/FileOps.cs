using System.Text.Json;
using Edith.Core.Runtime;

namespace Edith.Core.Files;

/// <summary>Sends a file to the Windows recycle bin; the app implements it, tests fake it.</summary>
public interface IRecycleBin
{
    void Recycle(string path);
}

/// <summary>
/// Where EDITH may change files: the user's own folders only (allow-list). Everything else —
/// Windows, Program Files, AppData, EDITH itself, other projects — is read-only for her.
/// </summary>
public sealed class FileSafety
{
    private readonly IReadOnlyList<string> _writableRoots;

    public FileSafety(IEnumerable<string> writableRoots)
    {
        _writableRoots = writableRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => Path.GetFullPath(r).TrimEnd(Path.DirectorySeparatorChar))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> WritableRoots => _writableRoots;

    public static FileSafety ForCurrentUser()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new FileSafety(
        [
            Path.Combine(home, "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ]);
    }

    /// <summary>True when <paramref name="path"/> is strictly inside a writable root (never the root itself).</summary>
    public bool CanWrite(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch
        {
            return false;
        }

        return _writableRoots.Any(root =>
            full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public string Explain(string path) =>
        $"Не трогаю «{path}»: менять файлы можно только в Загрузках, Документах, на рабочем столе, в Изображениях, Видео и Музыке.";
}

public sealed record FileMove(string From, string To);

/// <summary>One user-visible action on files ("разобрала загрузки"), undoable as a whole.</summary>
public sealed record FileBatch(
    Guid Id,
    string Title,
    DateTimeOffset At,
    IReadOnlyList<FileMove> Moves,
    IReadOnlyList<string> Recycled);

public sealed record FileResult(bool Ok, string Message, FileBatch? Batch = null);

/// <summary>
/// All file changes EDITH makes go through here: allow-listed folders, no overwrites
/// ("имя (2).ext" instead), deletion only to the recycle bin, every change recorded so
/// "верни как было" can undo the last batch.
/// </summary>
public sealed class FileOps
{
    private const int KeptBatches = 20;

    private readonly FileSafety _safety;
    private readonly IRecycleBin _recycleBin;
    private readonly string? _logPath;
    private readonly IEventBus? _bus;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly List<FileBatch> _batches;

    public FileOps(
        FileSafety safety,
        IRecycleBin recycleBin,
        string? logPath = null,
        IEventBus? bus = null,
        Func<DateTimeOffset>? clock = null)
    {
        _safety = safety;
        _recycleBin = recycleBin;
        _logPath = logPath;
        _bus = bus;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _batches = Load(logPath);
    }

    public FileSafety Safety => _safety;

    public FileBatch? LastBatch
    {
        get
        {
            lock (_gate)
                return _batches.LastOrDefault();
        }
    }

    /// <summary>Moves (or renames) files as one batch. Stops at nothing: failures are reported per file.</summary>
    public FileResult Move(string title, IReadOnlyList<FileMove> planned)
    {
        var done = new List<FileMove>();
        var failed = new List<string>();

        foreach (FileMove move in planned)
        {
            if (!_safety.CanWrite(move.From) || !_safety.CanWrite(move.To))
            {
                failed.Add($"{Path.GetFileName(move.From)} (запрещённая папка)");
                continue;
            }

            try
            {
                if (!File.Exists(move.From))
                {
                    failed.Add($"{Path.GetFileName(move.From)} (не найден)");
                    continue;
                }

                string target = FreeName(move.To);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Move(move.From, target);
                done.Add(new FileMove(move.From, target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(move.From)} ({ex.Message})");
            }
        }

        FileBatch? batch = done.Count == 0 ? null : Record(title, done, []);
        return new FileResult(failed.Count == 0, Summary(done.Count, "перемещено", failed), batch);
    }

    /// <summary>Sends files to the recycle bin (never a permanent delete).</summary>
    public FileResult Recycle(string title, IReadOnlyList<string> paths)
    {
        var done = new List<string>();
        var failed = new List<string>();

        foreach (string path in paths)
        {
            if (!_safety.CanWrite(path))
            {
                failed.Add($"{Path.GetFileName(path)} (запрещённая папка)");
                continue;
            }

            try
            {
                if (!File.Exists(path))
                {
                    failed.Add($"{Path.GetFileName(path)} (не найден)");
                    continue;
                }

                _recycleBin.Recycle(path);
                done.Add(path);
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)} ({ex.Message})");
            }
        }

        FileBatch? batch = done.Count == 0 ? null : Record(title, [], done);
        return new FileResult(failed.Count == 0, Summary(done.Count, "в корзине", failed), batch);
    }

    /// <summary>Reverses the last batch's moves. Recycled files are left in the recycle bin (restore there).</summary>
    public FileResult UndoLast()
    {
        FileBatch? batch;
        lock (_gate)
        {
            batch = _batches.LastOrDefault();
            if (batch is not null)
            {
                _batches.RemoveAt(_batches.Count - 1);
                Save();
            }
        }

        if (batch is null)
            return new FileResult(false, "Нечего возвращать: я ещё ничего не меняла в файлах.");

        int restored = 0;
        var failed = new List<string>();
        foreach (FileMove move in batch.Moves.Reverse())
        {
            try
            {
                if (!File.Exists(move.To))
                {
                    failed.Add($"{Path.GetFileName(move.To)} (уже нет на месте)");
                    continue;
                }

                string back = FreeName(move.From);
                Directory.CreateDirectory(Path.GetDirectoryName(back)!);
                File.Move(move.To, back);
                restored++;
                TryRemoveEmptyDirectory(Path.GetDirectoryName(move.To));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add($"{Path.GetFileName(move.To)} ({ex.Message})");
            }
        }

        _bus?.Publish(EdithEvent.Create("files.undone", batch.Title,
            data: new Dictionary<string, string> { ["tool"] = "files", ["restored"] = restored.ToString() }));

        string recycledNote = batch.Recycled.Count > 0
            ? $" Удалённые файлы ({batch.Recycled.Count}) лежат в корзине — их можно восстановить оттуда."
            : "";
        string failedNote = failed.Count > 0 ? $" Не получилось: {string.Join(", ", failed.Take(3))}." : "";
        return new FileResult(failed.Count == 0, $"Вернула как было: {restored} файлов.{recycledNote}{failedNote}");
    }

    /// <summary>"отчёт.pdf" → "отчёт (2).pdf" when taken. Never overwrites.</summary>
    public static string FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            return path;

        string directory = Path.GetDirectoryName(path)!;
        string name = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(directory, $"{name} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                return candidate;
        }
    }

    private FileBatch Record(string title, IReadOnlyList<FileMove> moves, IReadOnlyList<string> recycled)
    {
        var batch = new FileBatch(Guid.NewGuid(), title, _clock(), moves, recycled);
        lock (_gate)
        {
            _batches.Add(batch);
            while (_batches.Count > KeptBatches)
                _batches.RemoveAt(0);
            Save();
        }

        _bus?.Publish(EdithEvent.Create(EventKinds.ToolFinished, "files", data: new Dictionary<string, string>
        {
            ["tool"] = "files",
            ["app"] = title,
            ["arguments"] = $"moves={moves.Count}; recycled={recycled.Count}",
            ["result"] = batch.Id.ToString(),
        }));
        return batch;
    }

    private static string Summary(int done, string verb, List<string> failed)
    {
        string text = done > 0 ? $"Готово: {done} {Files(done)} {verb}." : "Ничего не изменила.";
        if (failed.Count > 0)
            text += $" Не получилось с {failed.Count}: {string.Join(", ", failed.Take(3))}" + (failed.Count > 3 ? " и др." : "") + ".";
        return text;
    }

    public static string Files(int n) =>
        (n % 10 == 1 && n % 100 != 11) ? "файл" :
        (n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14) ? "файла" : "файлов";

    private static void TryRemoveEmptyDirectory(string? directory)
    {
        try
        {
            if (directory is not null && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                Directory.Delete(directory);
        }
        catch
        {
        }
    }

    private void Save()
    {
        if (_logPath is null)
            return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_logPath))!);
            string temp = _logPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_batches, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _logPath, true);
        }
        catch
        {
        }
    }

    private static List<FileBatch> Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<List<FileBatch>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
