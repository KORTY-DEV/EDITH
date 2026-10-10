namespace Edith.Core.Brain;

/// <summary>
/// Decides whether the user is (probably) in a game. Pure logic: the process list and the
/// full-screen probe are injected, so it is cheap to test and never polls on its own.
/// </summary>
public static class GameProcessMatcher
{
    /// <summary>
    /// Names of at least 4 characters match by prefix (so "Tanki" matches "TankiOnline"),
    /// shorter names must match exactly so that "wot" cannot match "wotan-notes".
    /// </summary>
    public static string? FindMatch(
        IEnumerable<string> processNames,
        IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return null;
        }

        foreach (string raw in processNames)
        {
            string name = Normalize(raw);

            if (name.Length == 0)
            {
                continue;
            }

            foreach (string rawPattern in patterns)
            {
                string pattern = Normalize(rawPattern);

                if (pattern.Length == 0)
                {
                    continue;
                }

                bool match =
                    pattern.Length >= 4
                        ? name.StartsWith(pattern, StringComparison.Ordinal)
                        : name == pattern;

                if (match)
                {
                    return raw;
                }
            }
        }

        return null;
    }

    private static string Normalize(string name)
    {
        name = name.Trim().ToLowerInvariant();

        return name.EndsWith(".exe", StringComparison.Ordinal)
            ? name[..^4]
            : name;
    }
}

public sealed record GameModeState(
    bool Active,
    string Reason,
    DateTimeOffset CheckedAt);

/// <summary>
/// Lazy, time-boxed game-mode probe. <see cref="Current"/> re-evaluates at most once per
/// <c>pollInterval</c>; there is no timer or thread inside.
/// </summary>
public sealed class GameModeMonitor
{
    private readonly Func<IEnumerable<string>> _processNames;
    private readonly Func<bool> _fullscreenProbe;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private GameModeState? _state;
    private bool _forced;
    private bool _forcedValue;

    public GameModeMonitor(
        Func<IEnumerable<string>> processNames,
        Func<bool> fullscreenProbe,
        Func<DateTimeOffset>? clock = null)
    {
        _processNames = processNames;
        _fullscreenProbe = fullscreenProbe;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Test / manual override (null = automatic).</summary>
    public void Force(bool? active)
    {
        lock (_gate)
        {
            _forced = active.HasValue;
            _forcedValue = active.GetValueOrDefault();
            _state = null;
        }
    }

    public GameModeState Evaluate(GamingSettings settings)
    {
        DateTimeOffset now = _clock();

        lock (_gate)
        {
            if (_forced)
            {
                return new GameModeState(_forcedValue, "forced", now);
            }

            if (!settings.Enabled)
            {
                return _state = new GameModeState(false, "disabled", now);
            }

            if (_state is not null &&
                now - _state.CheckedAt < TimeSpan.FromSeconds(settings.PollSeconds))
            {
                return _state;
            }
        }

        // The probes run outside the lock: they touch the OS and must not block other callers.
        GameModeState fresh = Probe(settings, now);

        lock (_gate)
        {
            return _state = fresh;
        }
    }

    private GameModeState Probe(GamingSettings settings, DateTimeOffset now)
    {
        try
        {
            string? match = GameProcessMatcher.FindMatch(_processNames(), settings.Processes);

            if (match is not null)
            {
                return new GameModeState(true, "process:" + match, now);
            }

            if (settings.DetectFullscreen && _fullscreenProbe())
            {
                return new GameModeState(true, "fullscreen", now);
            }
        }
        catch
        {
            // A failing probe must never break the assistant; assume "no game".
        }

        return new GameModeState(false, "none", now);
    }
}
