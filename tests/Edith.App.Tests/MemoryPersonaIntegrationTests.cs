using Edith.App;
using Edith.Core.Persona;

namespace Edith.App.Tests;

public sealed class MemoryPersonaIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-engine-v2-" + Guid.NewGuid().ToString("N"));
    private string Db => Path.Combine(_dir, "memory-v2.db");

    private ResponseEngine Engine() => new(Db, () => [], () => new PersonaProfile());

    [Fact]
    public async Task Address_ChangesGreeting_AndSurvivesRestart()
    {
        ResponseEngine engine = Engine();
        Assert.EndsWith(", сэр.", await engine.HandleAsync("привет"));

        Assert.Equal("Запомнила.", await engine.HandleAsync("называй меня Тимур"));

        string greeting = await Engine().HandleAsync("привет");
        Assert.EndsWith(", Тимур.", greeting);
    }

    [Fact]
    public async Task RelativeName_IsRecalled()
    {
        ResponseEngine engine = Engine();
        await engine.HandleAsync("мою маму зовут Ольга");

        Assert.Equal("Из близких — мама Ольга.", await Engine().HandleAsync("что ты помнишь обо мне"));
    }

    [Fact]
    public async Task ConversationRecall_SeesEarlierTurnsOfToday()
    {
        ResponseEngine engine = Engine();
        engine.CurrentSession = 42;
        await engine.HandleAsync("объясни мне коротко чем отличается ssd от hdd");

        ResponseEngine restarted = Engine();
        string recall = await restarted.HandleAsync("о чём мы говорили сегодня");

        Assert.Equal("ConversationRecall", restarted.LastRoute);
        Assert.StartsWith("Сегодня говорили про «объясни мне коротко чем отличается", recall);
    }

    [Fact]
    public async Task ReminderAnswer_ClosesIt_OtherCommandsAlsoCount()
    {
        var reminders = new Edith.Core.Reminders.ReminderScheduler();
        var engine = new ResponseEngine(Db, () => [], () => new PersonaProfile(), reminders: reminders);
        Edith.Core.Reminders.Reminder reminder = reminders.Add("проверить чайник", DateTimeOffset.Now);

        reminders.BeginAwaitingAck(reminder);
        Assert.Equal("Хорошо.", await engine.HandleAsync("хорошо"));
        Assert.Null(reminders.AwaitingAck);
        Assert.Null(reminders.ExpireAck());

        reminders.BeginAwaitingAck(reminder);
        await engine.HandleAsync("как тебя зовут");
        Assert.Equal("IdentityFastPath", engine.LastRoute);
        Assert.Null(reminders.AwaitingAck);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }
}
