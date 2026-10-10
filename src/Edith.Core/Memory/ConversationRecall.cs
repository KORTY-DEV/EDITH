using System.Text.RegularExpressions;

namespace Edith.Core.Memory;

/// <summary>
/// "О чём мы говорили вчера?" — answered from the local conversation log, deterministically.
/// No model is needed: EDITH lists what the user asked about that day.
/// </summary>
public static class ConversationRecall
{
    private static readonly Regex Question = new(
        @"(?:о ч[её]м|что)\s+(?:мы\s+)?(?:с тобой\s+)?(?:говорили|обсуждали|болтали|разговаривали)" +
        @"|(?:о ч[её]м|что)\s+я\s+(?:тебя\s+|у тебя\s+)?(?:спрашивал|спрашивала|просил|просила)",
        RegexOptions.Compiled);

    // Routes that are EDITH talking about herself, not topics of the conversation.
    private static readonly HashSet<string> MetaRoutes =
        ["ConversationRecall", "ActivityReport", "MemoryRecall", "MemoryForget", "IdentityFastPath", "ReminderAck"];

    // Routes that are errands, not conversation: grouped by kind instead of quoted.
    private static readonly Dictionary<string, string> CommandRoutes = new()
    {
        ["AppControl"] = "приложения",
        ["Reminder"] = "напоминания",
        ["WebResearch"] = "поиск в интернете",
        ["Browser"] = "браузер",
        ["Documents"] = "документы",
        ["PowerSchedule"] = "выключение",
        ["MemorySave"] = "запомнить о тебе",
        ["Files"] = "файлы",
        ["Screen"] = "экран",
    };

    /// <summary>Real conversation topics of the day (not commands, not questions about EDITH herself).</summary>
    public static IReadOnlyList<string> Topics(IReadOnlyList<ConversationTurn> turns) =>
        turns
            .Where(turn => !MetaRoutes.Contains(turn.Route) && !CommandRoutes.ContainsKey(turn.Route))
            .Select(turn => turn.User.Trim().TrimEnd('?', '.', '!'))
            .Where(IsTopic)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Kinds of errands done that day, in order of first appearance.</summary>
    public static IReadOnlyList<string> Errands(IReadOnlyList<ConversationTurn> turns) =>
        turns
            .Where(turn => CommandRoutes.ContainsKey(turn.Route))
            .Select(turn => CommandRoutes[turn.Route])
            .Distinct()
            .ToList();

    /// <summary>Recognizes the question and the day it is about (today when not said).</summary>
    public static bool TryParse(string command, DateTimeOffset now, out DateTimeOffset from, out DateTimeOffset to, out string dayWord)
    {
        string text = LocalMemoryStore.Normalize(command);
        from = to = default;
        dayWord = "";

        if (!Question.IsMatch(text))
            return false;

        DateTimeOffset today = new(now.Date, now.Offset);
        (int daysBack, dayWord) =
            text.Contains("позавчера") ? (2, "позавчера") :
            text.Contains("вчера") ? (1, "вчера") :
            (0, "сегодня");

        from = today.AddDays(-daysBack);
        to = from.AddDays(1);
        return true;
    }

    public static string Summarize(IReadOnlyList<ConversationTurn> turns, string dayWord, int maxTopics = 3)
    {
        IReadOnlyList<string> topics = Topics(turns);
        IReadOnlyList<string> errands = Errands(turns);
        string day = Capitalize(dayWord);

        if (topics.Count == 0 && errands.Count == 0)
        {
            return dayWord == "сегодня"
                ? "Сегодня мы ещё ничего толком не обсуждали."
                : $"{day} мы не разговаривали.";
        }

        var sentences = new List<string>();
        if (topics.Count > 0)
        {
            List<string> shown = topics.TakeLast(Math.Max(1, maxTopics)).ToList();
            string more = topics.Count > shown.Count ? $" и ещё {topics.Count - shown.Count}" : "";
            sentences.Add($"{day} говорили про {string.Join(", ", shown.Select(t => "«" + t + "»"))}{more}.");
        }

        if (errands.Count > 0)
        {
            sentences.Add(topics.Count > 0
                ? $"Ещё были поручения: {string.Join(", ", errands)}."
                : $"{day} в основном были поручения: {string.Join(", ", errands)}.");
        }

        return string.Join(" ", sentences);
    }

    private static bool IsTopic(string text)
    {
        string normalized = LocalMemoryStore.Normalize(text);
        return normalized.Length >= 6 &&
               normalized is not ("привет" or "спасибо" or "стоп" or "повтори" or "еще раз" or "как дела");
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
