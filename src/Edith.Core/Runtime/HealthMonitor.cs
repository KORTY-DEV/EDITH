namespace Edith.Core.Runtime;

public enum HealthStatus
{
    Unknown,
    Ok,
    Degraded,
    Failed
}

public sealed record HealthResult(HealthStatus Status, string Detail)
{
    public static HealthResult Ok(string detail = "") => new(HealthStatus.Ok, detail);
    public static HealthResult Degraded(string detail) => new(HealthStatus.Degraded, detail);
    public static HealthResult Failed(string detail) => new(HealthStatus.Failed, detail);
}

/// <summary>A named probe. <paramref name="Title"/> is spoken/shown to the user (Russian).</summary>
public sealed record HealthCheck(
    string Name,
    string Title,
    Func<CancellationToken, Task<HealthResult>> Probe);

/// <summary>
/// Runs health probes and publishes <see cref="EventKinds.HealthChanged"/> only on status
/// transitions, so a stuck microphone produces one event instead of one per poll.
/// Every probe has its own timeout; a throwing or hanging probe counts as Failed.
/// </summary>
public sealed class HealthMonitor
{
    private readonly IReadOnlyList<HealthCheck> _checks;
    private readonly IEventBus _bus;
    private readonly TimeSpan _timeout;
    private readonly object _gate = new();
    private readonly Dictionary<string, HealthResult> _current = new(StringComparer.Ordinal);

    public HealthMonitor(IEnumerable<HealthCheck> checks, IEventBus bus, TimeSpan? probeTimeout = null)
    {
        _checks = checks.ToList();
        _bus = bus;
        _timeout = probeTimeout ?? TimeSpan.FromSeconds(5);
    }

    public IReadOnlyDictionary<string, HealthResult> Current
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, HealthResult>(_current);
        }
    }

    /// <summary>Worst status across all checks; Unknown before the first run.</summary>
    public HealthStatus Overall
    {
        get
        {
            lock (_gate)
                return _current.Count == 0 ? HealthStatus.Unknown : _current.Values.Max(r => r.Status);
        }
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken = default)
    {
        foreach (HealthCheck check in _checks)
        {
            HealthResult result = await ProbeAsync(check, cancellationToken).ConfigureAwait(false);
            HealthResult? previous;

            lock (_gate)
            {
                _current.TryGetValue(check.Name, out previous);
                _current[check.Name] = result;
            }

            if (previous?.Status == result.Status)
                continue;

            // First successful reading is not news; everything else is.
            if (previous is null && result.Status == HealthStatus.Ok)
                continue;

            _bus.Publish(EdithEvent.Create(
                EventKinds.HealthChanged,
                $"{check.Title}: {Describe(result.Status)}" + (result.Detail.Length > 0 ? $" ({result.Detail})" : ""),
                data: new Dictionary<string, string>
                {
                    ["check"] = check.Name,
                    ["status"] = result.Status.ToString(),
                    ["previous"] = previous?.Status.ToString() ?? HealthStatus.Unknown.ToString(),
                    ["detail"] = result.Detail,
                }));
        }
    }

    public static string Describe(HealthStatus status) => status switch
    {
        HealthStatus.Ok => "в порядке",
        HealthStatus.Degraded => "работает с ограничениями",
        HealthStatus.Failed => "не работает",
        _ => "неизвестно",
    };

    private async Task<HealthResult> ProbeAsync(HealthCheck check, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            return await check.Probe(timeout.Token).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthResult.Failed("timeout");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthResult.Failed(ex.Message);
        }
    }
}
