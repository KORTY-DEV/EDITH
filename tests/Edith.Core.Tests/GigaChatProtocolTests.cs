using System.Text.Json;
using System.Text.Json.Nodes;
using Edith.Core.Agent;
using Edith.Core.Brain;

namespace Edith.Core.Tests;

public sealed class GigaChatProtocolTests
{
    [Fact]
    public void Request_MergesSystem_MapsToolCallsAndResults()
    {
        JsonElement args = JsonDocument.Parse("""{"name":"telegram"}""").RootElement;
        var messages = new List<ChatMessage>
        {
            new("system", "ты EDITH"),
            new("system", "инструменты"),
            new("user", "открой телеграм"),
            new("assistant", "", [new ToolCall("open_app", args)]),
            new("tool", "открыто", ToolName: "open_app"),
        };
        var tools = new List<ToolSpec> { new("open_app", "Открыть приложение", new JsonObject(), ToolRisk.Read) };

        JsonNode body = JsonNode.Parse(GigaChatProtocol.BuildRequest("GigaChat-2-Max", messages, tools, 500, 0.3))!;
        JsonArray list = body["messages"]!.AsArray();

        Assert.Equal(4, list.Count);
        Assert.Equal("ты EDITH\n\nинструменты", (string?)list[0]!["content"]);
        Assert.Equal("open_app", (string?)list[2]!["function_call"]!["name"]);
        Assert.Equal("telegram", (string?)list[2]!["function_call"]!["arguments"]!["name"]);
        Assert.Equal("function", (string?)list[3]!["role"]);
        Assert.Equal("открыто", (string?)JsonNode.Parse((string)list[3]!["content"]!)!["result"]);
        Assert.Equal("object", (string?)body["functions"]![0]!["parameters"]!["type"]);
        Assert.Equal("auto", (string?)body["function_call"]);
    }

    [Fact]
    public void Response_ReadsAnswerCallAndUsage()
    {
        var reply = GigaChatProtocol.ParseResponse("""
            {"choices":[{"message":{"role":"assistant","content":"","function_call":{"name":"Open_App","arguments":{"name":"vk"}}},"finish_reason":"function_call"}],
             "usage":{"prompt_tokens":1200,"completion_tokens":15}}
            """);

        Assert.Equal("open_app", reply.ToolCalls.Single().Name);
        Assert.Equal("vk", reply.ToolCalls[0].Arguments.GetProperty("name").GetString());
        Assert.Equal(1200, reply.PromptTokens);

        var answer = GigaChatProtocol.ParseResponse("""{"choices":[{"message":{"role":"assistant","content":"Привет."}}]}""");
        Assert.Equal("Привет.", answer.Content);
        Assert.Empty(answer.ToolCalls);
    }

    [Fact]
    public void Request_NeverLeavesAFunctionCallWithoutResult()
    {
        JsonElement args = JsonDocument.Parse("{}").RootElement;
        var messages = new List<ChatMessage>
        {
            new("user", "что на странице"),
            new("assistant", "", [new ToolCall("browser_look", args)]),
            new("user", "Ответь по уже полученным данным."),
        };

        JsonArray list = JsonNode.Parse(GigaChatProtocol.BuildRequest("m", messages, [], 100, 0.2))!["messages"]!.AsArray();

        Assert.Equal(["user", "assistant", "function", "user"], list.Select(x => (string?)x!["role"]).ToArray());
        Assert.Equal("browser_look", (string?)list[2]!["name"]);
    }
}
