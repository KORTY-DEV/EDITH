using Edith.Core.Power;
using Edith.Core.Runtime;

namespace Edith.Core.Tests;

public sealed class ShutdownCommandParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 20, 10, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("выключи компьютер через 4 часа", 240)]
    [InlineData("Эдит, выключи ПК через четыре часа", 240)]
    [InlineData("выключи комп через час", 60)]
    [InlineData("выключи компьютер через полтора часа", 90)]
    [InlineData("выключи компьютер через два с половиной часа", 150)]
    [InlineData("выключи компьютер через полчаса", 30)]
    [InlineData("выключи компьютер через 15 минут", 15)]
    [InlineData("выключи компьютер через двадцать минут", 20)]
    public void In_SchedulesRelativeTime(string phrase, int minutes)
    {
        Assert.True(ShutdownCommandParser.TryParse(phrase, Now, out ShutdownCommand command));
        Assert.Equal(ShutdownCommandKind.ScheduleAt, command.Kind);
        Assert.Equal(Now.AddMinutes(minutes), command.At);
    }

    [Theory]
    [InlineData("выключи компьютер в 23:30", 23, 30, 23)]
    [InlineData("выключи компьютер в 23 30", 23, 30, 23)]
    [InlineData("выключи компьютер в 7", 7, 0, 24)]
    [InlineData("выключи компьютер в 20:05", 20, 5, 24)]
    public void At_SchedulesNextOccurrence(string phrase, int hour, int minute, int day)
    {
        Assert.True(ShutdownCommandParser.TryParse(phrase, Now, out ShutdownCommand command));
        Assert.Equal((day, hour, minute), (command.At!.Value.Day, command.At.Value.Hour, command.At.Value.Minute));
    }

    [Theory]
    [InlineData("выключи компьютер когда агент закончит работу")]
    [InlineData("выключи пк, когда ассистент завершит")]
    [InlineData("выключи компьютер когда ии доделает")]
    public void AfterAgent(string phrase)
    {
        Assert.True(ShutdownCommandParser.TryParse(phrase, Now, out ShutdownCommand command));
        Assert.Equal(ShutdownCommandKind.AfterAgent, command.Kind);
    }

    [Theory]
    [InlineData("отмени выключение", ShutdownCommandKind.Cancel)]
    [InlineData("не выключай компьютер", ShutdownCommandKind.Cancel)]
    [InlineData("когда ты выключишь компьютер", ShutdownCommandKind.Status)]
    [InlineData("во сколько выключение", ShutdownCommandKind.Status)]
    public void CancelAndStatus(string phrase, ShutdownCommandKind kind)
    {
        Assert.True(ShutdownCommandParser.TryParse(phrase, Now, out ShutdownCommand command));
        Assert.Equal(kind, command.Kind);
    }

    [Theory]
    [InlineData("выключи компьютер")]
    [InlineData("выключи свет через час")]
    [InlineData("выключи компьютер через 300 часов")]
    [InlineData("выключи компьютер в 25:00")]
    [InlineData("как выключить компьютер")]
    public void Rejects_ImmediateOrNonsense(string phrase)
    {
        Assert.False(ShutdownCommandParser.TryParse(phrase, Now, out _));
    }
}

public sealed class ShutdownSchedulerTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), $"edith-shutdown-{Guid.NewGuid():N}.json");
    private DateTimeOffset _now = new(2026, 9, 23, 20, 0, 0, TimeSpan.FromHours(3));
    private readonly FakePower _power = new();
    private readonly FakeAgent _agent = new();

    private ShutdownScheduler Create(IEventBus? bus = null) => new(_power, _agent, () => _now, bus, _state);

    [Fact]
    public void AtTime_IssuesOnce_WhenDue()
    {
        var bus = new EventBus();
        ShutdownScheduler scheduler = Create(bus);
        scheduler.Schedule(new ShutdownCommand(ShutdownCommandKind.ScheduleAt, _now.AddHours(4)));

        scheduler.Tick();
        Assert.Equal(0, _power.Shutdowns);

        _now = _now.AddHours(4);
        scheduler.Tick();
        scheduler.Tick();

        Assert.Equal(1, _power.Shutdowns);
        Assert.Equal(ShutdownScheduler.Grace, _power.LastGrace);
        Assert.Equal(["power.scheduled", "power.issued"], bus.Recent().Select(e => e.Kind));
    }

    [Fact]
    public void Cancel_BeforeDue_PreventsShutdown_AfterIssue_Aborts()
    {
        ShutdownScheduler scheduler = Create();
        scheduler.Schedule(new ShutdownCommand(ShutdownCommandKind.ScheduleAt, _now.AddMinutes(1)));
        Assert.True(scheduler.Cancel());
        _now = _now.AddMinutes(2);
        scheduler.Tick();
        Assert.Equal(0, _power.Shutdowns);
        Assert.False(scheduler.Cancel());

        scheduler.Schedule(new ShutdownCommand(ShutdownCommandKind.ScheduleAt, _now));
        scheduler.Tick();
        Assert.True(scheduler.Cancel());
        Assert.Equal(1, _power.Aborts);
    }

    [Fact]
    public void Plan_SurvivesRestart_ButAnIssuedOneIsNotRepeated()
    {
        Create().Schedule(new ShutdownCommand(ShutdownCommandKind.ScheduleAt, _now.AddHours(1)));

        ShutdownScheduler restarted = Create();
        Assert.Equal(ShutdownTrigger.AtTime, restarted.Current?.Trigger);

        _now = _now.AddHours(1);
        restarted.Tick();
        Assert.Equal(1, _power.Shutdowns);

        ShutdownScheduler again = Create();
        Assert.Null(again.Current);
    }

    [Fact]
    public void AfterAgent_WaitsForStopNewerThanBusy_AndAQuietPeriod()
    {
        ShutdownScheduler scheduler = Create();
        _agent.LastStop = _now.AddMinutes(-30); // an old stop from before arming does not count
        scheduler.Schedule(new ShutdownCommand(ShutdownCommandKind.AfterAgent));

        _now = _now.AddMinutes(10);
        scheduler.Tick();
        Assert.Equal(0, _power.Shutdowns);

        _agent.LastStop = _now;               // agent finished a turn...
        _now = _now.AddMinutes(1);
        _agent.LastBusy = _now;               // ...but a new prompt came in
        _now = _now.AddMinutes(5);
        scheduler.Tick();
        Assert.Equal(0, _power.Shutdowns);

        _agent.LastStop = _now;               // finished for real
        _now = _now.AddMinutes(1);
        scheduler.Tick();
        Assert.Equal(0, _power.Shutdowns);     // still inside the quiet period

        _now = _now.AddMinutes(1);
        scheduler.Tick();
        Assert.Equal(1, _power.Shutdowns);
    }

    [Fact]
    public void FailingShutdown_ClearsPlan_AndReportsError()
    {
        var bus = new EventBus();
        _power.Throw = true;
        ShutdownScheduler scheduler = Create(bus);
        scheduler.Schedule(new ShutdownCommand(ShutdownCommandKind.ScheduleAt, _now));

        scheduler.Tick();

        Assert.Null(scheduler.Current);
        Assert.Equal(EventKinds.Error, bus.Recent().Last().Kind);
    }

    [Fact]
    public void Describe_SpeaksPlan()
    {
        Assert.Equal("Выключение не запланировано.", ShutdownScheduler.Describe(null, _now));
        Assert.Equal(
            "Выключу компьютер в 00:00, через 4 ч.",
            ShutdownScheduler.Describe(new ShutdownState(ShutdownTrigger.AtTime, _now.AddHours(4), _now), _now));
        Assert.Equal(
            "Выключу компьютер в 20:25, через 25 мин.",
            ShutdownScheduler.Describe(new ShutdownState(ShutdownTrigger.AtTime, _now.AddMinutes(25), _now), _now));
    }

    public void Dispose()
    {
        try { File.Delete(_state); } catch { }
    }

    private sealed class FakePower : IPowerControl
    {
        public int Shutdowns;
        public int Aborts;
        public TimeSpan LastGrace;
        public bool Throw;

        public void ShutdownWithGrace(TimeSpan grace, string reason)
        {
            if (Throw)
                throw new InvalidOperationException("access denied");
            Shutdowns++;
            LastGrace = grace;
        }

        public void AbortShutdown() => Aborts++;
    }

    private sealed class FakeAgent : IAgentSignals
    {
        public DateTimeOffset? LastStop { get; set; }
        public DateTimeOffset? LastBusy { get; set; }
    }
}
