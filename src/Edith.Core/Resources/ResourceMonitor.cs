using Edith.Core.Runtime;

namespace Edith.Core.Resources;

/// <summary>
/// Combines the game probe and the GPU probe into a <see cref="ResourcePlan"/> and publishes
/// <see cref="EventKinds.ResourcesChanged"/> only when the decision changes. Has no timer of
/// its own: the host calls <see cref="Poll"/> on its schedule, tests call it directly.
/// </summary>
public sealed class ResourceMonitor
{
    private readonly Func<bool> _gameActive;
    private readonly Func<GpuSnapshot?> _gpu;
    private readonly Func<int> _edithVramMb;
    private readonly IEventBus _bus;
    private readonly object _gate = new();
    private ResourcePlan? _current;

    public ResourceMonitor(
        Func<bool> gameActive,
        Func<GpuSnapshot?> gpu,
        IEventBus bus,
        ResourceSettings? settings = null,
        Func<int>? edithVramMb = null)
    {
        _gameActive = gameActive;
        _gpu = gpu;
        _bus = bus;
        _edithVramMb = edithVramMb ?? (() => 0);
        Settings = settings ?? new ResourceSettings();
    }

    public ResourceSettings Settings { get; }

    public ResourcePlan? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public event Action<ResourcePlan>? Changed;

    public ResourcePlan Poll()
    {
        bool game = SafeGet(_gameActive, false);
        GpuSnapshot? gpu = SafeGet(_gpu, null);
        int own = SafeGet(_edithVramMb, 0);

        ResourcePlan plan = ResourcePolicy.Decide(game, gpu, Settings, own);
        bool changed;

        lock (_gate)
        {
            changed = !plan.SameDecisionAs(_current);
            _current = plan;
        }

        if (changed)
        {
            _bus.Publish(EdithEvent.Create(
                EventKinds.ResourcesChanged,
                Describe(plan),
                data: new Dictionary<string, string>
                {
                    ["brain"] = plan.Brain.ToString(),
                    ["heavy"] = plan.HeavyWorkAllowed.ToString(),
                    ["game"] = plan.GameActive.ToString(),
                    ["reason"] = plan.Reason,
                    ["vram-used-mb"] = plan.Gpu?.MemoryUsedMb.ToString() ?? "",
                    ["vram-total-mb"] = plan.Gpu?.MemoryTotalMb.ToString() ?? "",
                    ["gpu-util"] = plan.Gpu?.UtilizationPercent.ToString() ?? "",
                }));

            try
            {
                Changed?.Invoke(plan);
            }
            catch
            {
            }
        }

        return plan;
    }

    public static string Describe(ResourcePlan plan) => plan.Reason switch
    {
        "gpu-free" => "Видеокарта свободна",
        "gpu-shared" => "Видеокарта частично занята, тяжёлые задачи отложены",
        "gpu-busy-foreign" => "Видеокарта занята другой программой, мозг на процессоре",
        "game-gpu-headroom" => "Игра, мозг на видеокарте с запасом",
        "game-cpu-only" => "Игра, мозг на процессоре",
        "game-gpu-unknown" => "Игра, состояние видеокарты неизвестно, мозг на процессоре",
        "gpu-unknown" => "Состояние видеокарты неизвестно",
        _ => plan.Reason,
    };

    private static T SafeGet<T>(Func<T> probe, T fallback)
    {
        try
        {
            return probe();
        }
        catch
        {
            return fallback;
        }
    }
}
