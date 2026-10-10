namespace Edith.Core.Resources;

public enum BrainPlacement
{
    Gpu,
    Cpu
}

/// <summary>
/// What EDITH may use right now. <see cref="HeavyWorkAllowed"/> gates multi-step agent tasks
/// and vision models; when false such tasks go to the deferred queue ("сделаю после боя").
/// </summary>
public sealed record ResourcePlan(
    BrainPlacement Brain,
    bool HeavyWorkAllowed,
    bool GameActive,
    string Reason,
    GpuSnapshot? Gpu)
{
    public bool SameDecisionAs(ResourcePlan? other) =>
        other is not null &&
        other.Brain == Brain &&
        other.HeavyWorkAllowed == HeavyWorkAllowed &&
        other.GameActive == GameActive &&
        other.Reason == Reason;
}

public sealed record ResourceSettings
{
    /// <summary>VRAM the conversational (router) model needs, incl. KV cache.</summary>
    public int RouterVramMb { get; init; } = 5600;

    /// <summary>VRAM a heavy worker / vision model needs.</summary>
    public int HeavyVramMb { get; init; } = 6500;

    /// <summary>
    /// Keep the router on the GPU during a game when there is headroom. Off by default until
    /// the FPS impact is benchmarked; the game then keeps the GPU to itself.
    /// </summary>
    public bool AllowGpuDuringGame { get; init; }

    /// <summary>Extra VRAM left untouched for the game when <see cref="AllowGpuDuringGame"/>.</summary>
    public int GameVramMarginMb { get; init; } = 1024;

    /// <summary>Above this GPU load the router leaves the GPU during a game.</summary>
    public int MaxGpuUtilizationDuringGame { get; init; } = 85;
}

/// <summary>
/// Pure decision: where the brain runs and whether heavy work may start. VRAM already held by
/// EDITH's own model counts as available to EDITH, otherwise a loaded model would evict itself.
/// </summary>
public static class ResourcePolicy
{
    public static ResourcePlan Decide(
        bool gameActive,
        GpuSnapshot? gpu,
        ResourceSettings settings,
        int edithVramMb = 0)
    {
        if (gpu is null)
        {
            return gameActive
                ? new(BrainPlacement.Cpu, false, true, "game-gpu-unknown", null)
                : new(BrainPlacement.Gpu, true, false, "gpu-unknown", null);
        }

        int available = Math.Max(0, gpu.MemoryTotalMb - Math.Max(0, gpu.MemoryUsedMb - Math.Max(0, edithVramMb)));

        if (gameActive)
        {
            bool headroom =
                settings.AllowGpuDuringGame &&
                available >= settings.RouterVramMb + settings.GameVramMarginMb &&
                gpu.UtilizationPercent <= settings.MaxGpuUtilizationDuringGame;

            return headroom
                ? new(BrainPlacement.Gpu, false, true, "game-gpu-headroom", gpu)
                : new(BrainPlacement.Cpu, false, true, "game-cpu-only", gpu);
        }

        if (available >= settings.HeavyVramMb)
            return new(BrainPlacement.Gpu, true, false, "gpu-free", gpu);

        return available >= settings.RouterVramMb
            ? new(BrainPlacement.Gpu, false, false, "gpu-shared", gpu)
            : new(BrainPlacement.Cpu, false, false, "gpu-busy-foreign", gpu);
    }
}
