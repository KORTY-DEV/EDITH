using Edith.Core.Reminders;
using Edith.Core.Runtime;
using Edith.Core.Time;

namespace Edith.Core.Tests;

public sealed class ReminderParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 16, 0, 0, TimeSpan.FromHours(3));

    [Theory]
    [InlineData("напомни мне через час позвонить маме", 60, "позвонить маме")]
    [InlineData("Эдит, напомни через 20 минут что надо выключить духовку", 20, "надо выключить духовку")]
    [InlineData("напомни выключить духовку через двадцать минут", 20, "выключить духовку")]
    [InlineData("напомни через полчаса про чайник", 30, "чайник")]
    [InlineData("напомни мне через полтора часа забрать мою посылку", 90, "забрать твою посылку")]
    [InlineData("напомни через 30 секунд проверить", 0.5, "проверить")]
    public void Delay(string phrase, double minutes, string text)
    {
        Assert.True(ReminderParser.TryParse(phrase, Now, out ReminderCommand command));
        Assert.Equal(ReminderCommandKind.Add, command.Kind);
        Assert.Equal(Now.AddMinutes(minutes), command.DueAt);
        Assert.Equal(text, command.Text);
    }

    [Theory]
    [InlineData("напомни завтра в 9 про встречу", 24, 9, 0, "встречу")]
    [InlineData("напомни в 18:30 купить хлеб", 23, 18, 30, "купить хлеб")]
    [InlineData("напомни в 7 вечера что стрим", 23, 19, 0, "стрим")]
    [InlineData("напомни в 10 утра сделать зарядку", 24, 10, 0, "сделать зарядку")]
    [InlineData("напомни мне сходить в магазин в 20:00", 23, 20, 0, "сходить в магазин")]
    public void At(string phrase, int day, int hour, int minute, string text)
    {
        Assert.True(ReminderParser.TryParse(phrase, Now, out ReminderCommand command));
        Assert.Equal((day, hour, minute), (command.DueAt!.Value.Day, command.DueAt.Value.Hour, command.DueAt.Value.Minute));
        Assert.Equal(text, command.Text);
    }

    [Theory]
    [InlineData("какие у меня напоминания", ReminderCommandKind.List)]
    [InlineData("что ты должна мне напомнить", ReminderCommandKind.List)]
    [InlineData("отмени все напоминания", ReminderCommandKind.CancelAll)]
    [InlineData("отмени напоминание", ReminderCommandKind.CancelLast)]
    public void Management(string phrase, ReminderCommandKind kind)
    {
        Assert.True(ReminderParser.TryParse(phrase, Now, out ReminderCommand command));
        Assert.Equal(kind, command.Kind);
    }

    [Theory]
    [InlineData("напомни мне")]
    [InlineData("напомни как его зовут")]
    [InlineData("напомни сегодня в 10 утра позвонить")] // already past today
    [InlineData("выключи компьютер через час")]
    public void Rejects(string phrase)
    {
        Assert.False(ReminderParser.TryParse(phrase, Now, out ReminderCommand command) && command.Kind == ReminderCommandKind.Add);
    }
}

public sealed class ReminderSchedulerTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"edith-reminders-{Guid.NewGuid():N}.json");
    private DateTimeOffset _now = new(2026, 9, 23, 16, 0, 0, TimeSpan.FromHours(3));

    [Fact]
    public void DueRemindersAreHandedOutOnce_AndSurviveRestart()
    {
        var bus = new EventBus();
        var scheduler = new ReminderScheduler(_path, bus, () => _now);
        scheduler.Add("позвонить маме", _now.AddHours(1));
        scheduler.Add("чайник", _now.AddMinutes(5));

        var restarted = new ReminderScheduler(_path, bus, () => _now);
        Assert.Empty(restarted.TakeDue());

        _now = _now.AddMinutes(10);
        Assert.Equal(["чайник"], restarted.TakeDue().Select(r => r.Text));
        Assert.Empty(restarted.TakeDue());

        _now = _now.AddHours(2);
        Assert.Equal(["позвонить маме"], new ReminderScheduler(_path, bus, () => _now).TakeDue().Select(r => r.Text));
        Assert.Contains(bus.Recent(), e => e.Kind == "reminder.due" && e.Summary == "чайник");
    }

    [Fact]
    public void CancelLastAndAll()
    {
        var scheduler = new ReminderScheduler(_path, clock: () => _now);
        scheduler.Add("a", _now.AddHours(1));
        _now = _now.AddSeconds(1);
        scheduler.Add("b", _now.AddMinutes(30));

        Assert.Equal("b", scheduler.CancelLast()?.Text);
        Assert.Equal(1, scheduler.CancelAll());
        Assert.Null(scheduler.CancelLast());
    }

    [Fact]
    public void DescribePending_SortsAndLimits()
    {
        Reminder[] pending =
        [
            new(Guid.NewGuid(), "позвонить маме", _now.AddHours(1), _now),
            new(Guid.NewGuid(), "встреча", _now.AddDays(1).AddHours(-7), _now),
        ];

        Assert.Equal(
            "Напоминаний: 2. в 17:00 — позвонить маме; завтра в 09:00 — встреча.",
            ReminderScheduler.DescribePending(pending, _now));
        Assert.Equal("Напоминаний нет.", ReminderScheduler.DescribePending([], _now));
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch { }
    }
}

public sealed class ReminderAckTests
{
    [Theory]
    [InlineData("хорошо", true)]
    [InlineData("Окей.", true)]
    [InlineData("да сделал", true)]
    [InlineData("угу спасибо", true)]
    [InlineData("открой дискорд", false)]
    [InlineData("какая сегодня погода в москве", false)]
    public void RecognizesShortAnswers(string text, bool expected)
    {
        Assert.Equal(expected, ReminderAck.IsAcknowledgement(text));
    }

    [Fact]
    public void Unanswered_RepeatsTwiceAsQuestion_ThenGivesUp()
    {
        DateTimeOffset now = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(3));
        var scheduler = new ReminderScheduler(clock: () => now);
        Reminder first = scheduler.Add("проверить чайник", now);
        Assert.Equal("Напоминаю: проверить чайник.", first.Spoken);

        scheduler.BeginAwaitingAck(first);
        Reminder second = scheduler.ExpireAck()!;
        Assert.Equal(now + ReminderScheduler.RepeatDelay, second.DueAt);
        Assert.Equal("Напоминаю ещё раз: проверить чайник. Сделано?", second.Spoken);

        scheduler.BeginAwaitingAck(second);
        Reminder third = scheduler.ExpireAck()!;
        scheduler.BeginAwaitingAck(third);
        Assert.Null(scheduler.ExpireAck());
    }
}

public sealed class SpokenTimeTests
{
    [Fact]
    public void Describe_UsesRelativeDays()
    {
        var now = new DateTimeOffset(2026, 9, 23, 16, 0, 0, TimeSpan.FromHours(3));

        Assert.Equal("в 18:30", SpokenTime.Describe(now.AddHours(2.5), now));
        Assert.Equal("завтра в 09:00", SpokenTime.Describe(now.AddHours(17), now));
        Assert.Equal("послезавтра в 16:00", SpokenTime.Describe(now.AddDays(2), now));
        Assert.Equal("28 сентября в 16:00", SpokenTime.Describe(now.AddDays(5), now));
    }
}
