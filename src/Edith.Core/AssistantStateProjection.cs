namespace Edith.Core;

public static class AssistantStateProjection
{
    public static bool OrbVisible(AssistantState state) =>
        state != AssistantState.Sleeping;

    public static AssistantState ForBackend(string backendState) =>
        backendState.Trim().ToLowerInvariant() switch
        {
            "sleeping" or "waitingwake" => AssistantState.Sleeping,
            "listening" => AssistantState.Listening,
            "thinking" or "busy" => AssistantState.Thinking,
            "speaking" => AssistantState.Speaking,
            "warning" => AssistantState.Warning,
            _ => AssistantState.Sleeping
        };
}
