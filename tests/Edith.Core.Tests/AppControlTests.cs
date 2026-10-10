using Edith.Core.Apps;
using Edith.Core.Runtime;

namespace Edith.Core.Tests;

public sealed class AppCommandParserTests
{
    [Theory]
    [InlineData("закрой танки и дискорд", AppAction.Close, new[] { "танки", "дискорд" })]
    [InlineData("Эдит, закрой танки, дискорд и стим.", AppAction.Close, new[] { "танки", "дискорд", "стим" })]
    [InlineData("открой браузер и телеграм", AppAction.Open, new[] { "браузер", "телеграм" })]
    [InlineData("открой мне телеграм", AppAction.Open, new[] { "телеграм" })]
    [InlineData("запусти быстро дискорд", AppAction.Open, new[] { "дискорд" })]
    [InlineData("Эдит, открой мне пожалуйста яндекс музыку", AppAction.Open, new[] { "яндекс музыку" })]
    [InlineData("запусти игру мир танков", AppAction.Open, new[] { "мир танков" })]
    [InlineData("переключись на телеграм", AppAction.Focus, new[] { "телеграм" })]
    [InlineData("перейди в дискорд пожалуйста", AppAction.Focus, new[] { "дискорд" })]
    [InlineData("сверни оперу", AppAction.Minimize, new[] { "оперу" })]
    [InlineData("включи музыку", AppAction.Open, new[] { "музыку" })]
    public void Parses(string phrase, AppAction action, string[] targets)
    {
        Assert.True(AppCommandParser.TryParse(phrase, out AppCommand command));
        Assert.Equal(action, command.Action);
        Assert.Equal(targets, command.Targets);
    }

    [Theory]
    [InlineData("открой папку загрузки")]
    [InlineData("открой сайт хабр")]
    [InlineData("закрой все")]
    [InlineData("что такое дискорд")]
    [InlineData("открой")]
    public void Ignores(string phrase)
    {
        Assert.False(AppCommandParser.TryParse(phrase, out _));
    }
}

public sealed class AppCatalogTests
{
    [Theory]
    [InlineData("танки", "tanks")]
    [InlineData("Мир танков", "tanks")]
    [InlineData("телегу", "telegram")]
    [InlineData("оперу", "browser")]
    [InlineData("яндекс музыку", "music")]
    [InlineData("дискорда", "discord")]
    [InlineData("дискомфорт", "discord")]
    [InlineData("телеграмма", "telegram")]
    [InlineData("стима", "steam")]
    public void Resolve_FindsBySpokenForm(string spoken, string id)
    {
        Assert.Equal(id, AppCatalog.Resolve(AppCatalog.Defaults, spoken)?.Id);
    }

    [Fact]
    public void Resolve_UnknownIsNull()
    {
        Assert.Null(AppCatalog.Resolve(AppCatalog.Defaults, "компьютер"));
        Assert.Null(AppCatalog.Resolve(AppCatalog.Defaults, "свет"));
        Assert.Null(AppCatalog.Resolve(AppCatalog.Defaults, "звук"));
        Assert.Null(AppCatalog.Resolve(AppCatalog.Defaults, "телевизор"));
    }

    [Fact]
    public void Load_MergesCustomEntries()
    {
        string path = Path.Combine(Path.GetTempPath(), $"edith-apps-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """
            [
              // своё приложение и переопределение браузера
              { "id": "factorio", "aliases": ["факторио"], "processes": ["factorio"], "shortcuts": ["factorio — ярлык"] },
              { "id": "browser", "title": "Chrome", "aliases": ["браузер"], "processes": ["chrome"] },
            ]
            """);
        try
        {
            IReadOnlyList<AppEntry> catalog = AppCatalog.Load(path);

            Assert.Equal("factorio", AppCatalog.Resolve(catalog, "факторио")?.Title);
            Assert.Equal("Chrome", AppCatalog.Resolve(catalog, "браузер")?.Title);
            Assert.NotNull(AppCatalog.Resolve(catalog, "дискорд"));
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public sealed class ShortcutIndexTests
{
    [Fact]
    public void Find_PrefersExact_SkipsUninstallers()
    {
        var index = new ShortcutIndex(
        [
            new Shortcut("Удалить Мир танков", @"C:\x\uninstall.lnk"),
            new Shortcut("Мир танков", @"C:\x\tanks.lnk"),
            new Shortcut("Браузер Opera GX", @"C:\x\opera.lnk"),
            new Shortcut("factorio — ярлык", @"C:\x\factorio.lnk"),
        ]);

        Assert.Equal(@"C:\x\tanks.lnk", index.Find(["мир танков"])?.Path);
        Assert.Equal(@"C:\x\opera.lnk", index.Find(["opera gx"])?.Path);
        Assert.Equal(@"C:\x\factorio.lnk", index.Find(["factorio"])?.Path);
        Assert.Null(index.Find(["удалить"]));
        Assert.Null(index.Find(["gta"]));
    }

    [Fact]
    public void Scan_ReadsLnkAndUrlRecursively()
    {
        string dir = Path.Combine(Path.GetTempPath(), "edith-shortcuts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Steam"));
        File.WriteAllText(Path.Combine(dir, "Discord.lnk"), "");
        File.WriteAllText(Path.Combine(dir, "Steam", "Palworld.url"), "");
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "");
        try
        {
            ShortcutIndex index = ShortcutIndex.Scan([dir, Path.Combine(dir, "missing")]);

            Assert.Equal(["Discord", "Palworld"], index.All.Select(s => s.Name).Order());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public sealed class AppActionsTests
{
    private sealed class FakeControl : IAppControl
    {
        public List<RunningApp> Processes { get; } = [];
        public List<int> Closed { get; } = [];
        public List<string> Launched { get; } = [];
        public int? Focused;

        public IReadOnlyList<RunningApp> Running() => Processes;
        public bool RequestClose(int processId) { Closed.Add(processId); return true; }
        public bool Focus(int processId) { Focused = processId; return true; }
        public bool Minimize(int processId) => true;
        public void Launch(string target) => Launched.Add(target);
    }

    private static readonly ShortcutIndex Shortcuts = new(
    [
        new Shortcut("Discord", @"C:\s\Discord.lnk"),
        new Shortcut("Telegram", @"C:\s\Telegram.lnk"),
        new Shortcut("Мир танков", @"C:\s\tanks.lnk"),
        new Shortcut("Palworld", @"C:\s\Palworld.url"),
    ]);

    private static (AppActions Actions, FakeControl Control, EventBus Bus) Create()
    {
        var control = new FakeControl();
        var bus = new EventBus();
        return (new AppActions(AppCatalog.Defaults, () => Shortcuts, control, bus), control, bus);
    }

    private static AppPlan Plan(AppActions actions, string phrase)
    {
        Assert.True(AppCommandParser.TryParse(phrase, out AppCommand command));
        return actions.Plan(command) ?? throw new Xunit.Sdk.XunitException("no plan");
    }

    [Fact]
    public void Close_ClosesOnlyWindowedProcesses_AndReportsNotRunning()
    {
        (AppActions actions, FakeControl control, EventBus bus) = Create();
        control.Processes.AddRange(
        [
            new RunningApp(10, "Discord", true),
            new RunningApp(11, "Discord", false),
            new RunningApp(20, "WorldOfTanks", true),
        ]);

        AppPlan plan = Plan(actions, "закрой танки, дискорд и телеграм");

        Assert.True(plan.NeedsConfirmation);
        Assert.Equal("Закрываю Мир танков и Discord. Telegram и так не запущен.", actions.Execute(plan));
        Assert.Equal([20, 10], control.Closed);
        Assert.Equal(2, bus.Recent().Count(e => e.Kind == EventKinds.ToolFinished && e.Get("tool") == "close_app"));
    }

    [Fact]
    public void Close_NeverTouchesProtectedProcesses()
    {
        (AppActions actions, FakeControl control, _) = Create();
        control.Processes.Add(new RunningApp(1, "explorer", true));

        Assert.Equal("Не стала закрывать Проводник.", actions.Execute(Plan(actions, "закрой проводник")));
        Assert.Empty(control.Closed);
    }

    [Fact]
    public void Open_UsesShortcuts_AndInstalledShortcutsWithoutCatalogEntry()
    {
        (AppActions actions, FakeControl control, _) = Create();

        AppPlan plan = Plan(actions, "открой дискорд и palworld");

        Assert.False(plan.NeedsConfirmation);
        Assert.Equal("Открываю Discord и Palworld.", actions.Execute(plan));
        Assert.Equal([@"C:\s\Discord.lnk", @"C:\s\Palworld.url"], control.Launched);
    }

    [Fact]
    public void Open_ReportsUnknownTargetsNextToKnownOnes()
    {
        (AppActions actions, _, _) = Create();

        Assert.Equal(
            "Открываю Telegram. Не знаю, что такое «абракадабра».",
            actions.Execute(Plan(actions, "открой телеграм и абракадабра")));
    }

    [Fact]
    public void Plan_IsNullWhenNothingIsAnApp()
    {
        (AppActions actions, _, _) = Create();
        Assert.True(AppCommandParser.TryParse("выключи компьютер", out AppCommand command));

        Assert.Null(actions.Plan(command));
    }

    [Fact]
    public void Focus_SwitchesOrStartsTheApp()
    {
        (AppActions actions, FakeControl control, _) = Create();

        Assert.Equal("Telegram не был запущен, открываю.", actions.Execute(Plan(actions, "переключись на телеграм")));

        control.Processes.Add(new RunningApp(33, "Telegram", true));
        Assert.Equal("Переключаюсь на Telegram.", actions.Execute(Plan(actions, "переключись на телеграм")));
        Assert.Equal(33, control.Focused);
    }
}

public sealed class ShippedAppsExampleTests
{
    [Fact]
    public void ExampleConfig_LoadsWithoutWarnings()
    {
        var warnings = new List<string>();

        IReadOnlyList<AppEntry> catalog = AppCatalog.Load(@"C:\EDITH\config\apps.example.json", warnings);

        Assert.Empty(warnings);
        Assert.True(AppCatalog.Resolve(catalog, "факторио")?.ConfirmClose);
    }
}

public sealed class ShortcutTranslitTests
{
    [Theory]
    [InlineData("лэм студио", "LM Studio")]
    [InlineData("докер", "Docker Desktop")]
    [InlineData("термиус", "Termius")]
    public void RussianPronunciation_FindsLatinShortcut(string spoken, string expected)
    {
        var index = new ShortcutIndex(
        [
            new Shortcut("LM Studio", "a"), new Shortcut("Docker Desktop", "b"), new Shortcut("Termius", "c"), new Shortcut("Discord", "d"),
        ]);

        Assert.Equal(expected, index.Find([spoken])?.Name);
    }

    [Fact]
    public void UnrelatedWords_DoNotMatch()
    {
        var index = new ShortcutIndex([new Shortcut("LM Studio", "a"), new Shortcut("Steam", "b")]);

        Assert.Null(index.Find(["калькулятор погоды"]));
    }
}
