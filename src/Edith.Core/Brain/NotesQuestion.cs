using System.Text.RegularExpressions;

namespace Edith.Core.Brain;

/// <summary>
/// Questions the owner's notes should answer: "что у меня записано про Эдит", "какие задачи по Улею",
/// "что по ИДЭУ". The notes are searched first and the model only retells them; a model left to
/// itself took "Улей" for a TV series.
/// </summary>
public static class NotesQuestion
{
    // The owner's projects, said any way speech recognition spells them.
    private static readonly Regex Project = new(
        @"\b(?:улей|улья|улье|улью|улеем|улей|uley|эдит\w*|edith|идэу|иду|идеу|ideu|иде[уэ]\w*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Cue = new(
        @"заметк|obsidian|обсидиан|хранилищ|что (?:у меня )?(?:записан|записал|записывал)|задач|план(?:ы|ах|ов)?\b|статус|этап|архитектур|решени[яей]|иде[июй]\w*|на чем (?:я )?остановил|что дальше|что (?:там )?по\b|как (?:там )?(?:дела )?(?:с|по)\b|предмет|конспект|лекци",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Explicit = new(@"заметк|obsidian|обсидиан|хранилищ", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Commands and live-PC questions are other routes' business.
    private static readonly Regex NotAboutNotes = new(
        @"^(?:открой|запусти|закрой|включи|выключи|найди в интернете|поищи)\b|как работает|что такое",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool Matches(string question)
    {
        string text = question.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (text.Length < 6 || NotAboutNotes.IsMatch(text))
            return false;

        return Explicit.IsMatch(text) || (Project.IsMatch(text) && Cue.IsMatch(text));
    }

    /// <summary>The names the notes use (Latin) for what is said in Russian.</summary>
    public static IReadOnlyList<string> Synonyms(string question)
    {
        string text = question.ToLowerInvariant().Replace('ё', 'е');
        var result = new List<string>();
        if (Regex.IsMatch(text, @"\b(?:улей|улья|улье|улью|улеем|uley)\b"))
            result.Add("uley");
        if (Regex.IsMatch(text, @"\b(?:эдит\w*|edith|e\.d\.i\.t\.h)\b"))
            result.Add("edith");
        if (Regex.IsMatch(text, @"\b(?:идэу|иду|идеу|ideu)\b"))
            result.Add("ideu");
        return result;
    }
}
