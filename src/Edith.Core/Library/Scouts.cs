using System.Globalization;
using System.Text;
using Edith.Core.Activity;

namespace Edith.Core.Library;

/// <summary>
/// The librarian's eyes on the owner's machine. Everything here only READS: git log and status, folder listings, the first
/// lines of a README. Nothing is ever written, changed or run in the places it looks at.
/// </summary>
public static class RepoDigest
{
    private static readonly HashSet<string> SkipFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", ".venv", "venv", "__pycache__", "dist", "build", "target", ".vs", ".idea", "packages", "data", "models", "secrets", "app", "wake-training", "logs", "vendor", "third_party", "site-packages" };

    /// <summary>A markdown snapshot of the repository: branch, recent commits, unsaved changes, what the code is made of, how it is described.</summary>
    public static string Build(string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return "";

        var text = new StringBuilder();
        string branch = GitActivity.Run(repoPath, "branch --show-current")?.Trim() ?? "?";
        string status = GitActivity.Run(repoPath, "status --porcelain --untracked-files=no") ?? "";
        string log = GitActivity.Run(repoPath, "log -n 15 --pretty=format:%cd|%h|%s --date=short") ?? "";
        string count = GitActivity.Run(repoPath, "rev-list --count HEAD")?.Trim() ?? "?";

        text.AppendLine($"Папка: `{repoPath.Replace('\\', '/')}` · ветка `{branch}` · коммитов: {count}.");
        string[] dirty = status.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        text.AppendLine(dirty.Length == 0
            ? "Незакоммиченных изменений нет."
            : $"Не закоммичено: {dirty.Length} файл(ов), например " + string.Join(", ", dirty.Take(6).Select(l => "`" + l.Trim().Split(' ', 2)[^1].Trim() + "`")) + ".");

        text.AppendLine();
        text.AppendLine("**Последние коммиты:**");
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Split('|', 3);
            if (parts.Length == 3)
                text.AppendLine($"- {parts[0]} `{parts[1]}` {Clip(parts[2], 140)}");
        }

        (string layout, string kinds) = Layout(repoPath);
        if (layout.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("**Из чего состоит:** " + kinds);
            text.AppendLine("**Верхний уровень:** " + layout);
        }

        string readme = Head(repoPath, ["README.md", "readme.md", "docs/ROADMAP.md"], 14);
        if (readme.Length > 0)
        {
            text.AppendLine();
            text.AppendLine("**Как проект описан у себя (начало README):**");
            foreach (string line in readme.Split('\n'))
                text.AppendLine("> " + line.TrimEnd());
        }

        return text.ToString();
    }

    private static (string Layout, string Kinds) Layout(string root)
    {
        try
        {
            string top = string.Join(", ", Directory.GetDirectories(root)
                .Select(Path.GetFileName).Where(n => n is not null && !SkipFolders.Contains(n) && !n.StartsWith('.')).Take(14).Select(n => "`" + n + "/`"));

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int seen = 0;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && seen < 20000)
            {
                string dir = stack.Pop();
                foreach (string sub in Directory.GetDirectories(dir))
                    if (!SkipFolders.Contains(Path.GetFileName(sub)))
                        stack.Push(sub);
                foreach (string file in Directory.GetFiles(dir))
                {
                    seen++;
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext.Length > 0)
                        counts[ext] = counts.GetValueOrDefault(ext) + 1;
                }
            }

            string kinds = string.Join(", ", counts.OrderByDescending(p => p.Value).Take(8).Select(p => $"{p.Key} — {p.Value}"));
            return (top, $"{seen} файлов; {kinds}");
        }
        catch
        {
            return ("", "");
        }
    }

    private static string Head(string root, string[] candidates, int lines)
    {
        foreach (string name in candidates)
        {
            string path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
                continue;
            try
            {
                return string.Join('\n', File.ReadLines(path).Where(l => l.Trim().Length > 0).Take(lines).Select(l => Clip(l, 200)));
            }
            catch
            {
            }
        }

        return "";
    }

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}

public sealed record PcFind(string Path, long Bytes, DateTime Modified, string Title);

/// <summary>Looks through folders the owner named for documents worth knowing about; reads names and the first line of text files, never changes anything.</summary>
public static class PcScout
{
    private static readonly string[] Extensions = [".md", ".txt", ".pdf", ".docx", ".doc", ".pptx", ".xlsx", ".odt"];

    private static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase)
        { ".git", "node_modules", "bin", "obj", "AppData", ".venv", "__pycache__", "$RECYCLE.BIN", "System Volume Information", "Windows", "Program Files" };

    public static IReadOnlyList<PcFind> Scan(IEnumerable<string> folders, DateTime since, int max = 60)
    {
        var finds = new List<PcFind>();
        foreach (string root in folders.Where(Directory.Exists))
        {
            var stack = new Stack<string>();
            stack.Push(root);
            int visited = 0;
            while (stack.Count > 0 && finds.Count < max * 4 && visited++ < 6000)
            {
                string dir = stack.Pop();
                try
                {
                    foreach (string sub in Directory.GetDirectories(dir))
                        if (!Skip.Contains(System.IO.Path.GetFileName(sub)))
                            stack.Push(sub);

                    foreach (string file in Directory.GetFiles(dir))
                    {
                        if (!Extensions.Contains(System.IO.Path.GetExtension(file).ToLowerInvariant()))
                            continue;
                        var info = new FileInfo(file);
                        if (info.LastWriteTime >= since && info.Length > 200)
                            finds.Add(new PcFind(file, info.Length, info.LastWriteTime, TitleOf(file)));
                    }
                }
                catch
                {
                    // An unreadable folder is skipped, never a reason to stop.
                }
            }
        }

        return finds.OrderByDescending(f => f.Modified).Take(max).ToList();
    }

    private static string TitleOf(string file)
    {
        string ext = System.IO.Path.GetExtension(file).ToLowerInvariant();
        if (ext is not (".md" or ".txt"))
            return System.IO.Path.GetFileNameWithoutExtension(file);

        try
        {
            string first = File.ReadLines(file).FirstOrDefault(l => l.Trim().Length > 2)?.Trim().TrimStart('#', ' ') ?? "";
            return first.Length > 0 ? (first.Length > 100 ? first[..100] + "…" : first) : System.IO.Path.GetFileNameWithoutExtension(file);
        }
        catch
        {
            return System.IO.Path.GetFileNameWithoutExtension(file);
        }
    }

    public static string Render(IReadOnlyList<PcFind> finds)
    {
        var text = new StringBuilder();
        foreach (PcFind f in finds)
            text.AppendLine($"- {f.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} · `{f.Path.Replace('\\', '/')}` · {f.Bytes / 1024} КБ — {f.Title}");
        return text.ToString();
    }
}
