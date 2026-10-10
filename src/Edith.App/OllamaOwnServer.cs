using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Edith.Core.Brain;

namespace Edith.App;

/// <summary>
/// EDITH's own Ollama server (e.g. 127.0.0.1:11435 over C:\AI\OllamaModels on NVMe), separate
/// from the shared one on :11434 with everyone else's models. Started on demand, hidden; it
/// keeps running after EDITH exits so a restart finds the model already loaded.
/// </summary>
public static class OllamaOwnServer
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<bool> EnsureRunningAsync(BrainSettings settings, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OwnServerModelsPath))
            return true;

        if (await IsUpAsync(settings.Endpoint, cancellationToken))
            return true;

        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (await IsUpAsync(settings.Endpoint, cancellationToken))
                return true;

            var uri = new Uri(settings.Endpoint);
            var start = new ProcessStartInfo("ollama", "serve")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                // Not EDITH's own folder: the server outlives EDITH, and a process whose working
                // directory is app\current would keep the installer from replacing it.
                WorkingDirectory = Directory.Exists(settings.OwnServerModelsPath)
                    ? settings.OwnServerModelsPath
                    : Path.GetTempPath(),
            };
            start.Environment["OLLAMA_HOST"] = $"{uri.Host}:{uri.Port}";
            start.Environment["OLLAMA_MODELS"] = settings.OwnServerModelsPath;
            start.Environment["OLLAMA_FLASH_ATTENTION"] = "1";
            start.Environment["OLLAMA_KV_CACHE_TYPE"] = "q8_0";
            start.Environment["OLLAMA_MAX_LOADED_MODELS"] = "1";
            start.Environment["OLLAMA_NUM_PARALLEL"] = "1";
            Process.Start(start)?.Dispose();

            for (int i = 0; i < 40; i++)
            {
                await Task.Delay(250, cancellationToken);
                if (await IsUpAsync(settings.Endpoint, cancellationToken))
                {
                    Edith.Core.Runtime.EdithEvents.Publish("brain.server", "Запущен сервер Ollama EDITH",
                        data: new Dictionary<string, string> { ["endpoint"] = settings.Endpoint, ["models"] = settings.OwnServerModelsPath });
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VoiceDiagnostics.WriteRaw("ollama-own-server-error.txt", ex.ToString());
            return false;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<bool> IsUpAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(endpoint.TrimEnd('/') + "/api/version", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }
}
