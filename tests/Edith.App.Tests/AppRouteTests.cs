using Edith.App;
using Edith.Core.Apps;
using Edith.Core.Persona;

namespace Edith.App.Tests;

public sealed class AppRouteTests
{
    private sealed class FakeControl : IAppControl
    {
        public List<int> Closed { get; } = [];
        public IReadOnlyList<RunningApp> Running() =>
            [new RunningApp(5, "WorldOfTanks", true), new RunningApp(6, "Discord", true)];
        public bool RequestClose(int processId) { Closed.Add(processId); return true; }
        public bool Focus(int processId) => true;
        public bool Minimize(int processId) => true;
        public void Launch(string target) { }
    }

    [Fact]
    public async Task ClosingAGame_WaitsForConfirmation_OtherAppsCloseAtOnce()
    {
        var control = new FakeControl();
        var apps = new AppActions(AppCatalog.Defaults, () => new ShortcutIndex([]), control);
        Func<Task<string>>? pending = null;
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile(),
            requestPermission: (summary, _, execute) => { pending = execute; return "Нужно подтверждение: " + summary + "."; },
            apps: apps);

        Assert.Equal("Закрываю Discord.", await engine.HandleAsync("закрой дискорд"));
        Assert.Equal([6], control.Closed);

        Assert.Equal("Нужно подтверждение: закрыть Мир танков и Discord.", await engine.HandleAsync("закрой танки и дискорд"));
        Assert.Equal("AppControl", engine.LastRoute);
        Assert.Equal([6], control.Closed);

        Assert.Equal("Закрываю Мир танков и Discord.", await pending!());
        Assert.Equal([6, 5, 6], control.Closed);
    }

    [Fact]
    public async Task NonAppPhrase_FallsThroughToOldRoutes()
    {
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile(),
            apps: new AppActions(AppCatalog.Defaults, () => new ShortcutIndex([]), new FakeControl()));

        await engine.HandleAsync("как тебя зовут");

        Assert.Equal("IdentityFastPath", engine.LastRoute);
    }
}
