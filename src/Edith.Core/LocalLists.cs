using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Owner-specific lists that stay out of the shared code: names of the AI agents the owner says aloud ("когда ... закончит")
/// and extra programs EDITH must never close. One entry per line in config/agent-names.txt and config/protected-processes.txt
/// (not part of the repository).
/// </summary>
public static class LocalLists
{
    public static IReadOnlyList<string> Read(string fileName)
    {
        try
        {
            string path = Path.Combine(@"C:\EDITH\config", fileName);
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>A regex alternation of the default words and the owner's names.</summary>
    public static string Alternation(IEnumerable<string> defaults, string fileName) =>
        string.Join("|", defaults.Concat(Read(fileName)).Select(Regex.Escape).Distinct());
}
