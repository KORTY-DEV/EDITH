namespace Edith.Core.Runtime;

public interface IEventBus
{
    void Publish(EdithEvent evt);

    IDisposable Subscribe(Action<EdithEvent> handler);

    /// <summary>Most recent events, oldest first.</summary>
    IReadOnlyList<EdithEvent> Recent(int max = 100);
}

/// <summary>
/// In-process, synchronous event bus. Publishing never throws: a failing subscriber is
/// isolated so the audio thread that published the event keeps running. Subscribers that
/// do slow work (disk, UI) must hand it off themselves.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly object _gate = new();
    private readonly List<Action<EdithEvent>> _handlers = [];
    private readonly Queue<EdithEvent> _recent = new();
    private readonly int _capacity;

    public EventBus(int recentCapacity = 500)
    {
        _capacity = Math.Max(1, recentCapacity);
    }

    /// <summary>Raised when a subscriber throws; never raised recursively.</summary>
    public event Action<EdithEvent, Exception>? SubscriberFailed;

    public void Publish(EdithEvent evt)
    {
        Action<EdithEvent>[] handlers;

        lock (_gate)
        {
            _recent.Enqueue(evt);
            while (_recent.Count > _capacity)
                _recent.Dequeue();

            handlers = [.. _handlers];
        }

        foreach (Action<EdithEvent> handler in handlers)
        {
            try
            {
                handler(evt);
            }
            catch (Exception ex)
            {
                try
                {
                    SubscriberFailed?.Invoke(evt, ex);
                }
                catch
                {
                }
            }
        }
    }

    public IDisposable Subscribe(Action<EdithEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
            _handlers.Add(handler);

        return new Subscription(this, handler);
    }

    public IReadOnlyList<EdithEvent> Recent(int max = 100)
    {
        lock (_gate)
        {
            int skip = Math.Max(0, _recent.Count - Math.Max(0, max));
            return _recent.Skip(skip).ToList();
        }
    }

    private void Unsubscribe(Action<EdithEvent> handler)
    {
        lock (_gate)
            _handlers.Remove(handler);
    }

    private sealed class Subscription(EventBus owner, Action<EdithEvent> handler) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(handler);
        }
    }
}

/// <summary>
/// Process-wide access point for code that is itself a singleton
/// (BrainService.Shared, PermissionManager.Shared). Until the app installs a real bus,
/// events go to a small private bus that nobody observes.
/// </summary>
public static class EdithEvents
{
    private static IEventBus _bus = new EventBus(50);

    public static IEventBus Bus => Volatile.Read(ref _bus);

    public static void Install(IEventBus bus) =>
        Volatile.Write(ref _bus, bus ?? throw new ArgumentNullException(nameof(bus)));

    public static void Publish(
        string kind,
        string summary,
        long session = 0,
        IReadOnlyDictionary<string, string>? data = null) =>
        Bus.Publish(EdithEvent.Create(kind, summary, session, data));
}
