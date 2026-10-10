namespace Edith.Core.Brain;

/// <summary>
/// Keeps an interactive request useful when a non-EDITH model owns the GPU.
/// The policy itself is pure so the runtime branch can be regression-tested
/// without an Ollama server.
/// </summary>
public enum BrainAvailabilityAction
{
    UsePrimary,
    UseCpuFallback,
    Unavailable
}

public static class BrainAvailabilityPolicy
{
    public const string UnavailableReply =
        "Не смогла получить ответ сейчас.";

    public static BrainAvailabilityAction Decide(
        bool primaryIsBlocked,
        bool cpuFallbackIsAvailable) =>
        !primaryIsBlocked
            ? BrainAvailabilityAction.UsePrimary
            : cpuFallbackIsAvailable
                ? BrainAvailabilityAction.UseCpuFallback
                : BrainAvailabilityAction.Unavailable;
}
