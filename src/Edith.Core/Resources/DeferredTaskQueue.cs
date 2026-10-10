using System.Text.Json;
using Edith.Core.Runtime;

namespace Edith.Core.Resources;

public sealed record DeferredTask(
    Guid Id,
    string Request,
    string Reason,
    DateTimeOffset EnqueuedAt);

/// <summary>
/// Heavy requests that arrived while the GPU belonged to a game (or to someone else).
/// Persisted to JSON so "сделаю после боя" survives an EDITH restart.
/// </summary>
public sealed class DeferredTaskQueue
{
    private readonly string? _path;
    private readonly IEventBus? _bus;
    private readonly object _gate = new();
    private readonly List<DeferredTask> _items;

    public DeferredTaskQueue(string? path = null, IEventBus? bus = null)
    {
        _path = path;
        _bus = bus;
        _items = Load(path);
    }

    public IReadOnlyList<DeferredTask> Pending
    {
        get
        {
            lock (_gate)
                return _items.ToList();
        }
    }

    public DeferredTask Enqueue(string request, string reason, DateTimeOffset? now = null)
    {
        var task = new DeferredTask(Guid.NewGuid(), request.Trim(), reason, now ?? DateTimeOffset.Now);

        lock (_gate)
        {
            _items.Add(task);
            Save();
        }

        _bus?.Publish(EdithEvent.Create(
            EventKinds.TaskDeferred,
            task.Request,
            data: new Dictionary<string, string> { ["id"] = task.Id.ToString(), ["reason"] = reason }));

        return task;
    }

    /// <summary>Removes and returns every task once heavy work is allowed again.</summary>
    public IReadOnlyList<DeferredTask> TakeReady(ResourcePlan plan)
    {
        if (!plan.HeavyWorkAllowed)
            return [];

        List<DeferredTask> ready;
        lock (_gate)
        {
            ready = _items.ToList();
            if (ready.Count == 0)
                return [];

            _items.Clear();
            Save();
        }

        foreach (DeferredTask task in ready)
        {
            _bus?.Publish(EdithEvent.Create(
                EventKinds.TaskResumed,
                task.Request,
                data: new Dictionary<string, string> { ["id"] = task.Id.ToString() }));
        }

        return ready;
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            int removed = _items.RemoveAll(task => task.Id == id);
            if (removed > 0)
                Save();
            return removed > 0;
        }
    }

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

    private static List<DeferredTask> Load(string? path)
    {
        try
        {
            return path is not null && File.Exists(path)
                ? JsonSerializer.Deserialize<List<DeferredTask>>(File.ReadAllText(path)) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
