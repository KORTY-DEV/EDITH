using Edith.Core;

namespace Edith.Core.Tests;

public class AssistantStateTests
{
    [Fact]
    public void Sleeping_IsDefaultStateValue()
    {
        Assert.Equal(0, (int)AssistantState.Sleeping);
    }

    [Fact]
    public void Listening_IsDifferentFromSleeping()
    {
        Assert.NotEqual(
            AssistantState.Sleeping,
            AssistantState.Listening);
    }
}
