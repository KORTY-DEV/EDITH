using System.Text.Json;
using Edith.App;
using Edith.Core.Runtime;

namespace Edith.App.Tests;

public sealed class RuntimeIntegrationTests
{
    private static string TempMemory() =>
        Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json");

    [Theory]
    [InlineData("что ты делала")]
    [InlineData("Что ты сделала сегодня?")]
    [InlineData("чем ты занималась")]
    public async Task ActivityQuestion_AnswersFromJournalSource(string question)
    {
        EdithEvent[] journal =
        [
            EdithEvent.Create(EventKinds.CommandHeard, "открой проводник"),
            EdithEvent.Create(EventKinds.ToolFinished, "open_app",
                data: new Dictionary<string, string> { ["tool"] = "open_app" }),
        ];
        var engine = new ResponseEngine(TempMemory(), () => journal);

        string response = await engine.HandleAsync(question);

        Assert.Equal("ActivityReport", engine.LastRoute);
        Assert.Equal("Открыла приложение.", response);
    }

    [Fact]
    public async Task ActivityQuestion_BrokenJournal_IsReportedNotThrown()
    {
        var engine = new ResponseEngine(TempMemory(), () => throw new IOException("db locked"));

        Assert.Equal("Не смогла открыть журнал действий.", await engine.HandleAsync("что ты делала"));
    }

    [Theory]
    [InlineData("что ты помнишь обо мне")]
    [InlineData("что ты делаешь на выходных")]
    public void OtherQuestions_AreNotActivityQueries(string question)
    {
        Assert.False(ActivityReport.IsActivityQuery(question));
    }

    [Fact]
    public async Task ToolRouter_JournalsStartAndFailure()
    {
        string tool = "zz_missing_" + Guid.NewGuid().ToString("N")[..8];
        var seen = new List<EdithEvent>();
        using IDisposable subscription = EdithEvents.Bus.Subscribe(evt =>
        {
            if (evt.Get("tool") == tool)
                lock (seen) seen.Add(evt);
        });

        string result = await new BrainToolRouter().ExecuteAsync(
            tool,
            JsonDocument.Parse("""{"x":1}""").RootElement,
            new ToolExecutionContext("проверка", false),
            CancellationToken.None);

        Assert.StartsWith("Неизвестный инструмент", result);
        Assert.Equal([EventKinds.ToolStarted, EventKinds.ToolFailed], seen.Select(e => e.Kind));
        Assert.Equal("""{"x":1}""", seen[0].Get("arguments"));
        Assert.Equal(result, seen[1].Get("result"));
    }
}
