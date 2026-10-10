using System.Text.Json;
using System.Text.RegularExpressions;
using Edith.Core.Runtime;
using Edith.Core.Time;

namespace Edith.Core.Reminders;

/// <param name="Attempt">0 = first time; repeats happen when the user did not answer.</param>
public sealed record Reminder(Guid Id, string Text, DateTimeOffset DueAt, DateTimeOffset CreatedAt, int Attempt = 0)
{
    /// <summary>What EDITH says: first a plain reminder, then a question until answered.</summary>
    public string Spoken => Attempt == 0
        ? $"Напоминаю: {Text}."
        : $"Напоминаю ещё раз: {Text}. Сделано?";
}

/// <summary>Short answers that close a reminder: "хорошо", "ок", "сделал", "поняла"...</summary>
public static class ReminderAck
{
    private static readonly HashSet<string> Words =
    [
        "хорошо", "ок", "окей", "ok", "okay", "понял", "поняла", "понятно", "принято", "да", "ага", "угу",
        "ладно", "сделал", "сделала", "сделано", "готово", "спасибо", "запомнил", "запомнила", "помню", "угу спасибо",
    ];

    public static bool IsAcknowledgement(string text)
    {
        string normalized = System.Text.RegularExpressions.Regex.Replace(
            text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[,.!?]", " ").Trim();
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"\s+", " ");
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"^(?:эдит|да|ну)\s+", "");
        return Words.Contains(normalized) ||
               normalized.Split(' ').Length <= 3 && normalized.Split(' ').Any(Words.Contains);
    }
}

public enum ReminderCommandKind
{
    Add,
    List,
    CancelAll,
    CancelLast
}

public sealed record ReminderCommand(ReminderCommandKind Kind, string Text = "", DateTimeOffset? DueAt = null);

/// <summary>
/// "напомни мне через час позвонить маме", "напомни завтра в 9 про встречу",
/// "напомни выключить духовку через 20 минут", "какие у меня напоминания", "отмени напоминания".
/// </summary>
public static class ReminderParser
{
    private const string Head = @"^(?:эдит\s+)?(?:напомни|напомнишь|поставь напоминание)(?:\s+мне)?\s+";
    private const string Lead = @"(?:(?:о том|про то)?\s*что\s+|про\s+|о\s+|чтобы\s+|что бы\s+)?";

    private static readonly Regex TimeFirstDelay = new(Head + SpokenTime.DelayPattern + @"\s+" + Lead + @"(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex TimeFirstAt = new(Head + SpokenTime.AtPattern + @"\s+" + Lead + @"(?<text>.+)$", RegexOptions.Compiled);
    private static readonly Regex TextFirstDelay = new(Head + Lead + @"(?<text>.+?)\s+" + SpokenTime.DelayPattern + @"$", RegexOptions.Compiled);
    private static readonly Regex TextFirstAt = new(Head + Lead + @"(?<text>.+?)\s+" + SpokenTime.AtPattern + @"$", RegexOptions.Compiled);

    private static readonly Regex List = new(
        @"(?:какие|что)\s+(?:у меня\s+)?(?:есть\s+)?напоминани|(?:что|о чем)\s+(?:ты\s+)?(?:должна\s+)?мне\s+напомнить|список напоминаний",
        RegexOptions.Compiled);

    private static readonly Regex CancelAll = new(@"(?:отмени|удали|сбрось)\s+(?:все\s+)?напоминания", RegexOptions.Compiled);
    private static readonly Regex CancelLast = new(@"(?:отмени|удали)\s+(?:последнее\s+)?напоминание", RegexOptions.Compiled);

    public static bool TryParse(string text, DateTimeOffset now, out ReminderCommand command)
    {
        string input = SpokenTime.Normalize(text).TrimEnd('.');
        command = new ReminderCommand(ReminderCommandKind.List);

        if (CancelAll.IsMatch(input))
        {
            command = new ReminderCommand(ReminderCommandKind.CancelAll);
            return true;
        }

        if (CancelLast.IsMatch(input))
        {
            command = new ReminderCommand(ReminderCommandKind.CancelLast);
            return true;
        }

        if (List.IsMatch(input))
            return true;

        foreach (Regex pattern in new[] { TimeFirstDelay, TimeFirstAt, TextFirstDelay, TextFirstAt })
        {
            Match match = pattern.Match(input);
            if (!match.Success)
                continue;

            DateTimeOffset due;
            if (match.Groups["unit"].Success)
            {
                if (!SpokenTime.TryDelay(match, out TimeSpan delay))
                    continue;
                due = now + delay;
            }
            else if (!SpokenTime.TryAt(match, now, out due))
            {
                continue;
            }

            string what = ToSecondPerson(match.Groups["text"].Value.Trim());
            if (what.Length < 2)
                continue;

            command = new ReminderCommand(ReminderCommandKind.Add, what, due);
            return true;
        }

        return false;
    }

    /// <summary>"мне надо позвонить моей маме" → "тебе надо позвонить твоей маме".</summary>
    public static string ToSecondPerson(string text)
    {
        var swap = new Dictionary<string, string>
        {
            ["я"] = "ты", ["мне"] = "тебе", ["меня"] = "тебя", ["мной"] = "тобой",
            ["мой"] = "твой", ["моя"] = "твоя", ["мое"] = "твое", ["мои"] = "твои",
            ["моего"] = "твоего", ["моей"] = "твоей", ["моему"] = "твоему", ["мою"] = "твою", ["моих"] = "твоих",
        };

        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => swap.TryGetValue(word, out string? replaced) ? replaced : word));
    }
}

/// <summary>
/// Pending reminders, persisted so a restart does not lose them. <see cref="TakeDue"/> hands
/// out each reminder exactly once; one that fell due while EDITH was off is still delivered
/// on the next start (late, but not lost).
/// </summary>
public sealed class ReminderScheduler
{
    private readonly string? _path;
    private readonly IEventBus? _bus;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();
    private readonly List<Reminder> _items;

    public ReminderScheduler(string? path = null, IEventBus? bus = null, Func<DateTimeOffset>? clock = null)
    {
        _path = path;
        _bus = bus;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _items = Load(path);
    }

    public IReadOnlyList<Reminder> Pending
    {
        get
        {
            lock (_gate)
                return _items.OrderBy(r => r.DueAt).ToList();
        }
    }

    /// <summary>Repeats after an unanswered reminder: every 5 minutes, at most twice.</summary>
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMinutes(5);
    public const int MaxRepeats = 2;

    private Reminder? _awaitingAck;

    /// <summary>The reminder EDITH just said and is waiting for an answer to.</summary>
    public Reminder? AwaitingAck
    {
        get
        {
            lock (_gate)
                return _awaitingAck;
        }
    }

    public void BeginAwaitingAck(Reminder reminder)
    {
        lock (_gate)
            _awaitingAck = reminder;
    }

    /// <summary>The user answered (or simply engaged): the reminder is closed.</summary>
    public bool Acknowledge()
    {
        Reminder? acknowledged;
        lock (_gate)
        {
            acknowledged = _awaitingAck;
            _awaitingAck = null;
        }

        if (acknowledged is not null)
            Publish("reminder.acknowledged", acknowledged);
        return acknowledged is not null;
    }

    /// <summary>No answer came: ask again later, up to <see cref="MaxRepeats"/> times.</summary>
    public Reminder? ExpireAck()
    {
        Reminder? unanswered;
        lock (_gate)
        {
            unanswered = _awaitingAck;
            _awaitingAck = null;
        }

        if (unanswered is null || unanswered.Attempt >= MaxRepeats)
            return null;

        return Add(unanswered.Text, _clock() + RepeatDelay, unanswered.Attempt + 1);
    }

    public Reminder Add(string text, DateTimeOffset dueAt, int attempt = 0)
    {
        var reminder = new Reminder(Guid.NewGuid(), text, dueAt, _clock(), attempt);
        lock (_gate)
        {
            _items.Add(reminder);
            Save();
        }

        Publish("reminder.added", reminder);
        return reminder;
    }

    public IReadOnlyList<Reminder> TakeDue()
    {
        DateTimeOffset now = _clock();
        List<Reminder> due;
        lock (_gate)
        {
            due = _items.Where(r => r.DueAt <= now).OrderBy(r => r.DueAt).ToList();
            if (due.Count == 0)
                return due;

            _items.RemoveAll(r => r.DueAt <= now);
            Save();
        }

        foreach (Reminder reminder in due)
            Publish("reminder.due", reminder);
        return due;
    }

    public int CancelAll()
    {
        List<Reminder> removed;
        lock (_gate)
        {
            removed = _items.ToList();
            _items.Clear();
            Save();
        }

        foreach (Reminder reminder in removed)
            Publish("reminder.cancelled", reminder);
        return removed.Count;
    }

    public Reminder? CancelLast()
    {
        Reminder? last;
        lock (_gate)
        {
            last = _items.OrderBy(r => r.CreatedAt).LastOrDefault();
            if (last is null)
                return null;
            _items.Remove(last);
            Save();
        }

        Publish("reminder.cancelled", last);
        return last;
    }

    public static string DescribePending(IReadOnlyList<Reminder> pending, DateTimeOffset now)
    {
        if (pending.Count == 0)
            return "Напоминаний нет.";

        IEnumerable<string> items = pending.Take(3).Select(r => $"{SpokenTime.Describe(r.DueAt, now)} — {r.Text}");
        string more = pending.Count > 3 ? $" И ещё {pending.Count - 3}." : "";
        return $"Напоминаний: {pending.Count}. " + string.Join("; ", items) + "." + more;
    }

    private void Publish(string kind, Reminder reminder) =>
        _bus?.Publish(EdithEvent.Create(
            kind,
            reminder.Text,
            data: new Dictionary<string, string>
            {
                ["id"] = reminder.Id.ToString(),
                ["due"] = reminder.DueAt.ToString("O"),
            }));

    private void Save()
    {
        if (_path is null)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, _path, true);
        }
        catch
        {
        }
    }

    private static List<Reminder> Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<List<Reminder>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
