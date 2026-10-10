using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Edith.Core.Agent;
using Edith.Core.Brain;

namespace Edith.App;

/// <summary>
/// The model behind the librarian: long answers, no hurry, and never in the way of EDITH. "gigachat" uses the cloud
/// (cancelled the moment the owner asks something); "local" uses a model of EDITH's own Ollama server and frees the
/// video card a couple of minutes after the last call.
/// </summary>
public static class LibraryModel
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(25) };

    public static Func<string, string, CancellationToken, Task<string?>> For(string provider, string localModel, int gpuLayers = -1) =>
        provider.Equals("local", StringComparison.OrdinalIgnoreCase)
            ? (system, user, ct) => LocalAsync(localModel, gpuLayers, system, user, ct)
            : CloudAsync;

    private static async Task<string?> CloudAsync(string system, string user, CancellationToken cancellationToken)
    {
        CloudSettings cloud = CloudSettings.Load();
        if (!GigaChatClient.Shared.IsReady(cloud))
            return null;

        try
        {
            GigaChatProtocol.Reply reply = await GigaChatClient.Shared.ChatAsync(
                cloud, [new ChatMessage("system", system), new ChatMessage("user", user)], [], 0.2, cancellationToken, maxTokens: 3500);
            try
            {
                Directory.CreateDirectory(@"C:\EDITH\data\library");
                File.AppendAllText(@"C:\EDITH\data\library\tokens.csv", $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm},{reply.PromptTokens},{reply.CompletionTokens}{Environment.NewLine}");
            }
            catch
            {
            }

            return reply.Content;
        }
        catch (Exception ex) when (ex is OperationCanceledException or CloudUnavailableException)
        {
            return null;
        }
    }

    private static async Task<string?> LocalAsync(string model, int gpuLayers, string system, string user, CancellationToken cancellationToken)
    {
        // The librarian has its own server (port 11436) over the shared model folder: it neither evicts what another
        // program keeps loaded on :11434 nor needs a second copy of the models in EDITH's own folder.
        BrainSettings settings = BrainService.Shared.CurrentSettings() with
        {
            Endpoint = "http://127.0.0.1:11436",
            OwnServerModelsPath = Environment.GetEnvironmentVariable("OLLAMA_MODELS") is { Length: > 0 } shared ? shared : @"D:\AI\OllamaModels",
        };
        if (!await OllamaOwnServer.EnsureRunningAsync(settings, cancellationToken))
            return null;

        var body = new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "2m",
            messages = new[] { new { role = "system", content = system }, new { role = "user", content = user } },
            options = gpuLayers >= 0
                ? (object)new { temperature = 0.2, num_ctx = 12288, num_predict = 2400, num_gpu = gpuLayers }
                : new { temperature = 0.2, num_ctx = 12288, num_predict = 2400, num_gpu = -1 },
        };

        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await Http.PostAsync(settings.Endpoint.TrimEnd('/') + "/api/chat", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.GetProperty("message").GetProperty("content").GetString();
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or JsonException)
        {
            return null;
        }
    }
}
