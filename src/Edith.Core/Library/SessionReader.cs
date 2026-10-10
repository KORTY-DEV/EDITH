using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Edith.Core.Library;

public sealed record SessionTurn(string Role, string Text);

/// <summary>One finished AI work session (from an AI agent or assistant), reduced to its human-readable turns.</summary>
public sealed record SessionTranscript(
    string Id,
    string Source,
    string Label,
    string Project,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Path,
    IReadOnlyList<SessionTurn> Turns);

/// <summary>
/// Reads the local session files of AI agents (configured in config/library.json, two JSONL layouts): only what
/// people and the assistant said in words. Tool calls, tool results, system reminders and code blocks are
/// dropped: the librarian needs the story, not the source, and a code dump has no place in the vault.
/// </summary>
public static class SessionReader
{
    public const int MaxTurnChars = 1500;

    private static readonly Regex CodeFence = new(@"```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Tags = new(
        @"<(system-reminder|local-command-caveat|local-command-stdout|command-name|command-message|command-args|pasted_content|task-notification|ide_[a-z_]+|environment_context|user_instructions)\b.*?</\1[^>]*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Secrets never go anywhere, not even into a summary request.
    private static readonly Regex Secret = new(
        @"(sk-[A-Za-z0-9_\-]{16,}|AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{20,}|eyJ[A-Za-z0-9_\-]{20,}\.[A-Za-z0-9_\-]{10,}|" +
        @"(?i:(?:password|passwd|пароль|token|токен|api[_-]?key|secret)\s*[:=]\s*\S+)|[A-Za-z0-9+/]{40,}={0,2})",
        RegexOptions.Compiled);

    public static string Clean(string text)
    {
        text = Tags.Replace(text, " ");
        text = CodeFence.Replace(text, " [код] ");
        text = Secret.Replace(text, "[скрыто]");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length <= MaxTurnChars ? text : text[..MaxTurnChars] + "…";
    }

    /// <summary>Layout "messages": one JSON object per line with type user/assistant and a message with content.</summary>
    public static SessionTranscript? ReadMessages(string path, string source, string label)
    {
        var turns = new List<SessionTurn>();
        string? cwd = null, id = null;
        DateTimeOffset? first = null, last = null;

        foreach (string line in ReadLines(path))
        {
            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            string? type = Str(root, "type");
            cwd ??= Str(root, "cwd");
            id ??= Str(root, "sessionId");
            if (Str(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at))
            {
                first ??= at;
                last = at;
            }

            if (type is not ("user" or "assistant") || !root.TryGetProperty("message", out JsonElement message))
                continue;
            if (root.TryGetProperty("isSidechain", out JsonElement side) && side.ValueKind == JsonValueKind.True)
                continue;

            string text = TextOf(message);
            if (text.Length < 3)
                continue;

            turns.Add(new SessionTurn(type == "user" ? "user" : "assistant", Clean(text)));
        }

        turns = turns.Where(t => t.Text.Length >= 3).ToList();
        if (turns.Count == 0 || first is null || last is null)
            return null;

        return new SessionTranscript(
            id ?? System.IO.Path.GetFileNameWithoutExtension(path), source, label, ProjectName(cwd, path),
            first.Value, last.Value, path, turns);
    }

    /// <summary>Layout "events": lines with a payload (session_meta, message items with input_text/output_text).</summary>
    public static SessionTranscript? ReadEvents(string path, string source, string label)
    {
        var turns = new List<SessionTurn>();
        string? cwd = null, id = null;
        DateTimeOffset? first = null, last = null;

        foreach (string line in ReadLines(path))
        {
            JsonElement root;
            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            if (Str(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset at))
            {
                first ??= at;
                last = at;
            }

            if (!root.TryGetProperty("payload", out JsonElement payload))
                continue;

            if (Str(root, "type") == "session_meta")
            {
                id ??= Str(payload, "id");
                cwd ??= Str(payload, "cwd");
                continue;
            }

            if (Str(payload, "type") != "message" || Str(payload, "role") is not ("user" or "assistant") and not null)
                continue;

            string role = Str(payload, "role") ?? "";
            if (role is not ("user" or "assistant"))
                continue;

            string text = TextOf(payload);
            if (text.Length < 3)
                continue;

            turns.Add(new SessionTurn(role, Clean(text)));
        }

        turns = turns.Where(t => t.Text.Length >= 3).ToList();
        if (turns.Count == 0 || first is null || last is null)
            return null;

        return new SessionTranscript(
            id ?? System.IO.Path.GetFileNameWithoutExtension(path), source, label, ProjectName(cwd, path),
            first.Value, last.Value, path, turns);
    }

    /// <summary>A compact transcript for the model: the start of the session and its end, the middle thinned out.</summary>
    public static string Condense(SessionTranscript transcript, int budgetChars = 7000)
    {
        var all = transcript.Turns.Select(t => (t.Role == "user" ? "Хозяин: " : "AI: ") + t.Text).ToList();
        var text = new StringBuilder();
        int total = all.Sum(l => l.Length + 1);
        if (total <= budgetChars)
            return string.Join('\n', all);

        // Keep the first quarter and the last half of the budget; sample the middle.
        int head = budgetChars / 4, tail = budgetChars / 2, middle = budgetChars - head - tail;
        int used = 0, i = 0;
        for (; i < all.Count && used + all[i].Length < head; i++)
        {
            text.AppendLine(all[i]);
            used += all[i].Length + 1;
        }

        int tailStart = all.Count;
        int tailUsed = 0;
        while (tailStart > i && tailUsed + all[tailStart - 1].Length < tail)
        {
            tailStart--;
            tailUsed += all[tailStart].Length + 1;
        }

        text.AppendLine("[…середина сессии пропущена…]");
        int step = Math.Max(1, (tailStart - i) / Math.Max(1, middle / 220));
        int midUsed = 0;
        for (int k = i; k < tailStart && midUsed < middle; k += step)
        {
            string snippet = all[k].Length > 220 ? all[k][..220] + "…" : all[k];
            text.AppendLine(snippet);
            midUsed += snippet.Length + 1;
        }

        text.AppendLine("[…]");
        for (int k = tailStart; k < all.Count; k++)
            text.AppendLine(all[k]);
        return text.ToString();
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0)
                yield return line;
    }

    private static string? Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The text parts of a message: a plain string, or the "text" / "input_text" / "output_text" blocks of a list.</summary>
    private static string TextOf(JsonElement message)
    {
        if (!message.TryGetProperty("content", out JsonElement content))
            return "";

        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";

        if (content.ValueKind != JsonValueKind.Array)
            return "";

        var text = new StringBuilder();
        foreach (JsonElement part in content.EnumerateArray())
        {
            string? type = Str(part, "type");
            if (type is "text" or "input_text" or "output_text" && Str(part, "text") is { } t)
                text.Append(Str(part, "text")).Append(' ');
        }

        return text.ToString();
    }

    private static string ProjectName(string? cwd, string path)
    {
        if (!string.IsNullOrWhiteSpace(cwd))
        {
            string name = System.IO.Path.GetFileName(cwd.TrimEnd('\\', '/'));
            if (name.Length > 0)
                return name;
        }

        string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path) ?? "") ?? "";
        return folder.Length == 0 || folder.StartsWith("C--", StringComparison.Ordinal) && folder.TrimStart('C', '-').Length == 0 ? "Без проекта" : folder;
    }
}
