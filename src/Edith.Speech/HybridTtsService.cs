using System.Diagnostics;

namespace Edith.Speech;

public sealed record TtsDiagnostic(
    string Phase,
    string Provider,
    int Characters,
    int Words,
    double ElapsedMilliseconds,
    double? SynthesisMilliseconds = null,
    double? PlaybackMilliseconds = null);

public sealed class HybridTtsService : IDisposable
{
    private readonly WindowsTtsService _fallback;
    private readonly YandexSpeechKitTtsService _cloud;
    private readonly SileroTtsService _local;
    private readonly TtsSettings _settings;

    public HybridTtsService(string speechKitKeyPath)
        : this(new TtsSettings { Provider = "yandex", ApiKeyPath = speechKitKeyPath }) { }

    public HybridTtsService(TtsSettings settings)
    {
        _settings = settings.Normalize();
        _fallback = new WindowsTtsService();
        _cloud = new YandexSpeechKitTtsService(_settings);
        _local = new SileroTtsService(_settings);
    }

    public string SelectedVoice
    {
        get
        {
            if (LocalCandidate && _local.IsConfigured) return _local.VoiceName;
            if (CloudCandidate && _cloud.IsConfigured) return _cloud.VoiceName;
            return _fallback.SelectedVoice + " [Windows fallback]";
        }
    }

    public string LastProvider { get; private set; } = "windows";
    public event Action? FirstAudioStarted;
    public event Action? TtsRequestStarted;
    public event Action? TtsAudioReady;
    public event Action<TtsDiagnostic>? Diagnostic;

    public Task<int> PrerenderPhrasesAsync(CancellationToken cancellationToken = default) =>
        _local.IsConfigured ? _local.PrerenderAsync(PhraseCache.Phrases, cancellationToken) : Task.FromResult(0);

    public Task<int> RenderVoiceSamplesAsync(CancellationToken cancellationToken = default) =>
        _local.IsConfigured ? _local.RenderSamplesAsync(cancellationToken) : Task.FromResult(0);

    public Task RenderToFileAsync(string text, string path, CancellationToken cancellationToken = default) =>
        _local.RenderToFileAsync(text, path, cancellationToken);

    public string VoiceSamplePath(string voice) => _local.SamplePath(voice);

    public Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        LocalCandidate ? _local.WarmUpAsync(cancellationToken) : Task.CompletedTask;

    private bool LocalCandidate =>
        _settings.Provider is "auto" or "local";

    private bool CloudCandidate =>
        _settings.Provider == "yandex";

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default, long session = 0)
    {
        string speechText = text ?? string.Empty;
        Stopwatch clock = Stopwatch.StartNew();
        int characters = speechText.Length;
        int words = string.IsNullOrWhiteSpace(speechText) ? 0 : speechText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        double? requestAt = null;
        double? audioReadyAt = null;
        double? firstAudioAt = null;

        void RequestStarted()
        {
            TtsRequestStarted?.Invoke();
            requestAt = clock.Elapsed.TotalMilliseconds;
            Diagnostic?.Invoke(new("request-start", LastProvider, characters, words, clock.Elapsed.TotalMilliseconds));
        }
        void AudioReady()
        {
            TtsAudioReady?.Invoke();
            audioReadyAt = clock.Elapsed.TotalMilliseconds;
            Diagnostic?.Invoke(new("audio-ready", LastProvider, characters, words, clock.Elapsed.TotalMilliseconds));
        }
        void FirstAudio()
        {
            FirstAudioStarted?.Invoke();
            firstAudioAt = clock.Elapsed.TotalMilliseconds;
            Diagnostic?.Invoke(new("first-audio", LastProvider, characters, words, clock.Elapsed.TotalMilliseconds));
        }
        void Complete()
        {
            Diagnostic?.Invoke(new("complete", LastProvider, characters, words, clock.Elapsed.TotalMilliseconds,
                requestAt.HasValue && audioReadyAt.HasValue ? audioReadyAt - requestAt : null,
                firstAudioAt.HasValue ? clock.Elapsed.TotalMilliseconds - firstAudioAt : null));
        }

        if (LocalCandidate && _local.IsConfigured)
        {
            LastProvider = "local";
            try
            {
                await _local.SpeakAsync(speechText, cancellationToken, FirstAudio, RequestStarted, AudioReady, session: session);
                Complete();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _local.WasStopped)
            {
                throw;
            }
            catch
            {
                LastProvider = "windows";
            }
        }

        if (CloudCandidate)
        {
            if (_cloud.IsConfigured)
            {
                LastProvider = "yandex";
                try
                {
                    await _cloud.SpeakAsync(SpeechPronunciation.Apply(speechText, "yandex"), cancellationToken, FirstAudio, RequestStarted, AudioReady);
                    Complete();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _cloud.WasStopped)
                {
                    throw;
                }
                catch
                {
                    // Explicit Yandex mode may degrade to local and then Windows.
                    LastProvider = "windows";
                }
            }

            if (_local.IsConfigured)
            {
                LastProvider = "local";
                try
                {
                    await _local.SpeakAsync(speechText, cancellationToken, FirstAudio, RequestStarted, AudioReady, session: session);
                    Complete();
                    return;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _local.WasStopped)
                {
                    throw;
                }
                catch
                {
                    LastProvider = "windows";
                }
            }
        }

        LastProvider = "windows";
        await _fallback.SpeakAsync(speechText, cancellationToken, FirstAudio, RequestStarted, AudioReady);
        Complete();
    }

    /// <summary>
    /// Sentences as they come (local voice, pipelined). When the local voice is not available the
    /// sentences are collected and spoken as one text, as before.
    /// </summary>
    public async Task SpeakStreamAsync(System.Threading.Channels.ChannelReader<string> sentences, CancellationToken cancellationToken, long session = 0)
    {
        if (LocalCandidate && _local.IsConfigured)
        {
            LastProvider = "local";
            try
            {
                await _local.SpeakStreamAsync(sentences, cancellationToken, FirstAudioStarted, session);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _local.WasStopped)
            {
                throw;
            }
            catch
            {
                // fall through: the rest is spoken by the ordinary path
            }
        }

        var rest = new System.Text.StringBuilder();
        await foreach (string sentence in sentences.ReadAllAsync(cancellationToken))
            rest.Append(sentence).Append(' ');
        if (rest.Length > 0)
            await SpeakAsync(rest.ToString().Trim(), cancellationToken, session);
    }

    public void Stop()
    {
        _local.Stop();
        _cloud.Stop();
        _fallback.Stop();
    }

    public void Dispose()
    {
        _local.Dispose();
        _cloud.Dispose();
        _fallback.Dispose();
    }
}
