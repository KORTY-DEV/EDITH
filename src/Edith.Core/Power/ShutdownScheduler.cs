using System.Globalization;
using System.Text.Json;
using Edith.Core.Runtime;

namespace Edith.Core.Power;

public enum ShutdownTrigger
{
    None,
    AtTime,
    AfterAgent
}

public sealed record ShutdownState(
    ShutdownTrigger Trigger,
    DateTimeOffset? At,
    DateTimeOffset ArmedAt,
    bool Issued = false);

/// <summary>What the scheduler needs from Windows; the real one runs shutdown.exe.</summary>
public interface IPowerControl
{
    /// <summary>Starts a Windows shutdown with a visible warning; can be aborted during the grace period.</summary>
    void ShutdownWithGrace(TimeSpan grace, string reason);

    void AbortShutdown();
}

/// <summary>
/// Activity of an AI agent as seen through its hooks: the Stop hook touches the "stop" marker,
/// the UserPromptSubmit hook touches the "busy" marker (see scripts/Install-AgentHook.ps1).
/// </summary>
public interface IAgentSignals
{
    DateTimeOffset? LastStop { get; }

    DateTimeOffset? LastBusy { get; }
}

/// <summary>
/// Delayed PC shutdown: "через 4 часа", "в 23:30", "когда агент закончит".
/// Driven by <see cref="Tick"/> (the app calls it every few seconds). Nothing happens
/// without an explicit, already confirmed <see cref="Schedule"/> call, and Windows still
/// shows a one-minute warning that "отмени выключение" can abort.
/// </summary>
public sealed class ShutdownScheduler
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60);

    /// <summary>The agent must stay quiet this long after its Stop hook before we act.</summary>
    public static readonly TimeSpan AgentQuietPeriod = TimeSpan.FromMinutes(2);

    private readonly IPowerControl _power;
    private readonly IAgentSignals _agent;
    private readonly Func<DateTimeOffset> _clock;
    private readonly IEventBus? _bus;
    private readonly string? _statePath;
    private readonly object _gate = new();
    private ShutdownState? _state;

    public ShutdownScheduler(
        IPowerControl power,
        IAgentSignals agent,
        Func<DateTimeOffset>? clock = null,
        IEventBus? bus = null,
        string? statePath = null)
    {
        _power = power;
        _agent = agent;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _bus = bus;
        _statePath = statePath;
        _state = Load(statePath);

        // A shutdown already handed to Windows is not re-issued after an EDITH restart.
        if (_state is { Issued: true })
            _state = null;
    }

    public ShutdownState? Current
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public ShutdownState Schedule(ShutdownCommand command)
    {
        DateTimeOffset now = _clock();
        ShutdownState state = command.Kind switch
        {
            ShutdownCommandKind.ScheduleAt when command.At is { } at => new(ShutdownTrigger.AtTime, at, now),
            ShutdownCommandKind.AfterAgent => new(ShutdownTrigger.AfterAgent, null, now),
            _ => throw new ArgumentException("Not a scheduling command.", nameof(command)),
        };

        lock (_gate)
        {
            _state = state;
            Save();
        }

        Publish(EventKinds.PowerScheduled, Describe(state, now), state);
        return state;
    }

    /// <summary>Cancels a pending plan, and aborts Windows' countdown if it was already started.</summary>
    public bool Cancel()
    {
        ShutdownState? previous;
        lock (_gate)
        {
            previous = _state;
            _state = null;
            Save();
        }

        if (previous is null)
            return false;

        if (previous.Issued)
        {
            try
            {
                _power.AbortShutdown();
            }
            catch
            {
            }
        }

        Publish(EventKinds.PowerCancelled, "Выключение отменено", previous);
        return true;
    }

    /// <summary>Checks the trigger; issues the shutdown once when it is due.</summary>
    public void Tick()
    {
        DateTimeOffset now = _clock();
        ShutdownState? due;

        lock (_gate)
        {
            if (_state is null || _state.Issued || !IsDue(_state, now))
                return;

            _state = _state with { Issued = true };
            due = _state;
            Save();
        }

        string reason = due.Trigger == ShutdownTrigger.AfterAgent
            ? "ИИ-агент закончил работу — EDITH выключает компьютер."
            : "EDITH выключает компьютер по расписанию.";

        try
        {
            _power.ShutdownWithGrace(Grace, reason);
            Publish(EventKinds.PowerIssued, reason, due);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _state = null;
                Save();
            }

            Publish(EventKinds.Error, "Не удалось выключить компьютер: " + ex.Message, due);
        }
    }

    public static string Describe(ShutdownState? state, DateTimeOffset now)
    {
        if (state is null)
            return "Выключение не запланировано.";

        if (state.Issued)
            return "Компьютер выключится меньше чем через минуту.";

        return state.Trigger switch
        {
            ShutdownTrigger.AfterAgent => "Выключу компьютер, когда агент закончит работу.",
            ShutdownTrigger.AtTime when state.At is { } at => $"Выключу компьютер в {at.ToString("HH:mm", CultureInfo.InvariantCulture)}{Remaining(at - now)}.",
            _ => "Выключение не запланировано.",
        };
    }

    private bool IsDue(ShutdownState state, DateTimeOffset now) => state.Trigger switch
    {
        ShutdownTrigger.AtTime => state.At is { } at && now >= at,
        ShutdownTrigger.AfterAgent => AgentFinished(state, now),
        _ => false,
    };

    private bool AgentFinished(ShutdownState state, DateTimeOffset now)
    {
        DateTimeOffset? stop = _agent.LastStop;
        DateTimeOffset? busy = _agent.LastBusy;

        return stop is { } s &&
               s >= state.ArmedAt &&
               (busy is null || busy.Value <= s) &&
               now - s >= AgentQuietPeriod;
    }

    private static string Remaining(TimeSpan left)
    {
        if (left <= TimeSpan.Zero)
            return "";

        int hours = (int)left.TotalHours;
        int minutes = (int)Math.Ceiling(left.TotalMinutes - hours * 60);
        if (minutes == 60)
        {
            hours++;
            minutes = 0;
        }

        string text = hours > 0 && minutes > 0 ? $"{hours} ч {minutes} мин" :
                      hours > 0 ? $"{hours} ч" :
                      $"{minutes} мин";
        return $", через {text}";
    }

    private void Publish(string kind, string summary, ShutdownState state) =>
        _bus?.Publish(EdithEvent.Create(
            kind,
            summary,
            data: new Dictionary<string, string>
            {
                ["trigger"] = state.Trigger.ToString(),
                ["at"] = state.At?.ToString("O") ?? "",
                ["issued"] = state.Issued.ToString(),
            }));

    private void Save()
    {
        if (_statePath is null)
            return;

        try
        {
            string? directory = Path.GetDirectoryName(Path.GetFullPath(_statePath));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            if (_state is null)
            {
                File.Delete(_statePath);
                return;
            }

            File.WriteAllText(_statePath, JsonSerializer.Serialize(_state));
        }
        catch
        {
        }
    }

    private static ShutdownState? Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<ShutdownState>(File.ReadAllText(path))
                : null;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>Reads the marker files written by the agent hooks.</summary>
public sealed class AgentMarkerFiles(string directory) : IAgentSignals
{
    public const string StopFile = "agent-stop";
    public const string BusyFile = "agent-busy";

    public DateTimeOffset? LastStop => Stamp(StopFile);

    public DateTimeOffset? LastBusy => Stamp(BusyFile);

    private DateTimeOffset? Stamp(string name)
    {
        try
        {
            string path = Path.Combine(directory, name);
            return File.Exists(path) ? new DateTimeOffset(File.GetLastWriteTime(path)) : null;
        }
        catch
        {
            return null;
        }
    }
}
