using System.IO;
using System.Text.Json;
using Edith.Core.Library;

namespace Edith.App;

/// <summary>config/library.json: where the session files are, which projects exist, where the archive lives.</summary>
public sealed record LibrarySettings(
    string Vault,
    double IdleHours,
    string Provider,
    string LocalModel,
    int GpuLayers,
    IReadOnlyList<SessionSource> Sources,
    IReadOnlyList<LibraryProject> Projects,
    IReadOnlyList<string> ScanFolders)
{
    /// <summary>"~" means the user's profile folder.</summary>
    public static string Expand(string path) =>
        path.StartsWith("~", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart((char)126, (char)47, (char)92))
            : path;

    public static LibrarySettings Load(string? path = null)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path ?? LibrarianService.ConfigPath),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        JsonElement root = document.RootElement;

        string Str(string name, string fallback) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        IEnumerable<JsonElement> Items(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

        return new LibrarySettings(
            Expand(Str("vaultPath", "")),
            root.TryGetProperty("idleHours", out JsonElement i) && i.TryGetDouble(out double iv) ? iv : 3,
            Str("provider", "local"),
            Str("localModel", "qwen3.5:4b"),
            root.TryGetProperty("localGpuLayers", out JsonElement gl) && gl.TryGetInt32(out int glv) ? glv : -1,
            Items("sources").Select(s => new SessionSource(
                s.GetProperty("id").GetString() ?? "",
                s.TryGetProperty("label", out JsonElement l) ? l.GetString() ?? "" : s.GetProperty("id").GetString() ?? "",
                s.GetProperty("path").GetString() ?? "",
                s.TryGetProperty("format", out JsonElement f) ? f.GetString() ?? "messages" : "messages",
                s.TryGetProperty("pattern", out JsonElement pt) ? pt.GetString() ?? "*.jsonl" : "*.jsonl",
                s.TryGetProperty("recursive", out JsonElement r) && r.ValueKind == JsonValueKind.True)).ToList(),
            Items("projects").Select(p => new LibraryProject(
                p.GetProperty("name").GetString() ?? "",
                p.GetProperty("section").GetString() ?? "",
                p.TryGetProperty("aliases", out JsonElement a) ? a.EnumerateArray().Select(x => x.GetString() ?? "").ToArray() : [],
                p.TryGetProperty("repo", out JsonElement repo) && repo.GetString() is { } rp ? Expand(rp) : null)).ToList(),
            Items("scanFolders").Select(x => Expand(x.GetString() ?? "")).Where(x => x.Length > 0).ToList());
    }
}
