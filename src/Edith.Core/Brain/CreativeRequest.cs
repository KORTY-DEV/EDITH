using System.Text.RegularExpressions;

namespace Edith.Core.Brain;

/// <summary>
/// "Расскажи стих Пушкина", "сочини сказку", "расскажи анекдот": answered from the model's
/// own knowledge (never a web search) and allowed to be longer than the usual 1-3 sentences.
/// </summary>
public static class CreativeRequest
{
    private static readonly Regex Pattern = new(
        @"\b(?:стих\w*|стихотворени\w*|поэм\w*|сказк\w*|басн\w*|анекдот\w*|шутк\w*|рассказ(?:ик)?\b|истори[юи]\b|песн\w*|рифм\w*|сочини|придумай)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsCreative(string? text) =>
        !string.IsNullOrWhiteSpace(text) && Pattern.IsMatch(text.ToLowerInvariant().Replace('ё', 'е'));

    /// <summary>Token budget for a creative answer: room for ~12 lines on GPU, shorter on CPU.</summary>
    public static int NumPredict(bool cpuOnly) => cpuOnly ? 200 : 384;
}
