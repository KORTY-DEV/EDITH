using System.Globalization;
using System.Text;

namespace Edith.Core.Activity;

/// <summary>What the pulse noticed: a short dossier line for the brain and the cards for the owner.</summary>
public sealed record PulseResult(string Dossier, IReadOnlyList<PulseCard> Cards);

/// <summary>
/// Turns what is measured (repositories, program usage) into things worth telling. Deterministic
/// rules, no model: cheap, free of cloud and of invented facts. Every card has a key that makes it
/// appear once ("Улей: правки не сохранены" is told once per last commit, not every half hour).
/// </summary>
public static class PulseBuilder
{
    public const int ActiveWithinDays = 14;
    public const int StaleAfterDays = 4;

    /// <summary>Friendly program names; null hides a program (shell, EDITH herself, lock screen).</summary>
    public static string? FriendlyApp(string process)
    {
        string p = process.Trim().ToLowerInvariant();
        if (p.EndsWith(".exe", StringComparison.Ordinal))
            p = p[..^4];

        return p switch
        {
            "" or "explorer" or "edith.app" or "lockapp" or "shellhost" or "searchhost" or "startmenuexperiencehost"
                or "shellexperiencehost" or "textinputhost" or "applicationframehost" or "systemsettings"
                or "dwm" or "idle" or "taskmgr" or "snippingtool" or "screenclippinghost" => null,
            "worldoftanks" or "wot" or "wotlauncher" or "lgc_renderer_host" or "tanki" => "World of Tanks",
            "opera" or "opera_gx" or "operagx" => "Opera GX",
            "chrome" => "Chrome",
            "msedge" => "Edge",
            "firefox" => "Firefox",
            "comet" => "Comet",
            "telegram" => "Telegram",
            "code" => "VS Code",
            "devenv" => "Visual Studio",
            "obsidian" => "Obsidian",
            "discord" => "Discord",
            "steam" => "Steam",
            _ => process.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? process.Trim()[..^4] : process.Trim(),
        };
    }

    public static bool IsGame(string friendly) =>
        friendly is "World of Tanks" or "Brawlhalla" or "Factorio" or "ProjectZomboid" or "Project Zomboid" or "Steam"
            or "Minecraft" or "Dota 2" or "cs2";

    public static PulseResult Build(IReadOnlyList<RepoState> repos, IReadOnlyList<UsageRow> usage, DateTimeOffset now)
    {
        var cards = new List<PulseCard>();
        PulseCard Card(string kind, string key, string title, string body) => new(0, now, kind, key, title, body, ActivityStore.CardNew);

        List<RepoState> active = repos
            .Where(r => r.LastCommit is { } at && now - at <= TimeSpan.FromDays(ActiveWithinDays))
            .OrderByDescending(r => r.LastCommit)
            .ToList();

        foreach (RepoState repo in active)
        {
            int days = DaysSince(repo.LastCommit!.Value, now);
            string stamp = repo.LastCommit.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            if (repo.DirtyFiles > 0 && days >= 1)
            {
                cards.Add(Card("project", $"dirty:{repo.Name}:{stamp}",
                    $"{repo.Name}: не всё сохранено",
                    $"В проекте {repo.Name} {Files(repo.DirtyFiles)} изменено и не закоммичено, последний коммит {Ago(days)}" +
                    (repo.LastSubject.Length > 0 ? $": «{Clip(repo.LastSubject, 80)}»." : ".")));
            }
            else if (days >= StaleAfterDays)
            {
                cards.Add(Card("project", $"stale:{repo.Name}:{stamp}",
                    $"{repo.Name}: давно не трогали",
                    $"К проекту {repo.Name} не возвращались {Days(days)}. Последнее: «{Clip(repo.LastSubject, 80)}»."));
            }
        }

        // One review per ISO week, written the first time the pulse runs in the new week.
        (int year, int week) = IsoWeek(now);
        if (now.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Tuesday && (active.Count > 0 || usage.Count > 0))
        {
            string body = WeekBody(active, usage, now);
            if (body.Length > 0)
                cards.Add(Card("week", $"week:{year}-{week}", "Итоги недели", body));
        }

        return new PulseResult(Dossier(active, usage, now), cards);
    }

    /// <summary>
    /// What to say at the first greeting of the day: what was worked on yesterday and today, how long the
    /// computer was in use, and how many notes wait. Empty when there is nothing true to say.
    /// </summary>
    public static string Brief(IReadOnlyList<RepoState> repos, IReadOnlyList<UsageRow> usage, DateTimeOffset now, int newCards)
    {
        var parts = new List<string>();
        var worked = repos.Where(r => r.LastCommit is { } at && DaysSince(at, now) <= 1).OrderByDescending(r => r.LastCommit).Take(2).ToList();
        if (worked.Count > 0)
        {
            parts.Add("Над проектами " + string.Join(" и ", worked.Select(r => r.Name)) + " вы работали " +
                (DaysSince(worked[0].LastCommit!.Value, now) == 0 ? "сегодня" : "вчера") +
                (MostlyRussian(worked[0].LastSubject) ? $", последнее: «{Clip(worked[0].LastSubject, 70)}»" : ""));
        }

        DateTimeOffset yesterday = now.AddDays(-1);
        Dictionary<string, double> hours = AppHours(usage, yesterday, yesterday);
        double total = hours.Values.Sum();
        if (total >= 1)
        {
            KeyValuePair<string, double> top = hours.OrderByDescending(p => p.Value).First();
            parts.Add($"вчера за компьютером около {Hours(total)}, больше всего в {top.Key}");
        }

        if (newCards > 0)
            parts.Add(newCards == 1 ? "есть одна новая заметка" : $"новых заметок: {newCards}");

        return parts.Count == 0 ? "" : string.Join(". ", parts) + ".";
    }

    /// <summary>A line for the brain's context: what the owner works on and how the evenings go.</summary>
    public static string Dossier(IReadOnlyList<RepoState> active, IReadOnlyList<UsageRow> usage, DateTimeOffset now, int budget = 320)
    {
        var text = new StringBuilder();
        if (active.Count > 0)
        {
            text.Append("проекты: ").Append(string.Join(", ", active.Take(4).Select(r =>
                $"{r.Name} ({(DaysSince(r.LastCommit!.Value, now) == 0 ? "сегодня" : Ago(DaysSince(r.LastCommit.Value, now)))})")));
        }

        Dictionary<string, double> hours = AppHours(usage, now.AddDays(-ActiveWithinDays), now);
        if (hours.Count > 0)
        {
            if (text.Length > 0)
                text.Append("; ");
            text.Append("чаще всего за компьютером: ").Append(string.Join(", ", hours.OrderByDescending(p => p.Value).Take(3).Select(p => p.Key)));
        }

        string? gaming = GamingWindow(usage, now.AddDays(-ActiveWithinDays), now);
        if (gaming is not null)
        {
            if (text.Length > 0)
                text.Append("; ");
            text.Append("играет обычно ").Append(gaming);
        }

        string result = text.ToString();
        return result.Length <= budget ? result : result[..budget].TrimEnd(' ', ',', ';') + "…";
    }

    /// <summary>"вечером (19–23 ч)" when most game time falls into one stretch of the day.</summary>
    public static string? GamingWindow(IReadOnlyList<UsageRow> usage, DateTimeOffset from, DateTimeOffset to)
    {
        var perHour = new double[24];
        foreach (UsageRow row in usage)
        {
            string? app = FriendlyApp(row.App);
            DateTimeOffset at = new(row.Day.ToDateTime(TimeOnly.MinValue), to.Offset);
            if (app is null || !IsGame(app) || at < from.Date || at > to)
                continue;
            perHour[row.Hour] += row.Seconds;
        }

        double total = perHour.Sum();
        if (total < 3600)
            return null;

        int bestStart = 0;
        double best = 0;
        for (int start = 0; start < 24; start++)
        {
            double sum = 0;
            for (int k = 0; k < 4; k++)
                sum += perHour[(start + k) % 24];
            if (sum > best)
            {
                best = sum;
                bestStart = start;
            }
        }

        if (best < total * 0.6)
            return null;

        string part = bestStart switch
        {
            >= 5 and < 12 => "утром",
            >= 12 and < 18 => "днём",
            >= 18 and < 24 => "вечером",
            _ => "ночью",
        };
        return $"{part} ({bestStart}–{(bestStart + 4) % 24} ч)";
    }

    public static Dictionary<string, double> AppHours(IReadOnlyList<UsageRow> usage, DateTimeOffset from, DateTimeOffset to)
    {
        var seconds = new Dictionary<string, double>();
        foreach (UsageRow row in usage)
        {
            DateTimeOffset at = new(row.Day.ToDateTime(TimeOnly.MinValue), to.Offset);
            if (at < from.Date || at > to)
                continue;
            string? app = FriendlyApp(row.App);
            if (app is null)
                continue;
            seconds[app] = seconds.GetValueOrDefault(app) + row.Seconds;
        }

        return seconds.Where(p => p.Value >= 600).ToDictionary(p => p.Key, p => p.Value / 3600.0);
    }

    private static string WeekBody(IReadOnlyList<RepoState> active, IReadOnlyList<UsageRow> usage, DateTimeOffset now)
    {
        var parts = new List<string>();
        var worked = active.Where(r => r.Commits7d > 0).OrderByDescending(r => r.Commits7d).Take(3).ToList();
        if (worked.Count > 0)
            parts.Add("в проектах: " + string.Join(", ", worked.Select(r => $"{r.Name} — {Commits(r.Commits7d)}")));

        Dictionary<string, double> hours = AppHours(usage, now.AddDays(-7), now);
        if (hours.Count > 0)
        {
            KeyValuePair<string, double> top = hours.OrderByDescending(p => p.Value).First();
            parts.Add($"больше всего времени в {top.Key} — около {Hours(top.Value)}");
            double games = hours.Where(p => IsGame(p.Key)).Sum(p => p.Value);
            if (games >= 1)
                parts.Add($"игры — около {Hours(games)}");
        }

        return parts.Count == 0 ? "" : "За прошедшую неделю: " + string.Join("; ", parts) + ".";
    }

    /// <summary>A commit subject in English would be read aloud letter by letter: say it only when it is Russian.</summary>
    public static bool MostlyRussian(string text)
    {
        int cyrillic = text.Count(c => c is >= 'А' and <= 'я' or 'ё' or 'Ё');
        int latin = text.Count(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return cyrillic > 0 && cyrillic >= latin;
    }

    public static int DaysSince(DateTimeOffset at, DateTimeOffset now) => Math.Max(0, (now.Date - at.Date).Days);

    public static string Ago(int days) => days switch
    {
        0 => "сегодня",
        1 => "вчера",
        _ => Days(days) + " назад",
    };

    public static string Days(int n) => $"{n} {Plural(n, "день", "дня", "дней")}";

    public static string Files(int n) => $"{n} {Plural(n, "файл", "файла", "файлов")}";

    public static string Commits(int n) => $"{n} {Plural(n, "коммит", "коммита", "коммитов")}";

    public static string Hours(double hours) => hours < 1.5 ? "час" : $"{Math.Round(hours)} {Plural((int)Math.Round(hours), "час", "часа", "часов")}";

    public static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100, mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
            return many;
        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }

    private static (int Year, int Week) IsoWeek(DateTimeOffset at) =>
        (ISOWeek.GetYear(at.DateTime), ISOWeek.GetWeekOfYear(at.DateTime));

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
