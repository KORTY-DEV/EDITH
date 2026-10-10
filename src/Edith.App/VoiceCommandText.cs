using System.Text.RegularExpressions;

namespace Edith.App;

/// <summary>
/// Pure text rules of the voice session: stop/repeat commands, wake prefix
/// cleanup and "стой, а ..." interrupt tails. No state, no I/O.
/// </summary>
public static class VoiceCommandText
{
    private static readonly string[] WakePrefixes = ["эдит ", "эди ", "едит ", "еди "];

    private static readonly Regex TrailingPunctuation = new(@"[,.!?;:…]+(?=\s|$)|[«»""]", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Lowercase, ё→е, and sentence punctuation removed. Vosk never emits punctuation but
    /// Whisper-class STT does ("Стой, а открой…"); "1.5" and "что-то" stay intact.
    /// </summary>
    public static string Normalize(string text) =>
        Spaces.Replace(TrailingPunctuation.Replace(text.ToLowerInvariant().Replace('ё', 'е'), " "), " ").Trim();

    public static bool IsGlobalStop(string? text) =>
        Regex.IsMatch(text ?? "", @"^\s*стоп[.!]?\s*$", RegexOptions.IgnoreCase);

    public static bool IsRepeatCommand(string text) =>
        Normalize(text) is
            "еще раз" or
            "повтори" or
            "повтори еще раз" or
            "скажи еще раз" or
            "скажи это еще раз" or
            "повторишь" or
            "повторить";

    /// <summary>"эдит открой блокнот" → "открой блокнот".</summary>
    public static string CleanDirectCommand(string text)
    {
        text = Normalize(text);

        foreach (string prefix in WakePrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return text[prefix.Length..].Trim();
        }

        return text;
    }

    /// <summary>"стой а какая погода" → "какая погода"; просто "стой" → "".</summary>
    public static string ExtractInterruptTail(string phrase)
    {
        phrase = Normalize(phrase);

        if (string.IsNullOrWhiteSpace(phrase))
            return "";

        string[] words = phrase.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        int start = words.Length > 1 && words[0] is "ну" or "эй" or "да" or "так" or "ладно"
            ? 1
            : 0;

        int triggerIndex = -1;
        for (int i = start; i < Math.Min(words.Length, start + 2); i++)
        {
            if (IsInterruptOrWake(words[i]))
            {
                triggerIndex = i;
                break;
            }
        }

        if (triggerIndex < 0)
            return "";

        int tailIndex = triggerIndex + 1;
        while (tailIndex < words.Length &&
               words[tailIndex] is "а" or "ну" or "ладно" or "тогда" or "нет")
        {
            tailIndex++;
        }

        return tailIndex >= words.Length
            ? ""
            : string.Join(' ', words[tailIndex..]).Trim();
    }

    public static bool IsInterruptOrWake(string word) =>
        word is
            "стоп" or "стой" or "стои" or "стойте" or
            "остановись" or "останови" or
            "прекрати" or "перестань" or
            "хватит" or "достаточно" or
            "тихо" or "тише" or
            "замолчи" or "умолкни" or "молчи" or "помолчи" or
            "закройся" or "заткнись" or
            "отбой" or "отмена" or
            "эдит" or "едит" or "эди" or "еди" or
            "эдик" or "едик" or
            "эдить" or "едить" or
            "идет" or "едет" or "эти" or "идит";
}
