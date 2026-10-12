using System.Text.RegularExpressions;
using Edith.Core.Memory;

namespace Edith.Core.Screen;

/// <summary>What is in the window in front of the user, and where it came from.</summary>
/// <param name="Source">"uia" (the app's own accessibility tree) or "ocr" (text read from pixels).</param>
public sealed record ScreenContent(string App, string Title, string Text, string Source);

public sealed record UiElementInfo(string Name, string Kind);

/// <summary>Reads and operates the foreground window; implemented with UI Automation + OCR in the app.</summary>
public interface IScreen
{
    Task<ScreenContent?> ReadForegroundAsync(CancellationToken cancellationToken);

    /// <summary>Clickable things of the foreground window (buttons, menu items, links, tabs...).</summary>
    IReadOnlyList<UiElementInfo> Clickables();

    /// <summary>Invokes the element with exactly this name; false when it vanished or refused.</summary>
    bool Click(UiElementInfo element);

    /// <summary>Types text at the caret of the foreground window, like the keyboard would.</summary>
    void Type(string text);
}

public enum ScreenCommandKind
{
    Read,
    Click,
    Type
}

public sealed record ScreenCommand(ScreenCommandKind Kind, string Argument = "");

public static class ScreenCommandParser
{
    // "что на экране" / "посмотри на экран" are not here on purpose: with two monitors the window holding the focus is often not what the owner
    // looks at, so those go to the brain, which describes the monitor under the mouse (describe_screen).
    private static readonly Regex Read = new(
        @"^(?:что (?:у меня )?(?:в (?:этом |активном )?окне|открыто)|прочитай (?:(?:что )?(?:на экране|в окне|окно|ошибку|сообщение|текст на экране))|что (?:там )?написано(?: на экране| в окне)?|что за ошибка)$",
        RegexOptions.Compiled);

    private static readonly Regex Click = new(
        @"^(?:нажми|кликни|щелкни|ткни)\s+(?:на\s+)?(?:кнопку\s+|пункт\s+|ссылку\s+|вкладку\s+)?(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex Type = new(
        @"^(?:напечатай|введи|впиши|набери)\s+(?:текст\s+)?(?<text>.+)$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out ScreenCommand command)
    {
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[?!]|\.$", " ");
        input = Regex.Replace(Regex.Replace(input, @"\s+", " ").Trim(), @"^(?:эдит|пожалуйста)\s*,?\s*", "");
        command = new ScreenCommand(ScreenCommandKind.Read);

        if (Read.IsMatch(input))
            return true;

        Match click = Click.Match(input);
        if (click.Success)
        {
            command = new ScreenCommand(ScreenCommandKind.Click, click.Groups["name"].Value.Trim(' ', '«', '»', '"'));
            return true;
        }

        // Typing keeps the user's original casing and punctuation.
        Match type = Type.Match(input);
        if (type.Success)
        {
            Match original = Regex.Match(text.Trim(), @"(?:напечатай|введи|впиши|набери)\s+(?:текст\s+)?(?<text>.+)$", RegexOptions.IgnoreCase);
            command = new ScreenCommand(ScreenCommandKind.Type, (original.Success ? original.Groups["text"].Value : type.Groups["text"].Value).Trim(' ', '«', '»', '"'));
            return true;
        }

        return false;
    }
}

public static class UiTargets
{
    // Buttons whose press is hard to undo or leaves the PC: EDITH asks before pressing them.
    private static readonly Regex Risky = new(
        @"удал|стер|очист|формат|отправ|опубликов|купит|оплат|заплат|перевест|подтверд|принять|соглас|выйти|выход|завершить работу|перезагруз|выключ|сбросить|reset|delete|remove|erase|format|send|post|publish|buy|pay|purchase|confirm|accept|agree|sign out|log out|shut ?down|restart",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsRisky(string name) => Risky.IsMatch(name);

    /// <summary>The spoken name → best element: exact, then starts-with, then same word stems, then contains.</summary>
    public static UiElementInfo? Best(IReadOnlyList<UiElementInfo> elements, string spoken)
    {
        string wanted = Norm(spoken);
        if (wanted.Length == 0)
            return null;

        UiElementInfo? Pick(Func<string, bool> test) =>
            elements.Where(e => test(Norm(e.Name))).OrderBy(e => e.Name.Length).FirstOrDefault();

        string stems = string.Join(' ', RussianStemmer.Words(wanted).Select(RussianStemmer.Stem));
        return Pick(n => n == wanted)
            ?? Pick(n => n.StartsWith(wanted, StringComparison.Ordinal))
            ?? Pick(n => n.Length > 0 && string.Join(' ', RussianStemmer.Words(n).Select(RussianStemmer.Stem)) == stems)
            ?? (wanted.Length >= 4 ? Pick(n => n.Contains(wanted, StringComparison.Ordinal)) : null);
    }

    /// <summary>UIA text: names in reading order, without duplicates and noise, bounded for speech/prompts.</summary>
    public static string JoinText(IEnumerable<string> parts, int maxChars = 3000)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        int total = 0;

        foreach (string raw in parts)
        {
            string line = Regex.Replace(raw ?? "", @"\s+", " ").Trim();
            if (line.Length < 2 || !seen.Add(line))
                continue;

            lines.Add(line);
            total += line.Length + 1;
            if (total >= maxChars)
                break;
        }

        string text = string.Join("\n", lines);
        return text.Length <= maxChars ? text : text[..maxChars];
    }

    private static string Norm(string text) =>
        Regex.Replace(Regex.Replace(LocalMemoryStore.Normalize(text), @"[&_«»""'.,:!?…]", " "), @"\s+", " ").Trim();
}
