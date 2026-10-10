using System.Net;
using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// HTML → readable text for the brain: scripts, styles and markup dropped, whitespace folded.
/// Lines with prices come first, so a long shop page still shows them within the budget.
/// </summary>
public static class WebPageText
{
    private static readonly Regex Price = new(@"\d[\d\s  ]*(?:[.,]\d+)?\s*(?:₽|руб|р\.|\$|€)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Extract(string html, int maxChars = 6000)
    {
        string text = Regex.Replace(html, @"<(script|style|noscript|svg|head)\b[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<br\s*/?>|</(p|div|li|tr|h\d|td|th|section|article)>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = WebUtility.HtmlDecode(text);

        List<string> lines = text.Split('\n')
            .Select(l => Regex.Replace(l, @"\s+", " ").Trim())
            .Where(l => l.Length > 2)
            .Distinct()
            .ToList();

        IEnumerable<string> ordered = lines.Where(l => Price.IsMatch(l) && l.Length < 300)
            .Take(40)
            .Select(l => "[цена] " + l)
            .Concat(lines);

        var result = new System.Text.StringBuilder();
        foreach (string line in ordered)
        {
            if (result.Length + line.Length + 1 > maxChars)
                break;
            result.AppendLine(line);
        }

        return result.ToString().Trim();
    }
}
