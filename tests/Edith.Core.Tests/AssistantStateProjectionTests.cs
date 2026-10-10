using Edith.Core;

namespace Edith.Core.Tests;

public sealed class AssistantStateProjectionTests
{
    [Theory]
    [InlineData("Sleeping")]
    [InlineData("WaitingWake")]
    [InlineData("unknown")]
    public void IdleBackendStatesHideOrb(string backendState)
    {
        AssistantState state = AssistantStateProjection.ForBackend(backendState);
        Assert.False(AssistantStateProjection.OrbVisible(state));
    }

    [Theory]
    [InlineData("Listening", AssistantState.Listening)]
    [InlineData("Thinking", AssistantState.Thinking)]
    [InlineData("Speaking", AssistantState.Speaking)]
    [InlineData("Warning", AssistantState.Warning)]
    public void ActiveBackendStatesShowOrb(string backendState, AssistantState expected)
    {
        AssistantState state = AssistantStateProjection.ForBackend(backendState);
        Assert.Equal(expected, state);
        Assert.True(AssistantStateProjection.OrbVisible(state));
    }
}
