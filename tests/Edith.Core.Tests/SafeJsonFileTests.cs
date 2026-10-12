using Edith.Core;
using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class SafeJsonFileTests
{
    [Fact]
    public void BrokenFile_IsCopiedAside_NotSilentlyLost()
    {
        string dir = Path.Combine(Path.GetTempPath(), "edith-safejson-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "items.json");
        File.WriteAllText(file, "[ {\"broken\": ");

        List<int> loaded = SafeJsonFile.Load<List<int>>(file, () => []);

        Assert.Empty(loaded);
        Assert.Single(Directory.GetFiles(dir, "items.json.unreadable-*"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void LockedFile_IsRetried_AndReadOnceFree()
    {
        string file = Path.Combine(Path.GetTempPath(), "edith-safejson-lock-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(file, "[1,2,3]");
        var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        Task.Run(async () => { await Task.Delay(100); stream.Dispose(); });

        Assert.Equal([1, 2, 3], SafeJsonFile.Load<List<int>>(file, () => []));
        File.Delete(file);
    }

    [Fact]
    public void MissingFile_IsEmpty_AndWritesNothing()
    {
        string file = Path.Combine(Path.GetTempPath(), "edith-safejson-none-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.Empty(SafeJsonFile.Load<List<int>>(file, () => []));
        Assert.False(File.Exists(file));
    }
}

public sealed class ShutdownCancelWordingTests
{
    [Theory]
    [InlineData("не выключай компьютер", true)]
    [InlineData("не выключай", true)]
    [InlineData("отмени выключение", true)]
    [InlineData("не выключай свет в коридоре когда уйдёшь", false)]
    [InlineData("я не выключай-то ему", false)]
    public void Cancel_OnlyWhenItIsAboutTheComputer(string phrase, bool cancels)
    {
        bool parsed = Edith.Core.Power.ShutdownCommandParser.TryParse(phrase, DateTimeOffset.Now, out var command);
        Assert.Equal(cancels, parsed && command.Kind == Edith.Core.Power.ShutdownCommandKind.Cancel);
    }
}
