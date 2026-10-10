using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Edith.App;

namespace Edith.App.Tests;

/// <summary>
/// The real BrainService against a fake Ollama on localhost: HTTP, native tool calling,
/// the agent loop and the JSON fallback — everything except an actual model.
/// </summary>
public sealed class BrainAgentIntegrationTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "edith-fake-ollama-" + Guid.NewGuid().ToString("N"));
    private readonly List<JsonDocument> _chatRequests = [];
    private readonly Queue<(int Status, string Body)> _chatReplies = new();
    private readonly Queue<TimeSpan> _chatDelays = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _config;

    public BrainAgentIntegrationTests()
    {
        int port = FreePort();
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(ServeAsync);

        Directory.CreateDirectory(_dir);
        // The fake Ollama must answer, not the real cloud brain.
        string cloudOff = Path.Combine(_dir, "cloud.json");
        File.WriteAllText(cloudOff, """{ "enabled": false }""");
        Environment.SetEnvironmentVariable("EDITH_CLOUD_CONFIG", cloudOff);
        _config = Path.Combine(_dir, "brain.json");
        File.WriteAllText(_config, $$"""
            {
              "enabled": true,
              "endpoint": "http://localhost:{{port}}",
              "model": "fake:1b",
              "maxToolSteps": 4,
              "normal": { "model": "fake:1b", "keepAlive": "90s", "stepTimeoutSeconds": 10, "totalTimeoutSeconds": 30 },
              "gaming": { "enabled": false }
            }
            """);
    }

    private static int FreePort()
    {
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            // Each request on its own: a slow (delayed) reply must not block the next one.
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            string path = context.Request.Url!.AbsolutePath;
            string request = await new StreamReader(context.Request.InputStream).ReadToEndAsync();
            (int status, string body) = path switch
            {
                "/api/tags" => (200, """{"models":[{"name":"fake:1b","size":1000}]}"""),
                "/api/ps" => (200, """{"models":[{"name":"fake:1b","size":1000,"size_vram":1000}]}"""),
                "/api/chat" => await NextChatAsync(request),
                _ => (200, """{"done":true}"""),
            };

            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
        catch
        {
            // The client gave up (cancelled request): nothing to answer.
        }
    }

    private async Task<(int, string)> NextChatAsync(string request)
    {
        TimeSpan delay;
        (int, string) reply;
        lock (_chatRequests)
        {
            _chatRequests.Add(JsonDocument.Parse(request));
            delay = _chatDelays.Count > 0 ? _chatDelays.Dequeue() : TimeSpan.Zero;
            reply = _chatReplies.Count > 0 ? _chatReplies.Dequeue() : (200, Reply("готова"));
        }

        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);
        return reply;
    }

    private static string Reply(string content, string? toolCalls = null) =>
        $$"""{"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}{{(toolCalls is null ? "" : ",\"tool_calls\":" + toolCalls)}}},"done":true,"eval_count":5}""";

    [Fact]
    public async Task NativeToolCalls_RunTheTool_AndSendTheResultBackAsToolMessage()
    {
        _chatReplies.Enqueue((200, Reply("", """[{"function":{"name":"system_snapshot","arguments":{}}}]""")));
        _chatReplies.Enqueue((200, Reply("Памяти хватает, диски в порядке.")));

        string answer = await new BrainService(_config).RespondAsync("сколько свободно памяти?");

        Assert.Equal("Памяти хватает, диски в порядке.", answer);
        JsonElement first = _chatRequests[0].RootElement;
        Assert.True(first.TryGetProperty("tools", out JsonElement tools));
        Assert.Contains(tools.EnumerateArray(), t => t.GetProperty("function").GetProperty("name").GetString() == "system_snapshot");
        Assert.False(first.TryGetProperty("format", out _));

        JsonElement[] second = _chatRequests[1].RootElement.GetProperty("messages").EnumerateArray().ToArray();
        Assert.Equal("assistant", second[^2].GetProperty("role").GetString());
        Assert.Equal("system_snapshot", second[^2].GetProperty("tool_calls")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("tool", second[^1].GetProperty("role").GetString());
        Assert.Equal("system_snapshot", second[^1].GetProperty("tool_name").GetString());
        Assert.Contains("RAM", second[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ModelWithoutTools_FallsBackToJsonProtocol_AndIsRemembered()
    {
        _chatReplies.Enqueue((400, """{"error":"registry.ollama.ai/library/fake:1b does not support tools"}"""));
        _chatReplies.Enqueue((200, Reply("""{"type":"answer","text":"Работаю по старому протоколу."}""")));

        string answer = await new BrainService(_config).RespondAsync("привет, как дела?");

        Assert.Equal("Работаю по старому протоколу.", answer);
        JsonElement retry = _chatRequests[1].RootElement;
        Assert.Equal("json", retry.GetProperty("format").GetString());
        Assert.False(retry.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task BackgroundJson_IsPlain_AndYieldsToTheUser()
    {
        _chatReplies.Enqueue((200, Reply("""{"facts":[]}""")));
        var brain = new BrainService(_config);

        Assert.Equal("""{"facts":[]}""", await brain.CompleteJsonAsync("system", "user", CancellationToken.None));
        JsonElement plain = _chatRequests[0].RootElement;
        Assert.Equal("json", plain.GetProperty("format").GetString());
        Assert.False(plain.TryGetProperty("tools", out _));

        // A slow background request is running; the user asks something meanwhile.
        _chatDelays.Enqueue(TimeSpan.FromSeconds(8));
        _chatReplies.Enqueue((200, Reply("""{"facts":[]}""")));
        _chatReplies.Enqueue((200, Reply("Слушаю.")));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Task<string?> background = brain.CompleteJsonAsync("system", "user", CancellationToken.None);
        await Task.Delay(300);

        string answer = await brain.RespondAsync("привет");

        Assert.Null(await background);
        Assert.Equal("Слушаю.", answer);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(6), $"user waited {clock.Elapsed}");
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        try { Directory.Delete(_dir, true); } catch { }
    }
}
