using System.Globalization;
using Edith.Core.Runtime;

namespace Edith.Core.Memory;

/// <summary>
/// Background learning: every few minutes, while EDITH is idle and the brain is free, the
/// new user lines since the last run are sent to the brain for fact extraction. A watermark
/// in the memory database makes each line count once, across restarts.
/// </summary>
public sealed class MemoryDistiller(
    SqliteMemoryStore store,
    Func<string, string, CancellationToken, Task<string?>> completeJson,
    IEventBus? bus = null,
    Func<DateTimeOffset>? clock = null)
{
    public const string WatermarkKey = "distiller-watermark";

    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);
    private int _running;

    /// <summary>Returns the number of facts saved; 0 when there was nothing new or the brain declined.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
            return 0;

        try
        {
            DateTimeOffset now = _clock();
            DateTimeOffset since = Watermark() ?? now.AddDays(-1);
            IReadOnlyList<ConversationTurn> turns = store.Turns(since, now.AddSeconds(1), 200)
                .Where(t => t.Timestamp > since)
                .ToList();

            IReadOnlyList<string> lines = ConversationRecall.Topics(turns);
            if (turns.Count == 0)
                return 0;

            if (lines.Count == 0)
            {
                // Only commands and small talk: nothing to learn, but do not look at them again.
                SetWatermark(turns[^1].Timestamp);
                return 0;
            }

            string? json = await completeJson(FactExtractor.SystemPrompt, FactExtractor.BuildUserPrompt(lines), cancellationToken);
            if (json is null)
                return 0; // brain busy or off: the same lines are tried next time

            IReadOnlyList<ExtractedFact> facts = FactExtractor.Parse(json);
            int saved = FactExtractor.Store(store, facts);
            SetWatermark(turns[^1].Timestamp);

            if (saved > 0)
            {
                bus?.Publish(EdithEvent.Create(
                    "memory.learned",
                    string.Join("; ", facts.Take(saved).Select(f => $"{f.Key} — {f.Value}")),
                    data: new Dictionary<string, string> { ["count"] = saved.ToString(CultureInfo.InvariantCulture) }));
            }

            return saved;
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private DateTimeOffset? Watermark() =>
        store.GetMeta(WatermarkKey) is { } value &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset parsed)
            ? parsed
            : null;

    private void SetWatermark(DateTimeOffset value) =>
        store.SetMeta(WatermarkKey, value.ToString("O", CultureInfo.InvariantCulture));
}
