namespace Edith.Core.Runtime;

/// <summary>
/// One thing that happened inside EDITH: a wake, a heard command, a tool call,
/// a permission decision, a health or resource change. Everything the UI shows and
/// everything the journal stores is an <see cref="EdithEvent"/>.
/// </summary>
public sealed record EdithEvent(
    DateTimeOffset Timestamp,
    string Kind,
    long Session,
    string Summary,
    IReadOnlyDictionary<string, string> Data)
{
    public static EdithEvent Create(
        string kind,
        string summary,
        long session = 0,
        IReadOnlyDictionary<string, string>? data = null,
        DateTimeOffset? timestamp = null) =>
        new(
            timestamp ?? DateTimeOffset.Now,
            kind,
            session,
            summary,
            data ?? EmptyData);

    public static readonly IReadOnlyDictionary<string, string> EmptyData =
        new Dictionary<string, string>();

    public string? Get(string key) =>
        Data.TryGetValue(key, out string? value) ? value : null;
}

/// <summary>Well-known <see cref="EdithEvent.Kind"/> values. Kinds are dotted: "area.what".</summary>
public static class EventKinds
{
    public const string AppStarted = "app.started";
    public const string AppStopping = "app.stopping";

    public const string StateChanged = "voice.state";
    public const string WakeAccepted = "voice.wake";
    public const string CommandHeard = "voice.command";
    public const string BargeIn = "voice.barge-in";
    public const string ResponseSpoken = "voice.response";

    public const string RouteCompleted = "brain.route";
    public const string ToolStarted = "tool.started";
    public const string ToolFinished = "tool.finished";
    public const string ToolFailed = "tool.failed";

    public const string PermissionRequested = "permission.requested";
    public const string PermissionApproved = "permission.approved";
    public const string PermissionDenied = "permission.denied";
    public const string PermissionExecuted = "permission.executed";
    public const string PermissionFailed = "permission.failed";
    public const string PermissionExpired = "permission.expired";

    public const string HealthChanged = "health.changed";
    public const string ResourcesChanged = "resources.changed";
    public const string TaskDeferred = "task.deferred";
    public const string TaskResumed = "task.resumed";

    public const string PowerScheduled = "power.scheduled";
    public const string PowerCancelled = "power.cancelled";
    public const string PowerIssued = "power.issued";

    public const string SettingsChanged = "settings.changed";

    public const string Error = "error";

    /// <summary>Kinds that describe what EDITH actually did (answer to "что ты делала?").</summary>
    public static bool IsAction(string kind) =>
        kind.StartsWith("tool.", StringComparison.Ordinal) ||
        kind.StartsWith("permission.", StringComparison.Ordinal) ||
        kind.StartsWith("task.", StringComparison.Ordinal) ||
        kind.StartsWith("power.", StringComparison.Ordinal) ||
        kind.StartsWith("reminder.", StringComparison.Ordinal) ||
        kind.StartsWith("memory.", StringComparison.Ordinal);
}
