using System.Diagnostics;
using System.Globalization;

namespace Edith.Core.Activity;

/// <summary>A project (git repository) as it stands now: read from git, never changed.</summary>
public sealed record RepoState(
    string Name,
    string Branch,
    DateTimeOffset? LastCommit,
    string LastSubject,
    int Commits7d,
    int DirtyFiles,
    IReadOnlyList<string> RecentSubjects);

/// <summary>
/// Reads the state of the owner's repositories with read-only git commands (log, status, branch;
/// no index lock, no fetch, no network). That is the freshest and most truthful source about what
/// the owner is actually working on.
/// </summary>
public static class GitActivity
{
    public static RepoState? Read(string path, TimeSpan? timeout = null)
    {
        if (!Directory.Exists(Path.Combine(path, ".git")) && !File.Exists(Path.Combine(path, ".git")))
            return null;

        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(15);
        string? branch = Git(path, "branch --show-current", limit);
        if (branch is null)
            return null;

        string log = Git(path, "log -n 40 --since=\"14 days ago\" --pretty=format:%ct|%s", limit) ?? "";
        string status = Git(path, "status --porcelain --untracked-files=no", limit) ?? "";
        string? last = Git(path, "log -n 1 --pretty=format:%ct|%s", limit);

        DateTimeOffset now = DateTimeOffset.Now;
        var recent = new List<string>();
        int week = 0;
        foreach (string line in log.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            (DateTimeOffset? at, string subject) = ParseLine(line);
            if (at is null)
                continue;
            if (now - at.Value <= TimeSpan.FromDays(7))
                week++;
            if (recent.Count < 5)
                recent.Add(subject);
        }

        (DateTimeOffset? lastAt, string lastSubject) = ParseLine(last ?? "");
        int dirty = status.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

        return new RepoState(Path.GetFileName(path.TrimEnd('\\', '/')), branch.Trim(), lastAt, lastSubject, week, dirty, recent);
    }

    public static (DateTimeOffset? At, string Subject) ParseLine(string line)
    {
        int bar = line.IndexOf('|');
        if (bar <= 0 || !long.TryParse(line[..bar].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds))
            return (null, "");

        return (DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime(), line[(bar + 1)..].Trim());
    }

    /// <summary>One read-only git command (log, status, branch...) in <paramref name="workingDirectory"/>; null on failure.</summary>
    public static string? Run(string workingDirectory, string arguments, TimeSpan? timeout = null) =>
        Git(workingDirectory, arguments, timeout ?? TimeSpan.FromSeconds(20));

    private static string? Git(string workingDirectory, string arguments, TimeSpan timeout)
    {
        try
        {
            var start = new ProcessStartInfo("git", "-c core.quotepath=false -c safe.directory=* " + arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            };
            start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; // status must not write the index of a repo someone is working in
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(true); } catch { }
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception ex)
        {
            if (Environment.GetEnvironmentVariable("EDITH_PULSE_DEBUG") == "1")
                Console.Error.WriteLine("git: " + ex.Message);
            return null;
        }
    }
}
