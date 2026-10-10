using Edith.Core.Resources;
using Edith.Core.Runtime;

namespace Edith.Core.Tests;

public sealed class EventBusTests
{
    [Fact]
    public void Publish_DeliversToAllSubscribers_AndIsolatesFailures()
    {
        var bus = new EventBus();
        var seen = new List<string>();
        Exception? reported = null;
        bus.SubscriberFailed += (_, ex) => reported = ex;

        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(evt => seen.Add(evt.Kind));

        bus.Publish(EdithEvent.Create("a", "first"));

        Assert.Equal(["a"], seen);
        Assert.IsType<InvalidOperationException>(reported);
    }

    [Fact]
    public void DisposedSubscription_StopsReceiving()
    {
        var bus = new EventBus();
        int count = 0;
        IDisposable subscription = bus.Subscribe(_ => count++);

        bus.Publish(EdithEvent.Create("a", ""));
        subscription.Dispose();
        subscription.Dispose();
        bus.Publish(EdithEvent.Create("b", ""));

        Assert.Equal(1, count);
    }

    [Fact]
    public void Recent_KeepsOnlyCapacity_OldestFirst()
    {
        var bus = new EventBus(recentCapacity: 3);
        foreach (string kind in new[] { "1", "2", "3", "4", "5" })
            bus.Publish(EdithEvent.Create(kind, ""));

        Assert.Equal(["3", "4", "5"], bus.Recent().Select(e => e.Kind));
        Assert.Equal(["4", "5"], bus.Recent(2).Select(e => e.Kind));
    }
}

public sealed class ActionJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edith-journal-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task AttachedJournal_PersistsEvents_AcrossReopen()
    {
        var bus = new EventBus();
        await using (var journal = new ActionJournal(_path))
        {
            journal.Attach(bus);
            bus.Publish(EdithEvent.Create(EventKinds.ToolFinished, "открыла блокнот", 7,
                new Dictionary<string, string> { ["tool"] = "open_app" }));
            bus.Publish(EdithEvent.Create(EventKinds.CommandHeard, "открой блокнот", 7));
            await journal.FlushAsync();
            Assert.Equal(2, journal.WrittenCount);
        }

        await using var reopened = new ActionJournal(_path);
        IReadOnlyList<EdithEvent> all = reopened.Recent();

        Assert.Equal([EventKinds.ToolFinished, EventKinds.CommandHeard], all.Select(e => e.Kind));
        Assert.Equal("open_app", all[0].Get("tool"));
        Assert.Equal(7, all[0].Session);
    }

    [Fact]
    public async Task Queries_FilterByKindPrefixSessionAndTime()
    {
        await using var journal = new ActionJournal(_path);
        DateTimeOffset t0 = DateTimeOffset.Parse("2026-09-23T10:00:00+03:00");

        journal.Append(EdithEvent.Create("tool.finished", "a", 1, timestamp: t0));
        journal.Append(EdithEvent.Create("tool_x.other", "underscore must not match LIKE", 1, timestamp: t0));
        journal.Append(EdithEvent.Create("voice.command", "b", 2, timestamp: t0.AddMinutes(5)));
        journal.Append(EdithEvent.Create("tool.failed", "c", 2, timestamp: t0.AddMinutes(10)));
        await journal.FlushAsync();

        Assert.Equal(["a", "c"], journal.Recent(10, "tool.").Select(e => e.Summary));
        Assert.Equal(["b", "c"], journal.ForSession(2).Select(e => e.Summary));
        Assert.Equal(["b", "c"], journal.Since(t0.AddMinutes(1)).Select(e => e.Summary));
        Assert.Equal(["c"], journal.Recent(1).Select(e => e.Summary));
    }

    [Fact]
    public async Task LongValues_AreTruncated()
    {
        await using var journal = new ActionJournal(_path);
        string huge = new('x', ActionJournal.MaxFieldLength * 2);

        journal.Append(EdithEvent.Create("tool.finished", huge,
            data: new Dictionary<string, string> { ["result"] = huge }));
        await journal.FlushAsync();

        EdithEvent stored = Assert.Single(journal.Recent());
        Assert.True(stored.Summary.Length <= ActionJournal.MaxFieldLength + 1);
        Assert.True(stored.Get("result")!.Length <= ActionJournal.MaxFieldLength + 1);
    }

    public void Dispose()
    {
        foreach (string file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try { File.Delete(file); } catch { }
        }
    }
}

public sealed class NvidiaSmiParserTests
{
    [Theory]
    [InlineData("NVIDIA GeForce GTX 1070, 37, 5120, 8192", 37, 5120, 8192)]
    [InlineData("name, utilization.gpu [%], memory.used [MiB], memory.total [MiB]\nNVIDIA GeForce GTX 1070, 45 %, 3120 MiB, 8192 MiB", 45, 3120, 8192)]
    [InlineData("\r\nGPU, with, commas, 0, 100, 8192\r\n", 0, 100, 8192)]
    public void Parse_ReadsCsvWithOrWithoutUnits(string output, int util, int used, int total)
    {
        GpuSnapshot? gpu = NvidiaSmiParser.Parse(output);

        Assert.NotNull(gpu);
        Assert.Equal(util, gpu.UtilizationPercent);
        Assert.Equal(used, gpu.MemoryUsedMb);
        Assert.Equal(total, gpu.MemoryTotalMb);
        Assert.Equal(total - used, gpu.MemoryFreeMb);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("NVIDIA-SMI has failed because it couldn't communicate with the NVIDIA driver.")]
    [InlineData("name, utilization.gpu [%], memory.used [MiB], memory.total [MiB]")]
    public void Parse_ReturnsNullForGarbage(string? output)
    {
        Assert.Null(NvidiaSmiParser.Parse(output));
    }
}

public sealed class ResourcePolicyTests
{
    private static readonly ResourceSettings Defaults = new() { RouterVramMb = 3500, HeavyVramMb = 6000 };

    private static GpuSnapshot Gpu(int usedMb, int util = 10) => new("GTX 1070", util, usedMb, 8192);

    [Fact]
    public void NoGame_FreeGpu_AllowsHeavyWork()
    {
        ResourcePlan plan = ResourcePolicy.Decide(false, Gpu(500), Defaults);

        Assert.Equal(BrainPlacement.Gpu, plan.Brain);
        Assert.True(plan.HeavyWorkAllowed);
        Assert.Equal("gpu-free", plan.Reason);
    }

    [Fact]
    public void NoGame_ForeignProcessHoldsVram_MovesBrainToCpu()
    {
        // Another project's runs hold 6 GB: only ~2 GB left, not enough for the router.
        ResourcePlan plan = ResourcePolicy.Decide(false, Gpu(6200, util: 99), Defaults);

        Assert.Equal(BrainPlacement.Cpu, plan.Brain);
        Assert.False(plan.HeavyWorkAllowed);
        Assert.Equal("gpu-busy-foreign", plan.Reason);
    }

    [Fact]
    public void RealSample_OtherProjectTraining_KeepsEdithOffTheGpu()
    {
        // Captured 2026-09-23 while another project's runs held the GTX 1070.
        GpuSnapshot gpu = NvidiaSmiParser.Parse("NVIDIA GeForce GTX 1070, 35, 7454, 8192")!;

        ResourcePlan plan = ResourcePolicy.Decide(false, gpu, Defaults);

        Assert.Equal(BrainPlacement.Cpu, plan.Brain);
        Assert.Equal("gpu-busy-foreign", plan.Reason);
    }

    [Fact]
    public void NoGame_PartlyUsedGpu_KeepsRouterButDefersHeavyWork()
    {
        ResourcePlan plan = ResourcePolicy.Decide(false, Gpu(3000), Defaults);

        Assert.Equal(BrainPlacement.Gpu, plan.Brain);
        Assert.False(plan.HeavyWorkAllowed);
        Assert.Equal("gpu-shared", plan.Reason);
    }

    [Fact]
    public void EdithsOwnModelVram_CountsAsAvailable()
    {
        // 4.5 GB used, of which 3.4 GB is EDITH's own resident model.
        ResourcePlan plan = ResourcePolicy.Decide(false, Gpu(4500), Defaults, edithVramMb: 3400);

        Assert.Equal("gpu-free", plan.Reason);
    }

    [Fact]
    public void Game_DefaultsToCpuOnly_EvenWithHeadroom()
    {
        ResourcePlan plan = ResourcePolicy.Decide(true, Gpu(2500, util: 40), Defaults);

        Assert.Equal(BrainPlacement.Cpu, plan.Brain);
        Assert.False(plan.HeavyWorkAllowed);
        Assert.True(plan.GameActive);
    }

    [Theory]
    [InlineData(2500, 40, BrainPlacement.Gpu)]
    [InlineData(2500, 95, BrainPlacement.Cpu)]
    [InlineData(4500, 40, BrainPlacement.Cpu)]
    public void Game_WithGpuAllowed_NeedsVramMarginAndLowLoad(int usedMb, int util, BrainPlacement expected)
    {
        var settings = new ResourceSettings { AllowGpuDuringGame = true, RouterVramMb = 3500, HeavyVramMb = 6000 };

        ResourcePlan plan = ResourcePolicy.Decide(true, Gpu(usedMb, util), settings);

        Assert.Equal(expected, plan.Brain);
        Assert.False(plan.HeavyWorkAllowed);
    }

    [Fact]
    public void UnknownGpu_FallsBackByGameState()
    {
        Assert.Equal(BrainPlacement.Gpu, ResourcePolicy.Decide(false, null, Defaults).Brain);
        Assert.Equal(BrainPlacement.Cpu, ResourcePolicy.Decide(true, null, Defaults).Brain);
    }
}

public sealed class ResourceMonitorTests
{
    [Fact]
    public void Poll_PublishesOnlyOnDecisionChange_AndSurvivesProbeFailures()
    {
        var bus = new EventBus();
        bool game = false;
        GpuSnapshot? gpu = new("GTX 1070", 5, 500, 8192);
        bool gpuThrows = false;
        var monitor = new ResourceMonitor(
            () => game,
            () => gpuThrows ? throw new InvalidOperationException("nvidia-smi missing") : gpu,
            bus);

        monitor.Poll();
        monitor.Poll();
        game = true;
        monitor.Poll();
        gpuThrows = true;
        ResourcePlan unknown = monitor.Poll();

        List<EdithEvent> events = bus.Recent().Where(e => e.Kind == EventKinds.ResourcesChanged).ToList();
        Assert.Equal(["gpu-free", "game-cpu-only", "game-gpu-unknown"], events.Select(e => e.Get("reason")));
        Assert.Equal("game-gpu-unknown", unknown.Reason);
        Assert.Same(unknown, monitor.Current);
    }
}

public sealed class DeferredTaskQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edith-deferred-{Guid.NewGuid():N}.json");

    private static ResourcePlan Plan(bool heavy) =>
        new(BrainPlacement.Gpu, heavy, !heavy, heavy ? "gpu-free" : "game-cpu-only", null);

    [Fact]
    public void Tasks_WaitForHeavyWork_AndSurviveRestart()
    {
        var bus = new EventBus();
        var queue = new DeferredTaskQueue(_path, bus);
        queue.Enqueue("разбери загрузки", "game-cpu-only");

        Assert.Empty(queue.TakeReady(Plan(heavy: false)));

        var restarted = new DeferredTaskQueue(_path, bus);
        DeferredTask task = Assert.Single(restarted.TakeReady(Plan(heavy: true)));

        Assert.Equal("разбери загрузки", task.Request);
        Assert.Empty(new DeferredTaskQueue(_path).Pending);
        Assert.Equal([EventKinds.TaskDeferred, EventKinds.TaskResumed], bus.Recent().Select(e => e.Kind));
    }

    [Fact]
    public void Cancel_RemovesTask()
    {
        var queue = new DeferredTaskQueue(_path);
        DeferredTask task = queue.Enqueue("x", "r");

        Assert.True(queue.Cancel(task.Id));
        Assert.False(queue.Cancel(task.Id));
        Assert.Empty(queue.Pending);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}

public sealed class HealthMonitorTests
{
    [Fact]
    public async Task PublishesTransitionsOnly_AndTreatsHangsAndThrowsAsFailed()
    {
        var bus = new EventBus();
        HealthResult mic = HealthResult.Ok();
        var monitor = new HealthMonitor(
            [
                new HealthCheck("mic", "Микрофон", _ => Task.FromResult(mic)),
                new HealthCheck("hang", "Зависший", async ct => { await Task.Delay(Timeout.Infinite, ct); return HealthResult.Ok(); }),
                new HealthCheck("throw", "Падающий", _ => throw new IOException("нет файла")),
            ],
            bus,
            TimeSpan.FromMilliseconds(50));

        await monitor.RunOnceAsync();
        await monitor.RunOnceAsync();
        mic = HealthResult.Failed("нет кадров");
        await monitor.RunOnceAsync();
        mic = HealthResult.Ok();
        await monitor.RunOnceAsync();

        List<EdithEvent> events = bus.Recent().ToList();
        Assert.Equal(
            ["hang:Failed", "throw:Failed", "mic:Failed", "mic:Ok"],
            events.Select(e => $"{e.Get("check")}:{e.Get("status")}"));
        Assert.Equal("timeout", monitor.Current["hang"].Detail);
        Assert.Equal(HealthStatus.Failed, monitor.Overall);
        Assert.Contains("Микрофон: не работает", events[2].Summary);
    }
}

public sealed class ActivityReportTests
{
    [Fact]
    public void Summarize_UsesOnlyActions_AndHumanToolNames()
    {
        EdithEvent[] events =
        [
            EdithEvent.Create(EventKinds.CommandHeard, "открой блокнот"),
            EdithEvent.Create(EventKinds.ToolStarted, "", data: new Dictionary<string, string> { ["tool"] = "open_app" }),
            EdithEvent.Create(EventKinds.ToolFinished, "open_app", data: new Dictionary<string, string> { ["tool"] = "open_app" }),
            EdithEvent.Create(EventKinds.PermissionExecuted, "удалить файл report.txt"),
        ];

        Assert.Equal(
            "Открыла приложение. С твоего разрешения выполнила: удалить файл report.txt.",
            ActivityReport.Summarize(events));
    }

    [Fact]
    public void Summarize_IncludesPowerPlans()
    {
        Assert.Equal(
            "Запланировала выключение. Выключу компьютер в 00:10, через 4 ч. Отменила выключение.",
            ActivityReport.Summarize(
            [
                EdithEvent.Create(EventKinds.PowerScheduled, "Выключу компьютер в 00:10, через 4 ч."),
                EdithEvent.Create(EventKinds.PowerCancelled, "Выключение отменено"),
            ]));
    }

    [Fact]
    public void Summarize_WithoutActions_SaysSo()
    {
        Assert.Equal(
            "Пока ничего не делала, только разговаривала.",
            ActivityReport.Summarize([EdithEvent.Create(EventKinds.CommandHeard, "привет")]));
    }

    [Fact]
    public void Summarize_LimitsToLastItems()
    {
        var events = Enumerable.Range(1, 5)
            .Select(i => EdithEvent.Create(EventKinds.TaskDeferred, $"задача {i}"))
            .ToList();

        Assert.Equal(
            "Последние 2 действия: Отложила на потом: задача 4. Отложила на потом: задача 5.",
            ActivityReport.Summarize(events, maxItems: 2));
    }
}

public sealed class BrainProfileResourceTests
{
    private static readonly Edith.Core.Brain.BrainSettings Settings =
        Edith.Core.Brain.BrainSettingsLoader.Parse(File.ReadAllText(
            File.Exists(@"C:\EDITH\config\brain.json") ? @"C:\EDITH\config\brain.json" : throw new FileNotFoundException()));

    [Fact]
    public void ForeignGpuLoad_SelectsLightCpuProfile()
    {
        var plan = new ResourcePlan(BrainPlacement.Cpu, false, false, "gpu-busy-foreign", null);

        Edith.Core.Brain.BrainProfile profile = Edith.Core.Brain.BrainProfiles.Resolve(Settings, false, plan);

        Assert.Equal("gpu-busy", profile.Name);
        Assert.Equal(Settings.Gaming.Mode.Model, profile.Model);
        Assert.True(profile.Mode.CpuOnly);
    }

    [Fact]
    public void FreeGpuOrUnknown_KeepsNormalProfile_AndGameStillWins()
    {
        var free = new ResourcePlan(BrainPlacement.Gpu, true, false, "gpu-free", null);

        Assert.Equal("normal", Edith.Core.Brain.BrainProfiles.Resolve(Settings, false, free).Name);
        Assert.Equal("normal", Edith.Core.Brain.BrainProfiles.Resolve(Settings, false, null).Name);
        Assert.Equal("gaming", Edith.Core.Brain.BrainProfiles.Resolve(Settings, true,
            new ResourcePlan(BrainPlacement.Cpu, false, true, "game-cpu-only", null)).Name);
    }
}
