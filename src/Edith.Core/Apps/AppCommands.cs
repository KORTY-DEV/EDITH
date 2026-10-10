using System.Text.RegularExpressions;
using Edith.Core.Runtime;

namespace Edith.Core.Apps;

public enum AppAction
{
    Open,
    Close,
    Focus,
    Minimize
}

public sealed record AppCommand(AppAction Action, IReadOnlyList<string> Targets);

public sealed record RunningApp(int ProcessId, string ProcessName, bool HasWindow);

/// <summary>Windows operations on applications; the real one lives in the app, tests use a fake.</summary>
public interface IAppControl
{
    IReadOnlyList<RunningApp> Running();

    /// <summary>Asks the window to close, like clicking X: the app may still ask to save.</summary>
    bool RequestClose(int processId);

    bool Focus(int processId);

    bool Minimize(int processId);

    void Launch(string target);
}

public static class AppCommandParser
{
    private static readonly (Regex Verb, AppAction Action)[] Verbs =
    [
        (new(@"^(?:переключись|переключи|перейди)\s+(?:на|в)\s+", RegexOptions.Compiled), AppAction.Focus),
        (new(@"^(?:покажи|разверни)\s+", RegexOptions.Compiled), AppAction.Focus),
        (new(@"^сверни\s+", RegexOptions.Compiled), AppAction.Minimize),
        (new(@"^(?:закрой|выключи|выруби|заверши)\s+", RegexOptions.Compiled), AppAction.Close),
        (new(@"^(?:открой|запусти|включи)\s+", RegexOptions.Compiled), AppAction.Open),
    ];

    // "открой папку/файл/сайт ..." belong to other tools.
    private static readonly Regex NotAnApp = new(
        @"^(?:(?:папк|файл|сайт|ссылк|страниц|вкладк|документ|настройк)\w*|диск|окно|окна|все|всё)(?:\s|$)",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out AppCommand command)
    {
        command = new AppCommand(AppAction.Open, []);
        // Commas separate targets, so unlike AppCatalog.NormalizeName they survive here.
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[«»""'!?]|\.(?=\s|$)", " ");
        input = Regex.Replace(input, @"\s+", " ").Trim();
        input = Regex.Replace(input, @"^((?:эдит|эди|едит|еди)),", "$1");
        input = Regex.Replace(input, @"^(?:эдит|эди|едит|еди)\s+", "");
        input = Regex.Replace(input, @"^(?:пожалуйста|давай|можешь)\s+", "");

        foreach ((Regex verb, AppAction action) in Verbs)
        {
            Match match = verb.Match(input);
            if (!match.Success)
                continue;

            string rest = Regex.Replace(input[match.Length..], @",?\s+пожалуйста$", "").Trim().TrimEnd(',');
            // "открой мне телеграм", "запусти быстро дискорд": filler words are not part of the name.
            rest = Regex.Replace(rest, @"^(?:(?:мне|нам|пожалуйста|быстренько|быстро|срочно|сейчас|ка|давай|ещё|еще)\s+)+", "");
            if (rest.Length == 0 || NotAnApp.IsMatch(rest))
                return false;

            List<string> targets = Regex.Split(rest, @"\s*(?:,|\bи\b|\bа также\b)\s*")
                .Select(t => Regex.Replace(t.Trim(), @"^(?:приложение|программу|игру)\s+", ""))
                .Where(t => t.Length > 0)
                .ToList();

            if (targets.Count == 0 || targets.Count > 6)
                return false;

            command = new AppCommand(action, targets);
            return true;
        }

        return false;
    }
}

public sealed record AppPlan(
    AppCommand Command,
    IReadOnlyList<AppEntry> Apps,
    IReadOnlyList<string> Unknown)
{
    public bool NeedsConfirmation => Command.Action == AppAction.Close && Apps.Any(app => app.ConfirmClose);
}

/// <summary>Resolves spoken targets and carries out the command, journaling every step.</summary>
public sealed class AppActions(
    IReadOnlyList<AppEntry> catalog,
    Func<ShortcutIndex> shortcuts,
    IAppControl control,
    IEventBus? bus = null)
{
    /// <summary>
    /// Null when nothing in the command is an application EDITH knows: the phrase then goes on
    /// to other routes ("выключи компьютер", "открой сайт ...").
    /// </summary>
    public AppPlan? Plan(AppCommand command)
    {
        var apps = new List<AppEntry>();
        var unknown = new List<string>();

        foreach (string target in command.Targets)
        {
            AppEntry? entry = AppCatalog.Resolve(catalog, target);

            // For "открой X" any installed shortcut counts, even without a catalog entry.
            if (entry is null && command.Action == AppAction.Open && shortcuts().Find([target]) is { } shortcut)
                entry = new AppEntry { Id = "shortcut:" + shortcut.Name, Title = shortcut.Name, Shortcuts = [shortcut.Name] };

            if (entry is null)
                unknown.Add(target);
            else if (apps.All(a => a.Id != entry.Id))
                apps.Add(entry);
        }

        return apps.Count == 0 ? null : new AppPlan(command, apps, unknown);
    }

    public string Execute(AppPlan plan)
    {
        List<string> parts = plan.Command.Action switch
        {
            AppAction.Open => Open(plan.Apps),
            AppAction.Close => Close(plan.Apps),
            AppAction.Focus => Focus(plan.Apps),
            AppAction.Minimize => Minimize(plan.Apps),
            _ => [],
        };

        if (plan.Unknown.Count > 0)
            parts.Add($"Не знаю, что такое {string.Join(" и ", plan.Unknown.Select(u => "«" + u + "»"))}.");

        return string.Join(" ", parts);
    }

    private List<string> Open(IReadOnlyList<AppEntry> apps)
    {
        var opened = new List<string>();
        var failed = new List<string>();

        foreach (AppEntry app in apps)
        {
            string? target = shortcuts().Find(app.Shortcuts)?.Path ?? app.Launch;
            if (target is null)
            {
                failed.Add(app.Title);
                Journal(EventKinds.ToolFailed, "open_app", app, "ярлык не найден");
                continue;
            }

            try
            {
                control.Launch(target);
                opened.Add(app.Title);
                Journal(EventKinds.ToolFinished, "open_app", app, target);
            }
            catch (Exception ex)
            {
                failed.Add(app.Title);
                Journal(EventKinds.ToolFailed, "open_app", app, ex.Message);
            }
        }

        var result = new List<string>();
        if (opened.Count > 0)
            result.Add($"Открываю {Join(opened)}.");
        if (failed.Count > 0)
            result.Add($"Не нашла, как запустить {Join(failed)}.");
        return result;
    }

    private List<string> Close(IReadOnlyList<AppEntry> apps)
    {
        IReadOnlyList<RunningApp> running = control.Running();
        var closed = new List<string>();
        var notRunning = new List<string>();
        var refused = new List<string>();

        foreach (AppEntry app in apps)
        {
            if (!app.CanClose)
            {
                refused.Add(app.Title);
                continue;
            }

            List<RunningApp> windows = Windows(app, running);
            if (windows.Count == 0)
            {
                notRunning.Add(app.Title);
                continue;
            }

            bool any = false;
            foreach (RunningApp process in windows)
                any |= control.RequestClose(process.ProcessId);

            if (any)
            {
                closed.Add(app.Title);
                Journal(EventKinds.ToolFinished, "close_app", app, string.Join(",", windows.Select(w => w.ProcessId)));
            }
            else
            {
                refused.Add(app.Title);
                Journal(EventKinds.ToolFailed, "close_app", app, "окно не приняло закрытие");
            }
        }

        var result = new List<string>();
        if (closed.Count > 0)
            result.Add($"Закрываю {Join(closed)}.");
        if (notRunning.Count > 0)
            result.Add($"{Capitalize(Join(notRunning))} и так не {(notRunning.Count > 1 ? "запущены" : "запущен")}.");
        if (refused.Count > 0)
            result.Add($"Не стала закрывать {Join(refused)}.");
        return result;
    }

    private List<string> Focus(IReadOnlyList<AppEntry> apps)
    {
        IReadOnlyList<RunningApp> running = control.Running();
        AppEntry app = apps[0];
        RunningApp? window = Windows(app, running).FirstOrDefault();

        if (window is null)
        {
            List<string> opened = Open([app]);
            return opened.Count > 0 && opened[0].StartsWith("Открываю", StringComparison.Ordinal)
                ? [$"{app.Title} не был запущен, открываю."]
                : opened;
        }

        bool ok = control.Focus(window.ProcessId);
        Journal(ok ? EventKinds.ToolFinished : EventKinds.ToolFailed, "focus_app", app, window.ProcessId.ToString());
        return [ok ? $"Переключаюсь на {app.Title}." : $"Windows не дала переключиться на {app.Title}."];
    }

    private List<string> Minimize(IReadOnlyList<AppEntry> apps)
    {
        IReadOnlyList<RunningApp> running = control.Running();
        var done = new List<string>();

        foreach (AppEntry app in apps)
        {
            foreach (RunningApp window in Windows(app, running))
            {
                if (control.Minimize(window.ProcessId))
                {
                    done.Add(app.Title);
                    Journal(EventKinds.ToolFinished, "minimize_app", app, window.ProcessId.ToString());
                    break;
                }
            }
        }

        return done.Count > 0 ? [$"Свернула {Join(done)}."] : ["Не нашла открытых окон."];
    }

    private static List<RunningApp> Windows(AppEntry app, IReadOnlyList<RunningApp> running) =>
        running
            .Where(p => p.HasWindow &&
                        !AppCatalog.IsProtected(p.ProcessName) &&
                        app.Processes.Any(name => string.Equals(name, p.ProcessName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    private void Journal(string kind, string tool, AppEntry app, string detail) =>
        bus?.Publish(EdithEvent.Create(
            kind,
            tool,
            data: new Dictionary<string, string>
            {
                ["tool"] = tool,
                ["arguments"] = app.Id,
                ["app"] = app.Title,
                ["result"] = detail,
            }));

    private static string Join(IReadOnlyList<string> names) =>
        names.Count <= 1 ? string.Concat(names) : string.Join(", ", names.Take(names.Count - 1)) + " и " + names[^1];

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
