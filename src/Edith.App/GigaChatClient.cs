using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Edith.Core;
using Edith.Core.Agent;
using Edith.Core.Brain;
using Edith.Core.Runtime;
using Edith.Speech;

namespace Edith.App;

/// <summary>config/cloud.json: the cloud brain in front of the local model.</summary>
public sealed record CloudSettings
{
    public bool Enabled { get; init; }
    public string[] Models { get; init; } = ["GigaChat-2-Max", "GigaChat-2-Pro", "GigaChat-2"];
    public string Scope { get; init; } = "GIGACHAT_API_PERS";
    public string AuthKeyPath { get; init; } = @"C:\EDITH\secrets\gigachat-auth-key.txt";
    public string RootCertPath { get; init; } = @"C:\EDITH\config\russian_trusted_root_ca.cer";
    public int TimeoutSeconds { get; init; } = 20;
    public int MaxTokens { get; init; } = 600;

    public const string DefaultPath = @"C:\EDITH\config\cloud.json";

    /// <summary>Why the last <see cref="Load"/> fell back to "off".</summary>
    public static string? LastError { get; private set; }

    /// <summary>EDITH_CLOUD_CONFIG overrides the path (tests and verify.ps1 turn the cloud off).</summary>
    public static CloudSettings Load(string? path = null)
    {
        path ??= Environment.GetEnvironmentVariable("EDITH_CLOUD_CONFIG") is { Length: > 0 } custom ? custom : DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new CloudSettings();

            string json = File.ReadAllText(path);
            return new CloudSettings
            {
                Enabled = ConfigText.Get(json, "enabled") == "true",
                Scope = ConfigText.Get(json, "scope") ?? "GIGACHAT_API_PERS",
                AuthKeyPath = ConfigText.Get(json, "authKeyPath") ?? @"C:\EDITH\secrets\gigachat-auth-key.txt",
                RootCertPath = ConfigText.Get(json, "rootCertPath") ?? @"C:\EDITH\config\russian_trusted_root_ca.cer",
                TimeoutSeconds = (int)(ConfigText.GetNumber(json, "timeoutSeconds") ?? 20),
                MaxTokens = (int)(ConfigText.GetNumber(json, "maxTokens") ?? 600),
                Models = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                    .RootElement.TryGetProperty("models", out JsonElement m) && m.ValueKind == JsonValueKind.Array
                        ? m.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                        : ["GigaChat-2-Max", "GigaChat-2-Pro", "GigaChat-2"],
            };
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return new CloudSettings();
        }
    }
}

/// <summary>Why the cloud could not answer; the caller falls back to the local model.</summary>
public sealed class CloudUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// GigaChat (Sber) client: OAuth token cached for its 30 minutes, the model list walked down
/// when a model's free quota ends (HTTP 402), TLS checked against the Russian Trusted Root CA
/// from a file, so nothing is installed into Windows.
/// </summary>
public sealed class GigaChatClient
{
    private const string OAuthUrl = "https://ngw.devices.sberbank.ru:9443/api/v2/oauth";
    private const string ChatUrl = "https://gigachat.devices.sberbank.ru/api/v1/chat/completions";

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private readonly HashSet<string> _exhausted = new(StringComparer.OrdinalIgnoreCase);
    private HttpClient? _http;
    private string? _certPath;
    private string? _token;
    private DateTimeOffset _tokenExpires;
    private DateTimeOffset _retryAfter;

    public static GigaChatClient Shared { get; } = new();

    /// <summary>Configured, has a key and is not in a back-off after a network failure.</summary>
    public bool IsReady(CloudSettings settings) =>
        settings.Enabled &&
        LocalSecrets.Exists(settings.AuthKeyPath) &&
        DateTimeOffset.Now >= _retryAfter &&
        settings.Models.Any(m => !_exhausted.Contains(m));

    public string CurrentModel(CloudSettings settings) =>
        settings.Models.FirstOrDefault(m => !_exhausted.Contains(m)) ?? "";

    public async Task<GigaChatProtocol.Reply> ChatAsync(
        CloudSettings settings,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        double temperature,
        CancellationToken cancellationToken,
        Action<string>? onContent = null,
        int? maxTokens = null)
    {
        HttpClient http = Client(settings);

        // One request at a time: that is all the free tier allows.
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            if (maxTokens is { } longer)
                settings = settings with { MaxTokens = longer };
            return await ChatCoreAsync(settings, http, messages, tools, temperature, onContent, cancellationToken);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private async Task<GigaChatProtocol.Reply> ChatCoreAsync(
        CloudSettings settings,
        HttpClient http,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolSpec> tools,
        double temperature,
        Action<string>? onContent,
        CancellationToken cancellationToken)
    {
        foreach (string model in settings.Models.Where(m => !_exhausted.Contains(m)).ToArray())
        {
            bool refreshToken = false, refreshed = false;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                string token = await TokenAsync(settings, http, forceNew: refreshToken, cancellationToken);
                refreshToken = false;
                using var request = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
                {
                    Content = new StringContent(
                        GigaChatProtocol.BuildRequest(model, messages, tools, settings.MaxTokens, temperature, stream: onContent is not null),
                        Encoding.UTF8,
                        "application/json"),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                HttpResponseMessage response;
                string body;
                try
                {
                    if (onContent is null)
                    {
                        response = await http.SendAsync(request, timeout.Token);
                        body = await response.Content.ReadAsStringAsync(timeout.Token);
                    }
                    else
                    {
                        // Streamed: the words are passed on as they arrive.
                        response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                        body = response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync(timeout.Token);
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    BackOff();
                    throw new CloudUnavailableException("GigaChat не ответил вовремя");
                }
                catch (HttpRequestException ex)
                {
                    BackOff();
                    throw new CloudUnavailableException("нет связи с GigaChat: " + ex.Message, ex);
                }

                using (response)
                {
                    int status = (int)response.StatusCode;
                    if (response.IsSuccessStatusCode)
                    {
                        if (onContent is null)
                            return GigaChatProtocol.ParseResponse(body);

                        try
                        {
                            return await ReadStreamAsync(response, onContent, timeout.Token);
                        }
                        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                        {
                            BackOff();
                            throw new CloudUnavailableException("GigaChat оборвал ответ");
                        }
                    }

                    if (status == 401 && !refreshed)
                    {
                        refreshed = refreshToken = true;
                        continue;
                    }

                    // The free tier answers one request at a time, and the key is shared with other
                    // projects: a 429 usually clears in a second, so wait and ask again.
                    if (status == 429 && attempt < 3)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1000 + 700 * attempt), cancellationToken);
                        continue;
                    }

                    if (status == 402)
                    {
                        // Free tokens for this model are over: next model in the list.
                        _exhausted.Add(model);
                        EdithEvents.Publish(EventKinds.Error, $"GigaChat: закончилась бесплатная квота {model}");
                        break;
                    }

                    throw new CloudUnavailableException($"GigaChat HTTP {status}: {Trim(body)}");
                }
            }
        }

        throw new CloudUnavailableException("GigaChat: бесплатная квота всех моделей закончилась");
    }

    private static async Task<GigaChatProtocol.Reply> ReadStreamAsync(HttpResponseMessage response, Action<string> onContent, CancellationToken cancellationToken)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var parser = new GigaChatStreamReader();

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;

            string text = parser.Add(line[5..]);
            if (text.Length > 0)
                onContent(text);
            if (line.Contains("[DONE]", StringComparison.Ordinal))
                break;
        }

        return parser.Build();
    }

    private const string FilesUrl = "https://gigachat.devices.sberbank.ru/api/v1/files";

    /// <summary>
    /// Shows a picture to GigaChat (Max or Pro read images): the file is uploaded, attached to one
    /// question, answered, and deleted again. The picture leaves the PC, so callers use this only
    /// when the owner asked about what is on the screen.
    /// </summary>
    /// <summary>
    /// Draws a picture from a description (GigaChat's built-in painter): the model answers with an image id, the picture
    /// is downloaded and returned as bytes (jpeg). Null with a reason when nothing was drawn.
    /// </summary>
    public async Task<(byte[]? Bytes, string Reason)> GenerateImageAsync(CloudSettings settings, string prompt, CancellationToken cancellationToken)
    {
        HttpClient http = Client(settings);
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            string token = await TokenAsync(settings, http, forceNew: false, cancellationToken);
            foreach (string model in settings.Models.Where(m => !_exhausted.Contains(m) && !m.Equals("GigaChat-2", StringComparison.OrdinalIgnoreCase)))
            {
                var body = JsonSerializer.Serialize(new
                {
                    model,
                    stream = false,
                    max_tokens = 400,
                    function_call = "auto",
                    messages = new object[]
                    {
                        new { role = "system", content = "Ты — Василий Кандинский." },
                        new { role = "user", content = "Нарисуй: " + prompt },
                    },
                });

                using var chat = new HttpRequestMessage(HttpMethod.Post, ChatUrl) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                chat.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage response = await http.SendAsync(chat, cancellationToken);
                string reply = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
                {
                    _exhausted.Add(model);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    return (null, $"облако не нарисовало (HTTP {(int)response.StatusCode})");

                string content = GigaChatProtocol.ParseResponse(reply).Content;
                System.Text.RegularExpressions.Match id = System.Text.RegularExpressions.Regex.Match(content, "<img[^>]*src=\"([^\"]+)\"");
                if (!id.Success)
                    return (null, "облако ответило словами, а не картинкой: " + Trim(content));

                using var download = new HttpRequestMessage(HttpMethod.Get, $"{FilesUrl}/{id.Groups[1].Value}/content");
                download.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                download.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/jpg"));
                using HttpResponseMessage picture = await http.SendAsync(download, cancellationToken);
                if (!picture.IsSuccessStatusCode)
                    return (null, $"картинку не удалось скачать (HTTP {(int)picture.StatusCode})");
                return (await picture.Content.ReadAsByteArrayAsync(cancellationToken), "");
            }

            return (null, "нет модели с бесплатной квотой для рисования");
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public async Task<string> DescribeImageAsync(
        CloudSettings settings,
        byte[] jpeg,
        string question,
        CancellationToken cancellationToken)
    {
        HttpClient http = Client(settings);
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            string token = await TokenAsync(settings, http, forceNew: false, cancellationToken);

            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(jpeg);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            form.Add(file, "file", "screen.jpg");
            form.Add(new StringContent("general"), "purpose");

            using var upload = new HttpRequestMessage(HttpMethod.Post, FilesUrl) { Content = form };
            upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage uploaded = await http.SendAsync(upload, cancellationToken);
            string uploadBody = await uploaded.Content.ReadAsStringAsync(cancellationToken);
            if (!uploaded.IsSuccessStatusCode)
                throw new CloudUnavailableException($"GigaChat не принял картинку (HTTP {(int)uploaded.StatusCode}): {Trim(uploadBody)}");

            string fileId = JsonDocument.Parse(uploadBody).RootElement.GetProperty("id").GetString()!;
            try
            {
                foreach (string model in settings.Models.Where(m => !_exhausted.Contains(m) && !m.Equals("GigaChat-2", StringComparison.OrdinalIgnoreCase)))
                {
                    var body = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(new
                    {
                        model,
                        stream = false,
                        max_tokens = 700,
                        temperature = 0.2,
                        messages = new object[]
                        {
                            new { role = "system", content = "Ты описываешь изображение с экрана компьютера для владельца: по-русски, по делу, 2–5 предложений, без markdown. Если на экране магазин, список, таблица или код — назови главное с точными названиями, ценами и числами. Названия и текст на экране приводи точно. Всплывающее окно с ответом и светящийся шар в углу — это сам ассистент EDITH, их не описывай." },
                            new { role = "user", content = question, attachments = new[] { fileId } },
                        },
                    }))!;

                    using var chat = new HttpRequestMessage(HttpMethod.Post, ChatUrl)
                    {
                        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                    };
                    chat.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using HttpResponseMessage response = await http.SendAsync(chat, cancellationToken);
                    string reply = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
                    {
                        _exhausted.Add(model);
                        continue;
                    }

                    if (!response.IsSuccessStatusCode)
                        throw new CloudUnavailableException($"GigaChat не описал картинку (HTTP {(int)response.StatusCode}): {Trim(reply)}");

                    return GigaChatProtocol.ParseResponse(reply).Content;
                }

                throw new CloudUnavailableException("GigaChat: нет модели с бесплатной квотой для картинок");
            }
            finally
            {
                // Best effort: the picture should not stay on their side.
                try
                {
                    using var delete = new HttpRequestMessage(HttpMethod.Post, $"{FilesUrl}/{fileId}/delete");
                    delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    using HttpResponseMessage _ = await http.SendAsync(delete, CancellationToken.None);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private void BackOff() =>
        _retryAfter = DateTimeOffset.Now.AddMinutes(2);

    private async Task<string> TokenAsync(CloudSettings settings, HttpClient http, bool forceNew, CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!forceNew && _token is not null && DateTimeOffset.Now < _tokenExpires.AddMinutes(-1))
                return _token;

            string key = LocalSecrets.Read(settings.AuthKeyPath)
                ?? throw new CloudUnavailableException("нет ключа GigaChat");

            using var request = new HttpRequestMessage(HttpMethod.Post, OAuthUrl)
            {
                Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("scope", settings.Scope)]),
            };
            request.Headers.TryAddWithoutValidation("Authorization", "Basic " + key);
            request.Headers.Add("RqUID", Guid.NewGuid().ToString());
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

            HttpResponseMessage response;
            string body;
            try
            {
                response = await http.SendAsync(request, timeout.Token);
                body = await response.Content.ReadAsStringAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                BackOff();
                throw new CloudUnavailableException("GigaChat: авторизация не ответила вовремя");
            }
            catch (HttpRequestException ex)
            {
                BackOff();
                throw new CloudUnavailableException("нет связи с GigaChat: " + ex.Message, ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                    throw new CloudUnavailableException($"GigaChat: ключ не принят (HTTP {(int)response.StatusCode}): {Trim(body)}");

                using JsonDocument document = JsonDocument.Parse(body);
                _token = document.RootElement.GetProperty("access_token").GetString();
                _tokenExpires = document.RootElement.TryGetProperty("expires_at", out JsonElement e) && e.TryGetInt64(out long ms)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                    : DateTimeOffset.Now.AddMinutes(29);
                return _token ?? throw new CloudUnavailableException("GigaChat не выдал токен");
            }
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private HttpClient Client(CloudSettings settings)
    {
        if (_http is not null && _certPath == settings.RootCertPath)
            return _http;

        X509Certificate2? root = LoadRoot(settings.RootCertPath);

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                // Windows does not trust Sber's chain by default: accept it only when it
                // builds to the Russian Trusted Root CA from the file, nothing else.
                RemoteCertificateValidationCallback = (_, certificate, chain, errors) =>
                {
                    if (errors == SslPolicyErrors.None)
                        return true;
                    if (root is null || certificate is null || errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                        return false;

                    using var custom = new X509Chain();
                    custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    custom.ChainPolicy.CustomTrustStore.Add(root);
                    custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    if (chain is not null)
                        foreach (X509ChainElement element in chain.ChainElements)
                            custom.ChainPolicy.ExtraStore.Add(element.Certificate);

                    return custom.Build(new X509Certificate2(certificate));
                },
            },
        };

        _certPath = settings.RootCertPath;
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        return _http;
    }

    /// <summary>PEM (as Sber/Gosuslugi ship it) or DER.</summary>
    private static X509Certificate2? LoadRoot(string path)
    {
        if (!File.Exists(path))
            return null;

        string text = File.ReadAllText(path);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : X509CertificateLoader.LoadCertificateFromFile(path);
    }

    private static string Trim(string text) =>
        text.Length <= 300 ? text : text[..300];
}
