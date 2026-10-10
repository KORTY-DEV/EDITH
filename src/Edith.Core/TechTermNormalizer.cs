using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>Small post-STT vocabulary for frequent computer terms.</summary>
public static class TechTermNormalizer
{
    private static readonly (string Pattern, string Replacement)[] Terms =
    [
        ("эс\\s*эс\\s*ди|эсэсди|ссд", "SSD"),
        ("эйч\\s*ди\\s*ди|аш\\s*ди\\s*ди|аж\\s*ди\\s*ди|эйчдиди|ашдиди|аждиди|хдд", "HDD"),
        ("эр\\s*ти\\s*икс|р\\s*т\\s*х|ртх", "RTX"),
        ("джи\\s*форс|джифорс", "GeForce"),
        ("энвидиа|нвидиа|енвидиа", "NVIDIA"),
        ("джи\\s*пи\\s*ю|гпу", "GPU"),
        ("си\\s*пи\\s*ю|цпу", "CPU"),
        ("эс\\s*эс\\s*ди", "SSD"),
        ("эйч\\s*ди\\s*ди", "HDD")
    ];

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        string result = text.Trim().ToLowerInvariant().Replace('ё', 'е');
        foreach ((string pattern, string replacement) in Terms)
        {
            result = Regex.Replace(
                result,
                $"(?<![\\p{{L}}])(?:{pattern})(?![\\p{{L}}])",
                replacement,
                RegexOptions.IgnoreCase);
        }

        return result;
    }
}
