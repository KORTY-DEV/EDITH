using System.Text.Json;
using System.Text.Json.Nodes;
using Edith.Core.Agent;

namespace Edith.Core.Brain;

/// <summary>
/// EDITH's agent messages ↔ GigaChat /chat/completions. GigaChat differs from Ollama/OpenAI:
/// one system message first, one function call per reply, role "function" for results and
/// function results as JSON text.
/// </summary>
public static class GigaChatProtocol
{
    public static string BuildRequest(
        string model,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        int maxTokens,
        double temperature,
        bool stream = false)
    {
        var list = new JsonArray();

        string system = string.Join("\n\n", messages.Where(m => m.Role == "system").Select(m => m.Content).Where(c => c.Length > 0));
        if (system.Length > 0)
            list.Add(new JsonObject { ["role"] = "system", ["content"] = system });

        // GigaChat refuses a history where a function call has no result after it (HTTP 422).
        string? pendingCall = null;
        void CloseOpenCall()
        {
            if (pendingCall is null)
                return;

            list.Add(new JsonObject
            {
                ["role"] = "function",
                ["name"] = pendingCall,
                ["content"] = JsonSerializer.Serialize(new { result = "(нет результата)" }),
            });
            pendingCall = null;
        }

        foreach (ChatMessage message in messages.Where(m => m.Role != "system"))
        {
            if (message.Role != "tool")
                CloseOpenCall();

            switch (message.Role)
            {
                case "assistant" when message.ToolCalls is { Count: > 0 } calls:
                    pendingCall = calls[0].Name;
                    list.Add(new JsonObject
                    {
                        ["role"] = "assistant",
                        ["content"] = message.Content ?? "",
                        ["function_call"] = new JsonObject
                        {
                            ["name"] = calls[0].Name,
                            ["arguments"] = ArgumentsNode(calls[0].Arguments),
                        },
                    });
                    break;

                case "tool":
                    pendingCall = null;
                    list.Add(new JsonObject
                    {
                        ["role"] = "function",
                        ["name"] = message.ToolName ?? "tool",
                        // Plain UTF-8, not \uXXXX: escaping Cyrillic tripled the tokens and hit
                        // GigaChat's 4096-token limit per message on a read web page.
                        ["content"] = JsonSerializer.Serialize(
                            new { result = Clip(message.Content, MaxToolResultChars) },
                            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
                    });
                    break;

                default:
                    list.Add(new JsonObject { ["role"] = message.Role, ["content"] = message.Content ?? "" });
                    break;
            }
        }

        CloseOpenCall();

        var body = new JsonObject
        {
            ["model"] = model,
            ["messages"] = list,
            ["max_tokens"] = maxTokens,
            ["temperature"] = temperature,
            ["stream"] = stream,
        };

        if (tools.Count > 0)
        {
            var functions = new JsonArray();
            foreach (ToolSpec tool in tools)
            {
                JsonObject parameters = (JsonObject)tool.Parameters.DeepClone();
                parameters.TryAdd("type", "object");
                parameters.TryAdd("properties", new JsonObject());
                functions.Add(new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = parameters,
                });
            }

            body["functions"] = functions;
            body["function_call"] = "auto";
        }

        return body.ToJsonString();
    }

    public sealed record Reply(string Content, IReadOnlyList<ToolCall> ToolCalls, int PromptTokens, int CompletionTokens);

    public static Reply ParseResponse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        JsonElement message = root.GetProperty("choices")[0].GetProperty("message");

        string content = message.TryGetProperty("content", out JsonElement c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";

        var calls = new List<ToolCall>();
        if (message.TryGetProperty("function_call", out JsonElement call) &&
            call.ValueKind == JsonValueKind.Object &&
            call.TryGetProperty("name", out JsonElement name) &&
            !string.IsNullOrWhiteSpace(name.GetString()))
        {
            JsonElement arguments = call.TryGetProperty("arguments", out JsonElement a) ? a : default;
            if (arguments.ValueKind == JsonValueKind.String)
            {
                try { arguments = JsonDocument.Parse(arguments.GetString() ?? "{}").RootElement.Clone(); }
                catch (JsonException) { arguments = JsonDocument.Parse("{}").RootElement.Clone(); }
            }
            else if (arguments.ValueKind != JsonValueKind.Object)
            {
                arguments = JsonDocument.Parse("{}").RootElement.Clone();
            }
            else
            {
                arguments = arguments.Clone();
            }

            calls.Add(new ToolCall(name.GetString()!.Trim().ToLowerInvariant(), arguments));
        }

        int prompt = 0, completion = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out JsonElement p)) p.TryGetInt32(out prompt);
            if (usage.TryGetProperty("completion_tokens", out JsonElement q)) q.TryGetInt32(out completion);
        }

        return new Reply(content, calls, prompt, completion);
    }

    /// <summary>About 2.5k tokens of Russian text: well under GigaChat's 4096 per message.</summary>
    public const int MaxToolResultChars = 5000;

    private static string Clip(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static JsonNode ArgumentsNode(JsonElement arguments) =>
        arguments.ValueKind == JsonValueKind.Object
            ? JsonNode.Parse(arguments.GetRawText())!
            : new JsonObject();
}
