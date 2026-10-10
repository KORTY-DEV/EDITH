using Edith.Core;

namespace Edith.Core.Tests;

public sealed class BargeScanWindowTests
{
    [Fact]
    public void OwnPhrase_JustAfterTheCommand_IsNotHeard()
    {
        long end = 500_000;
        long floor = BargeScanWindow.FloorAfterCommand(end);
        Assert.Equal(0, BargeScanWindow.Available(end, floor));
        Assert.Equal(0, BargeScanWindow.Available(end + 16000 * 2, floor));
        Assert.Equal(0, BargeScanWindow.Available(end + 16000 * 2 + 7999, floor));
    }

    [Fact]
    public void Sound_AfterTheSettlePeriod_IsHeard_UpToThreeSeconds()
    {
        long floor = BargeScanWindow.FloorAfterCommand(0);
        Assert.Equal(8000, BargeScanWindow.Available(floor + 8000, floor));
        Assert.Equal(16000 * 3, BargeScanWindow.Available(floor + 16000 * 10, floor));
    }
}
