using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Edith.Speech;

public sealed class YandexSpeechKitTtsService : IDisposable
{
    private const string Endpoint =
        "https://tts.api.cloud.yandex.net/tts/v3/utteranceSynthesis";

    private const uint SND_SYNC =
        0x0000;

    private const uint SND_NODEFAULT =
        0x0002;

    private const uint SND_FILENAME =
        0x00020000;

    private readonly TtsSettings _settings;

    private readonly HttpClient _http =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(5)
        };

    private readonly SemaphoreSlim _gate =
        new(1, 1);

    private readonly object _cancelLock =
        new();

    private CancellationTokenSource?
        _currentCancellation;

    public bool WasStopped { get; private set; }

    public YandexSpeechKitTtsService(
        string keyPath)
        : this(new TtsSettings { ApiKeyPath = keyPath })
    {
    }

    public YandexSpeechKitTtsService(TtsSettings settings)
    {
        _settings = settings.Normalize();
        _http.Timeout = TimeSpan.FromSeconds(_settings.TimeoutSeconds);
    }

    public bool IsConfigured
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.ApiKey)) return true;
            try
            {
                return !string.IsNullOrWhiteSpace(LocalSecrets.Read(_settings.ApiKeyPath));
            }
            catch
            {
                return false;
            }
        }
    }

    public string VoiceName =>
        $"Yandex SpeechKit: {_settings.Voice} / {_settings.Style ?? _settings.Role} ({_settings.SampleRate} Hz)";

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default,
        Action? firstAudioStarted = null,
        Action? requestStarted = null,
        Action? audioReady = null)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Yandex SpeechKit API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(text))
            return;

        WasStopped = false;

        await _gate.WaitAsync(
            cancellationToken);

        CancellationTokenSource linked;

        lock (_cancelLock)
        {
            _currentCancellation?.Dispose();

            linked =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);

            _currentCancellation =
                linked;
        }

        try
        {
            requestStarted?.Invoke();
            bool firstAudioReported = false;
            bool audioReadyReported = false;
            foreach (string chunk in SplitText(text))
            {
                linked.Token.ThrowIfCancellationRequested();

                byte[] wav =
                    await SynthesizeAsync(
                        chunk,
                        linked.Token);

                linked.Token.ThrowIfCancellationRequested();

                string file =
                    Path.Combine(
                        @"C:\EDITH\data\cache\tts",
                        $"{Guid.NewGuid():N}.wav");

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(
                    file,
                    wav,
                    linked.Token);
                if (!audioReadyReported)
                {
                    audioReady?.Invoke();
                    audioReadyReported = true;
                }

                try
                {
                    if (!firstAudioReported)
                    {
                        firstAudioStarted?.Invoke();
                        firstAudioReported = true;
                    }
                    await Task.Run(
                        () =>
                        {
                            linked.Token
                                .ThrowIfCancellationRequested();

                            PlaySound(
                                file,
                                IntPtr.Zero,
                                SND_FILENAME |
                                SND_NODEFAULT |
                                SND_SYNC);
                        },
                        linked.Token);
                }
                finally
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                    }
                }
            }
        }
        finally
        {
            lock (_cancelLock)
            {
                if (ReferenceEquals(
                        _currentCancellation,
                        linked))
                {
                    _currentCancellation =
                        null;
                }
            }

            linked.Dispose();

            _gate.Release();
        }
    }

    private async Task<byte[]>
        SynthesizeAsync(
            string text,
            CancellationToken cancellationToken)
    {
        string apiKey =
            (_settings.ApiKey ?? LocalSecrets.Read(_settings.ApiKeyPath) ?? "").Trim();

        object payload =
            new
            {
                text,

                hints =
                    new object[]
                    {
                        new
                        {
                            voice =
                                _settings.Voice
                        },

                        new
                        {
                            role =
                                _settings.Style ?? _settings.Role
                        },

                        new
                        {
                            speed =
                                _settings.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        }
                    },

                outputAudioSpec =
                    new
                    {
                        containerAudio =
                            new
                            {
                                containerAudioType =
                                    "WAV"
                            }
                    }
            };

        string json =
            JsonSerializer.Serialize(
                payload);

        using var request =
            new HttpRequestMessage(
                HttpMethod.Post,
                Endpoint);

        request.Headers.Authorization =
            new AuthenticationHeaderValue(
                "Api-Key",
                apiKey);

        request.Content =
            new StringContent(
                json,
                Encoding.UTF8,
                "application/json");

        using var response =
            await _http.SendAsync(
                request,
                cancellationToken);

        string body =
            await response.Content
                .ReadAsStringAsync(
                    cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SpeechKit HTTP {(int)response.StatusCode}: {body}");
        }

        using var document =
            JsonDocument.Parse(body);

        JsonElement root =
            document.RootElement;

        JsonElement responseRoot;

        if (root.TryGetProperty(
                "result",
                out JsonElement result))
        {
            responseRoot =
                result;
        }
        else
        {
            responseRoot =
                root;
        }

        if (!responseRoot.TryGetProperty(
                "audioChunk",
                out JsonElement audioChunk))
        {
            throw new InvalidOperationException(
                "SpeechKit response has no audioChunk.");
        }

        if (!audioChunk.TryGetProperty(
                "data",
                out JsonElement data))
        {
            throw new InvalidOperationException(
                "SpeechKit response has no audio data.");
        }

        string? base64 =
            data.GetString();

        if (string.IsNullOrWhiteSpace(
                base64))
        {
            throw new InvalidOperationException(
                "SpeechKit returned empty audio.");
        }

        return Convert.FromBase64String(
            base64);
    }

    private static IEnumerable<string>
        SplitText(
            string text)
    {
        text =
            text.Trim();

        const int max =
            220;

        while (text.Length > max)
        {
            int split =
                text.LastIndexOf(
                    ' ',
                    max);

            if (split < 80)
                split = max;

            yield return
                text[..split]
                    .Trim();

            text =
                text[split..]
                    .Trim();
        }

        if (text.Length > 0)
        {
            yield return text;
        }
    }

    public void Stop()
    {
        WasStopped = true;
        lock (_cancelLock)
        {
            try
            {
                _currentCancellation?
                    .Cancel();
            }
            catch
            {
            }
        }

        // Останавливает текущий WAV.
        PlaySound(
            null,
            IntPtr.Zero,
            0);
    }

    public void Dispose()
    {
        Stop();

        _http.Dispose();
        _gate.Dispose();

        lock (_cancelLock)
        {
            _currentCancellation?
                .Dispose();

            _currentCancellation =
                null;
        }
    }

    [DllImport(
        "winmm.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern bool PlaySound(
        string? pszSound,
        IntPtr hmod,
        uint fdwSound);
}
