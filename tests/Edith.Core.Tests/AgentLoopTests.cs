using System.Text.Json;
using Edith.Core.Agent;

namespace Edith.Core.Tests;

public sealed class AgentLoopTests
{
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>Replays scripted replies and records what the loop sent.</summary>
    private sealed class ScriptedModel(bool native, params ChatReply[] replies) : IChatModel
    {
        private int _next;
        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];
        public List<int> ToolCounts { get; } = [];

        public bool UsesNativeTools => native;

        public Task<ChatReply> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int step, CancellationToken cancellationToken)
        {
            Requests.Add(messages.ToList());
            ToolCounts.Add(tools.Count);
            return Task.FromResult(replies[Math.Min(_next++, replies.Length - 1)]);
        }
    }

    private static readonly IReadOnlyList<ToolSpec> Tools =
    [
        new("find_files", "", new System.Text.Json.Nodes.JsonObject(), ToolRisk.Read),
        new("read_text_file", "", new System.Text.Json.Nodes.JsonObject(), ToolRisk.Read),
    ];

    private static IReadOnlyList<ChatMessage> Start => [new("system", "s"), new("user", "найди и прочитай заметку")];

    [Fact]
    public async Task Native_MultiStep_ToolResultsGoBackAsToolMessages()
    {
        var model = new ScriptedModel(true,
            new ChatReply("", [new ToolCall("find_files", Args("""{"root":"D","pattern":"*заметк*"}"""))]),
            new ChatReply("", [new ToolCall("read_text_file", Args("""{"path":"D\\заметка.txt"}"""))]),
            new ChatReply("В заметке список покупок.", []));
        var executed = new List<string>();
        var loop = new AgentLoop(model, (call, _) => { executed.Add(call.Name); return Task.FromResult("ok:" + call.Name); });

        AgentResult result = await loop.RunAsync(Start, Tools, null, CancellationToken.None);

        Assert.Equal(AgentStop.Answered, result.Stop);
        Assert.Equal("В заметке список покупок.", result.Answer);
        Assert.Equal(["find_files", "read_text_file"], executed);
        ChatMessage toolMessage = model.Requests[1].Last();
        Assert.Equal(("tool", "ok:find_files", "find_files"), (toolMessage.Role, toolMessage.Content, toolMessage.ToolName));
        Assert.Equal("find_files", model.Requests[1][^2].ToolCalls![0].Name);
    }

    [Fact]
    public async Task Legacy_JsonDecisions_Work()
    {
        var model = new ScriptedModel(false,
            new ChatReply("""{"type":"tool","tool":"GPU_STATUS","arguments":{}}""", []),
            new ChatReply("""{"type":"answer","text":"Видеокарта холодная."}""", []));
        string? called = null;
        var loop = new AgentLoop(model, (call, _) => { called = call.Name; return Task.FromResult("45C"); });

        AgentResult result = await loop.RunAsync(Start, [], null, CancellationToken.None);

        Assert.Equal("gpu_status", called);
        Assert.Equal("Видеокарта холодная.", result.Answer);
        Assert.StartsWith("Результат инструмента `gpu_status`", model.Requests[1].Last().Content);
        Assert.Equal("user", model.Requests[1].Last().Role);
    }

    [Fact]
    public async Task RepeatedIdenticalCall_IsStopped_AndAnswerForcedWithoutTools()
    {
        ToolCall same = new("find_files", Args("""{"root":"D","pattern":"*"}"""));
        var model = new ScriptedModel(true,
            new ChatReply("", [same]),
            new ChatReply("", [same]),
            new ChatReply("Нашла три файла.", []));
        int executions = 0;
        var loop = new AgentLoop(model, (_, _) => { executions++; return Task.FromResult("3 files"); });

        AgentResult result = await loop.RunAsync(Start, Tools, null, CancellationToken.None);

        Assert.Equal(AgentStop.Looping, result.Stop);
        Assert.Equal(1, executions);
        Assert.Equal("Нашла три файла.", result.Answer);
        Assert.Equal(0, model.ToolCounts.Last());
        Assert.Equal(AgentLoop.FinalAnswerRequest, model.Requests.Last().Last().Content);
    }

    [Fact]
    public async Task StepBudget_ForcesFinalAnswer()
    {
        int n = 0;
        var model = new ScriptedModel(true,
            new ChatReply("", [new ToolCall("find_files", Args("""{"n":1}"""))]),
            new ChatReply("", [new ToolCall("find_files", Args("""{"n":2}"""))]),
            new ChatReply("Итог по двум поискам.", []));
        var loop = new AgentLoop(model, (_, _) => Task.FromResult($"r{++n}"));

        AgentResult result = await loop.RunAsync(Start, Tools, new AgentOptions { MaxSteps = 2 }, CancellationToken.None);

        Assert.Equal(AgentStop.StepBudget, result.Stop);
        Assert.Equal(2, result.Steps.Count);
        Assert.Equal("Итог по двум поискам.", result.Answer);
    }

    [Fact]
    public async Task PendingPermission_EndsTheTurnWithTheQuestion()
    {
        bool pending = false;
        var model = new ScriptedModel(true,
            new ChatReply("", [new ToolCall("delete_file", Args("""{"path":"x"}""")), new ToolCall("find_files", Args("{}"))]));
        var executed = new List<string>();
        var loop = new AgentLoop(
            model,
            (call, _) => { executed.Add(call.Name); pending = true; return Task.FromResult("Внимание. Отправить в корзину x. Подтвердить?"); },
            () => pending);

        AgentResult result = await loop.RunAsync(Start, Tools, null, CancellationToken.None);

        Assert.Equal(AgentStop.NeedsPermission, result.Stop);
        Assert.Equal("Внимание. Отправить в корзину x. Подтвердить?", result.Answer);
        Assert.Equal(["delete_file"], executed);
        Assert.Single(model.Requests);
    }

    [Fact]
    public async Task LongToolOutput_IsClippedForTheModel_ButKeptInSteps()
    {
        string huge = new('x', 5000);
        var model = new ScriptedModel(true,
            new ChatReply("", [new ToolCall("read_text_file", Args("{}"))]),
            new ChatReply("Прочитала.", []));
        var loop = new AgentLoop(model, (_, _) => Task.FromResult(huge));

        AgentResult result = await loop.RunAsync(Start, Tools, new AgentOptions { MaxToolResultChars = 100 }, CancellationToken.None);

        Assert.Equal(101, model.Requests[1].Last().Content.Length);
        Assert.Equal(5000, result.Steps[0].Result.Length);
    }

    [Fact]
    public async Task NativeModel_PlainAnswer_NoTools()
    {
        var model = new ScriptedModel(true, new ChatReply("Я помню чудное мгновенье…", []));
        var loop = new AgentLoop(model, (_, _) => throw new InvalidOperationException("no tools expected"));

        AgentResult result = await loop.RunAsync(Start, Tools, null, CancellationToken.None);

        Assert.Equal(("Я помню чудное мгновенье…", AgentStop.Answered), (result.Answer, result.Stop));
    }

    [Fact]
    public async Task EmptyReply_IsReportedAsEmpty()
    {
        var loop = new AgentLoop(new ScriptedModel(true, new ChatReply("  ", [])), (_, _) => Task.FromResult(""));

        Assert.Equal(AgentStop.Empty, (await loop.RunAsync(Start, Tools, null, CancellationToken.None)).Stop);
    }
}
