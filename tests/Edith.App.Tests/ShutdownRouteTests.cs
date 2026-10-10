using Edith.App;
using Edith.Core.Persona;
using Edith.Core.Power;

namespace Edith.App.Tests;

public sealed class ShutdownRouteTests
{
    private sealed class NoPower : IPowerControl
    {
        public int Shutdowns;
        public void ShutdownWithGrace(TimeSpan grace, string reason) => Shutdowns++;
        public void AbortShutdown() { }
    }

    private sealed class NoAgent : IAgentSignals
    {
        public DateTimeOffset? LastStop => null;
        public DateTimeOffset? LastBusy => null;
    }

    [Fact]
    public async Task Schedule_AsksPermission_AndOnlyConfirmationArmsIt()
    {
        var power = new NoPower();
        var scheduler = new ShutdownScheduler(power, new NoAgent());
        Func<Task<string>>? pending = null;
        string? askedSummary = null;
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile(),
            scheduler,
            (summary, risk, execute) =>
            {
                askedSummary = summary;
                pending = execute;
                return "Нужно подтверждение: " + summary + ".";
            });

        string ask = await engine.HandleAsync("выключи компьютер через 4 часа");

        Assert.Equal("PowerSchedule", engine.LastRoute);
        Assert.StartsWith("Нужно подтверждение: выключить компьютер в ", ask);
        Assert.Null(scheduler.Current);

        string confirmed = await pending!();
        Assert.StartsWith("Выключу компьютер в ", confirmed);
        Assert.Equal(ShutdownTrigger.AtTime, scheduler.Current?.Trigger);
        Assert.Equal(0, power.Shutdowns);

        Assert.StartsWith("Выключу компьютер в ", await engine.HandleAsync("во сколько выключение"));
        Assert.Equal("Выключение отменено.", await engine.HandleAsync("отмени выключение"));
        Assert.Equal("Выключение и так не запланировано.", await engine.HandleAsync("отмени выключение"));
    }

    [Fact]
    public async Task WithoutScheduler_SaysUnavailable()
    {
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile());

        Assert.Equal("Выключение по расписанию сейчас недоступно.", await engine.HandleAsync("выключи компьютер через час"));
    }
}

public sealed class ReminderRouteTests
{
    [Fact]
    public async Task ReminderPhraseAboutShutdown_IsAReminder_NotAShutdown()
    {
        var reminders = new Edith.Core.Reminders.ReminderScheduler();
        bool permissionAsked = false;
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile(),
            new ShutdownScheduler(new NoPowerAtAll(), new NoSignals()),
            (_, _, _) => { permissionAsked = true; return "?"; },
            reminders: reminders);

        string reply = await engine.HandleAsync("напомни выключить компьютер через час");

        Assert.Equal("Reminder", engine.LastRoute);
        Assert.StartsWith("Хорошо, напомню ", reply); // "в 17:00" or "завтра в 00:30" near midnight
        Assert.EndsWith(": выключить компьютер.", reply);
        Assert.False(permissionAsked);
        Assert.Single(reminders.Pending);
        Assert.StartsWith("Напоминаний: 1.", await engine.HandleAsync("какие у меня напоминания"));
    }

    private sealed class NoPowerAtAll : IPowerControl
    {
        public void ShutdownWithGrace(TimeSpan grace, string reason) => throw new InvalidOperationException();
        public void AbortShutdown() { }
    }

    private sealed class NoSignals : IAgentSignals
    {
        public DateTimeOffset? LastStop => null;
        public DateTimeOffset? LastBusy => null;
    }
}
