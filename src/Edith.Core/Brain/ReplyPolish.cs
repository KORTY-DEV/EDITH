using System.Text.RegularExpressions;

namespace Edith.Core.Brain;

/// <summary>
/// Last touch on a model reply before it is shown and spoken: drops the service-desk tail
/// ("Нужно что-то ещё?", "Если хочешь, могу…") the persona rules alone do not stop,
/// and fixes verb forms small models get wrong ("следю").
/// </summary>
public static class ReplyPolish
{
    private static readonly Regex OfferTail = new(
        @"^(?:нужно|надо|нужна|могу|хочешь|хотите|если|чем|что[- ](?:то|нибудь|либо)|обращайся|обращайтесь|дай(?:те)? знать|скажи(?:те)?,? если|я рядом|готова помочь)\b" +
        @"[^.!?]*?(?:ещё|еще|помочь|помощь|подробнее|продолжить|рассказать|добавить|детал\w*|понадобится|нужно|рядом|знать|уточнить)\b[^.!?]*[.!?…]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Fixed)[] WordFixes =
    [
        (new(@"\bследю\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "слежу"),
        (new(@"\bпобедю\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "одержу победу"),
        (new(@"\bубедю\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "смогу убедить"),
        (new(@"\bложу\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "кладу"),
        (new(@"\bложи\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "положи"),
        (new(@"\bположу\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "положу"),
        (new(@"\bхочут\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "хотят"),
        (new(@"\bвыздоровлю\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "выздоровею"),
        (new(@"\bчудю\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), "чужу"),
    ];

    private static readonly Regex Sentences = new(@"(?<=[.!?…])\s+", RegexOptions.Compiled);

    /// <summary>"Нужно что-то ещё?", "Если хотите, могу подробнее": a sentence that only offers more.</summary>
    public static bool IsOffer(string sentence) => OfferTail.IsMatch(sentence.Trim());

    /// <summary>The word fixes alone ("следю" → "слежу"), for text that is spoken sentence by sentence.</summary>
    public static string FixWords(string text)
    {
        foreach ((Regex pattern, string fix) in WordFixes)
            text = pattern.Replace(text, m => char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(fix[0]) + fix[1..] : fix);
        return text;
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        string s = text.Trim();
        foreach ((Regex pattern, string fix) in WordFixes)
            s = pattern.Replace(s, m => char.IsUpper(m.Value[0]) ? char.ToUpperInvariant(fix[0]) + fix[1..] : fix);

        // Drop offer sentences from the end, but never the whole answer.
        List<string> parts = Sentences.Split(s).Where(p => p.Length > 0).ToList();
        while (parts.Count > 1 && OfferTail.IsMatch(parts[^1].Trim()))
            parts.RemoveAt(parts.Count - 1);

        s = string.Join(' ', parts);
        return s.Length > 0 && char.IsLower(s[0]) ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }
}
