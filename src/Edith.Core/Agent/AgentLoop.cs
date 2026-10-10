using System.Text.Json;
using System.Text.Json.Nodes;
using Edith.Core.Brain;

namespace Edith.Core.Agent;

public enum ToolRisk
{
    /// <summary>Only reads (system state, files, the web).</summary>
    Read,

    /// <summary>Changes something reversible (open an app, move a file); may ask first.</summary>
    Change,

    /// <summary>Hard to undo (kill a process, recycle a file): always asks first.</summary>
    Destructive
}

/// <summary>A tool the model may call. <paramref name="Parameters"/> is a JSON schema object.</summary>
public sealed record ToolSpec(string Name, string Description, JsonObject Parameters, ToolRisk Risk);

public sealed record ToolCall(string Name, JsonElement Arguments)
{
    public string Key => Name + ":" + (Arguments.ValueKind == JsonValueKind.Undefined ? "" : Arguments.GetRawText());
}

/// <param name="Role">system / user / assistant / tool.</param>
/// <param name="ToolName">For role=tool: which tool produced <paramref name="Content"/>.</param>
public sealed record ChatMessage(
    string Role,
    string Content,
    IReadOnlyList<ToolCall>? ToolCalls = null,
    string? ToolName = null);

public sealed record ChatReply(string Content, IReadOnlyList<ToolCall> ToolCalls);

/// <summary>
/// A chat model with tool calling. <paramref name="tools"/> empty = answer only.
/// <c>UsesNativeTools</c> false = the model gets the tools as text and replies with the
/// legacy JSON decision ({"type":"tool",...}), which <see cref="AgentLoop"/> also understands.
/// </summary>
public interface IChatModel
{
    bool UsesNativeTools { get; }

    Task<ChatReply> ChatAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolSpec> tools, int step, CancellationToken cancellationToken);
}

public sealed record AgentOptions
{
    /// <summary>Model turns that may call tools before a final answer is forced.</summary>
    public int MaxSteps { get; init; } = 5;

    /// <summary>The same call (name + arguments) more often than this means the model is looping.</summary>
    public int MaxIdenticalCalls { get; init; } = 1;

    /// <summary>Tool calls handled per model turn; extra ones are ignored.</summary>
    public int MaxCallsPerStep { get; init; } = 3;

    /// <summary>Tools whose repeat is normal: looking at a page again after pressing something.</summary>
    public IReadOnlySet<string> RepeatableTools { get; init; } = new HashSet<string> { "browser_look", "read_screen", "browser_back" };

    /// <summary>Tool output is cut to this length before it goes back into the model's context.</summary>
    public int MaxToolResultChars { get; init; } = 1500;
}

public enum AgentStop
{
    Answered,
    NeedsPermission,
    StepBudget,
    Looping,
    Empty
}

public sealed record AgentStep(int Index, ToolCall Call, string Result);

public sealed record AgentResult(string Answer, AgentStop Stop, IReadOnlyList<AgentStep> Steps);

/// <summary>
/// Plan → act → observe until an answer: the model calls tools, sees their results and
/// either calls more or answers. Bounded by steps; a pending permission question ends the
/// turn (the user answers it next); a model repeating itself is stopped and asked to answer
/// from what it has.
/// </summary>
public sealed class AgentLoop(
    IChatModel model,
    Func<ToolCall, CancellationToken, Task<string>> execute,
    Func<bool>? permissionPending = null)
{
    public const string FinalAnswerRequest =
        "Хватит инструментов. Дай финальный ответ пользователю по уже собранным данным, коротко, по-русски.";

    public async Task<AgentResult> RunAsync(
        IReadOnlyList<ChatMessage> start,
        IReadOnlyList<ToolSpec> tools,
        AgentOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= new AgentOptions();
        var messages = start.ToList();
        var steps = new List<AgentStep>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int step = 0; step < options.MaxSteps; step++)
        {
            ChatReply reply = await model.ChatAsync(messages, tools, step, cancellationToken);
            (string text, IReadOnlyList<ToolCall> calls) = Interpret(reply);

            if (calls.Count == 0)
            {
                return text.Length == 0
                    ? new AgentResult("", AgentStop.Empty, steps)
                    : new AgentResult(text, AgentStop.Answered, steps);
            }

            calls = calls.Take(options.MaxCallsPerStep).ToList();
            messages.Add(new ChatMessage("assistant", reply.Content, calls));

            foreach (ToolCall call in calls)
            {
                if (!options.RepeatableTools.Contains(call.Name))
                    seen[call.Key] = seen.GetValueOrDefault(call.Key) + 1;
                if (seen.GetValueOrDefault(call.Key) > options.MaxIdenticalCalls)
                {
                    // The call is already in the history; a call without a result makes some
                    // providers (GigaChat: HTTP 422) refuse the whole conversation.
                    messages.Add(ToolResultMessage(call, "Повторный вызов не выполнен: ответь по уже полученным данным."));
                    return await FinishAsync(messages, steps, step, AgentStop.Looping, cancellationToken);
                }

                string result = await execute(call, cancellationToken);
                steps.Add(new AgentStep(step, call, result));
                messages.Add(ToolResultMessage(call, Clip(result, options.MaxToolResultChars)));

                // The tool asked the user for permission: that question is this turn's answer.
                if (permissionPending?.Invoke() == true)
                    return new AgentResult(result, AgentStop.NeedsPermission, steps);
            }
        }

        return await FinishAsync(messages, steps, options.MaxSteps, AgentStop.StepBudget, cancellationToken);
    }

    /// <summary>Native tool calls win; otherwise the content may be a legacy JSON decision.</summary>
    private (string Text, IReadOnlyList<ToolCall> Calls) Interpret(ChatReply reply)
    {
        if (reply.ToolCalls.Count > 0)
            return (reply.Content.Trim(), reply.ToolCalls);

        string content = reply.Content.Trim();
        if (model.UsesNativeTools && !LooksLikeJson(content))
            return (content, []);

        BrainDecision decision = BrainDecisionParser.Parse(content);
        if (decision.Type == "tool" && !string.IsNullOrWhiteSpace(decision.Tool))
            return ("", [new ToolCall(decision.Tool.Trim().ToLowerInvariant(), decision.Arguments)]);

        return ((decision.Text ?? "").Trim(), []);
    }

    private ChatMessage ToolResultMessage(ToolCall call, string result) =>
        model.UsesNativeTools
            ? new ChatMessage("tool", result, ToolName: call.Name)
            : new ChatMessage(
                "user",
                $"Результат инструмента `{call.Name}`:{Environment.NewLine}{result}{Environment.NewLine}{Environment.NewLine}" +
                "Теперь либо дай финальный ответ, либо вызови следующий необходимый инструмент.");

    private async Task<AgentResult> FinishAsync(
        List<ChatMessage> messages,
        List<AgentStep> steps,
        int step,
        AgentStop stop,
        CancellationToken cancellationToken)
    {
        messages.Add(new ChatMessage("user", FinalAnswerRequest));
        ChatReply reply = await model.ChatAsync(messages, [], step, cancellationToken);
        (string text, _) = Interpret(reply with { ToolCalls = [] });

        if (text.Length == 0)
            text = "Я собрала данные, но не смогла сформулировать ответ.";

        return new AgentResult(text, stop, steps);
    }

    private static bool LooksLikeJson(string content) =>
        content.StartsWith('{') || content.StartsWith("```", StringComparison.Ordinal);

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
