using System.Diagnostics;

namespace Edith.Core;

/// <summary>
/// Per-interaction timeline (wake → listening → speech end → STT → brain → first sound).
/// Marks are kept in memory only; <see cref="Stages"/> is read once when the interaction ends,
/// so measuring never costs the voice path a disk write.
/// </summary>
public sealed class InteractionLatency
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(string Stage, double Ms, string? Detail)> _marks = [];

    public InteractionLatency(string? path = null)
    {
    }

    public double ElapsedMilliseconds => _clock.Elapsed.TotalMilliseconds;

    public void Mark(string stage, string? detail = null)
    {
        lock (_marks)
            _marks.Add((stage, _clock.Elapsed.TotalMilliseconds, detail));
    }

    /// <summary>First time each stage was reached, in ms since the session started.</summary>
    public IReadOnlyDictionary<string, string> Stages()
    {
        lock (_marks)
        {
            var result = new Dictionary<string, string>();
            foreach ((string stage, double ms, string? detail) in _marks)
            {
                if (!result.ContainsKey(stage))
                    result[stage] = ms.ToString("F0", System.Globalization.CultureInfo.InvariantCulture) + (detail is null ? "" : " | " + detail);
            }

            return result;
        }
    }
}
