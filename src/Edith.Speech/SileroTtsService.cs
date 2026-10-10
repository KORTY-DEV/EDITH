using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Edith.Speech;

public sealed class SileroTtsService : IDisposable
{
    private const uint SND_SYNC = 0x0000;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_FILENAME = 0x00020000;

    private readonly TtsSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private readonly SemaphoreSlim _workerStartGate = new(1, 1);
    private readonly object _workerLock = new();
    private Process? _worker;
    private StreamWriter? _workerInput;
    private CancellationTokenSource? _currentCancellation;
    private int _requestId;

    public SileroTtsService(TtsSettings settings)
    {
        _settings = settings.Normalize();
    }

    public bool IsConfigured =>
        _settings.LocalEngine.Equals("silero", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(_settings.LocalModelPath) &&
        File.Exists(_settings.LocalPythonPath) &&
        File.Exists(_settings.LocalWorkerPath);

    public bool WasStopped { get; private set; }

    public string VoiceName =>
        $"Silero v5_ru: {_settings.LocalVoice} / CPU / {_settings.SampleRate} Hz";

    /// <summary>Starts and loads the worker without producing audio.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;

        Stopwatch clock = Stopwatch.StartNew();
        bool wasWarm;
        lock (_workerLock)
            wasWarm = _worker is { HasExited: false } && _workerInput is not null;
        try
        {
            await EnsureWorkerAsync(cancellationToken);
            WriteWorkerDiagnostic("worker-ready", clock.Elapsed.TotalMilliseconds, wasWarm ? "warm" : "cold");

            // The first real synthesis took 1.7 s against 0.2 s later (the model warms on first use):
            // pay that at start-up with a short and a longer phrase, nothing is played.
            if (!wasWarm)
            {
                foreach (string phrase in new[] { "<speak>Готова.</speak>", "<speak>Проверяю систему, это займёт пару секунд, всё в порядке.</speak>" })
                {
                    string file = Path.Combine(@"C:\EDITH\data\cache\tts", $"warmup-{Guid.NewGuid():N}.wav");
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await SynthesizeChunkAsync(Interlocked.Increment(ref _requestId), phrase, _settings.LocalVoice, file, cancellationToken);
                    try { File.Delete(file); } catch { }
                }

                WriteWorkerDiagnostic("synthesis-warm", clock.Elapsed.TotalMilliseconds, "warmed");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WriteWorkerDiagnostic("warmup-failed", clock.Elapsed.TotalMilliseconds, ex.GetType().Name);
        }
    }

    public const string SampleText =
        "Добрый вечер, сэр. Сегодня вы работали над проектом, а завтра я напомню о встрече. Как вам мой голос?";

    public static readonly string[] SampleVoices = ["xenia", "kseniya", "baya", "eugene"];

    public string SamplePath(string voice) =>
        Path.Combine(@"C:\EDITH\data\voice-samples", $"{voice}-{_settings.LocalRatePercent}{(_settings.LocalPauses ? "p" : "")}.wav");

    /// <summary>One full-sentence sample per voice, to choose by ear; returns how many were new.</summary>
    public async Task<int> RenderSamplesAsync(CancellationToken cancellationToken = default)
    {
        int made = 0;
        Directory.CreateDirectory(Path.GetDirectoryName(SamplePath("x"))!);
        foreach (string voice in SampleVoices)
        {
            string target = SamplePath(voice);
            if (File.Exists(target))
                continue;

            string speech = SpeechPronunciation.Apply(SampleText, "local", _settings.LocalRatePercent, _settings.LocalPauses);
            string temp = target + ".part";
            await _gate.WaitAsync(cancellationToken);
            try
            {
                await SynthesizeChunkAsync(Interlocked.Increment(ref _requestId), speech, voice, temp, cancellationToken);
            }
            finally
            {
                _gate.Release();
            }

            File.Move(temp, target, overwrite: true);
            made++;
        }

        return made;
    }

    /// <summary>Records the fixed phrases into <see cref="PhraseCache"/> with the current voice; returns how many were new.</summary>
    public async Task<int> PrerenderAsync(IEnumerable<string> phrases, CancellationToken cancellationToken = default)
    {
        int made = 0;
        Directory.CreateDirectory(PhraseCache.Folder);
        foreach (string phrase in phrases)
        {
            IReadOnlyList<string> chunks = SileroTextChunker.Split(phrase);
            if (chunks.Count != 1)
                continue; // only one-piece phrases: the cache is looked up per chunk

            string speech = SpeechPronunciation.Apply(chunks[0], "local", _settings.LocalRatePercent, _settings.LocalPauses);
            string target = PhraseCache.PathFor(_settings.LocalVoice, _settings.Speed, speech);
            if (File.Exists(target))
                continue;

            string temp = target + ".part";
            await SynthesizeChunkAsync(Interlocked.Increment(ref _requestId), speech, _settings.LocalVoice, temp, cancellationToken);
            File.Move(temp, target, overwrite: true);
            made++;
        }

        return made;
    }

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default,
        Action? firstAudioStarted = null,
        Action? requestStarted = null,
        Action? audioReady = null,
        string? voiceOverride = null,
        long session = 0)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Silero local model, Python runtime or worker is not configured.");
        if (string.IsNullOrWhiteSpace(text)) return;

        WasStopped = false;
        await _gate.WaitAsync(cancellationToken);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_workerLock) _currentCancellation = linked;

        try
        {
            requestStarted?.Invoke();
            bool firstReported = false;
            bool readyReported = false;
            string voice = string.IsNullOrWhiteSpace(voiceOverride) ? _settings.LocalVoice : voiceOverride.Trim().ToLowerInvariant();
            IReadOnlyList<string> chunks = SileroTextChunker.Split(text);
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                string chunk = chunks[chunkIndex];
                string speechChunk = SpeechPronunciation.Apply(chunk, "local", _settings.LocalRatePercent, _settings.LocalPauses);
                linked.Token.ThrowIfCancellationRequested();
                string file = Path.Combine(@"C:\EDITH\data\cache\tts", $"silero-{Guid.NewGuid():N}.wav");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    long requestId = Interlocked.Increment(ref _requestId);
                    DateTimeOffset synthesisStarted = DateTimeOffset.Now;
                    WriteChunkDiagnostic(session, requestId, chunkIndex, chunks.Count, "synthesis-start", speechChunk);
                    await SynthesizeChunkAsync(requestId, speechChunk, voice, file, linked.Token);
                    WriteChunkDiagnostic(session, requestId, chunkIndex, chunks.Count, "synthesis-complete", speechChunk,
                        (DateTimeOffset.Now - synthesisStarted).TotalMilliseconds);
                    linked.Token.ThrowIfCancellationRequested();

                    if (!readyReported)
                    {
                        audioReady?.Invoke();
                        readyReported = true;
                    }
                    if (!firstReported)
                    {
                        firstAudioStarted?.Invoke();
                        firstReported = true;
                    }

                    await _playbackGate.WaitAsync(linked.Token);
                    try
                    {
                        DateTimeOffset playbackStarted = DateTimeOffset.Now;
                        WriteChunkDiagnostic(session, requestId, chunkIndex, chunks.Count, "playback-start", chunk);
                        // NAudio, not PlaySound: stopping PlaySound from another thread waited for the
                        // chunk to finish (a "стоп" was recognised 1 s in and the voice ran 6 s more).
                        await _player.PlayAsync(file, linked.Token);
                        WriteChunkDiagnostic(session, requestId, chunkIndex, chunks.Count, "playback-complete", chunk,
                            (DateTimeOffset.Now - playbackStarted).TotalMilliseconds);
                    }
                    finally
                    {
                        _playbackGate.Release();
                    }
                }
                finally
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        finally
        {
            lock (_workerLock)
            {
                if (ReferenceEquals(_currentCancellation, linked)) _currentCancellation = null;
            }
            _gate.Release();
        }
    }

    /// <summary>
    /// Speaks sentences as they arrive. While one plays, the next is already being synthesised, so
    /// sentences follow each other without the gap a whole-text request has between its chunks.
    /// </summary>
    public async Task SpeakStreamAsync(
        System.Threading.Channels.ChannelReader<string> sentences,
        CancellationToken cancellationToken,
        Action? firstAudioStarted = null,
        long session = 0)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Silero local model, Python runtime or worker is not configured.");

        WasStopped = false;
        await _gate.WaitAsync(cancellationToken);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_workerLock) _currentCancellation = linked;

        var ready = System.Threading.Channels.Channel.CreateUnbounded<string>();
        string voice = _settings.LocalVoice;

        Task producer = Task.Run(async () =>
        {
            try
            {
                int index = 0;
                await foreach (string sentence in sentences.ReadAllAsync(linked.Token))
                {
                    foreach (string chunk in SileroTextChunker.Split(sentence))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        string speechChunk = SpeechPronunciation.Apply(chunk, "local", _settings.LocalRatePercent, _settings.LocalPauses);
                        string file = Path.Combine(@"C:\EDITH\data\cache\tts", $"silero-{Guid.NewGuid():N}.wav");
                        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                        long requestId = Interlocked.Increment(ref _requestId);
                        DateTimeOffset started = DateTimeOffset.Now;
                        await SynthesizeChunkAsync(requestId, speechChunk, voice, file, linked.Token);
                        WriteChunkDiagnostic(session, requestId, index++, -1, "stream-synthesis-complete", speechChunk,
                            (DateTimeOffset.Now - started).TotalMilliseconds);
                        await ready.Writer.WriteAsync(file, linked.Token);
                    }
                }

                ready.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                ready.Writer.TryComplete(ex);
            }
        });

        try
        {
            bool first = true;
            await foreach (string file in ready.Reader.ReadAllAsync(linked.Token))
            {
                try
                {
                    if (first)
                    {
                        first = false;
                        firstAudioStarted?.Invoke();
                    }

                    await _playbackGate.WaitAsync(linked.Token);
                    try
                    {
                        await _player.PlayAsync(file, linked.Token);
                    }
                    finally
                    {
                        _playbackGate.Release();
                    }
                }
                finally
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        finally
        {
            linked.Cancel(); // stops the producer when we ended early (stop word)
            try { await producer; } catch { }
            while (ready.Reader.TryRead(out string? left))
                try { File.Delete(left); } catch { }

            lock (_workerLock)
            {
                if (ReferenceEquals(_currentCancellation, linked)) _currentCancellation = null;
            }
            _gate.Release();
        }
    }

    private static void WriteChunkDiagnostic(long session, long requestId, int chunkIndex, int chunkCount,
        string phase, string chunk, double? elapsedMilliseconds = null)
    {
        try
        {
            Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
            string safeChunk = chunk.Replace("\r", " ").Replace("\n", " ");
            File.AppendAllText(@"C:\EDITH\data\diagnostics\tts-chunks.txt",
                $"{DateTimeOffset.Now:O} session={session} request={requestId} chunk={chunkIndex + 1}/{chunkCount} phase={phase}" +
                (elapsedMilliseconds.HasValue ? $" elapsed-ms={elapsedMilliseconds.Value:F1}" : "") +
                $" text={safeChunk}{Environment.NewLine}");
        }
        catch
        {
            // Diagnostics must never interrupt speech.
        }
    }

    private async Task SynthesizeChunkAsync(long id, string text, string voice, string output, CancellationToken cancellationToken)
    {
        // A phrase EDITH says often is played from its recording.
        if (PhraseCache.TryCopy(voice, _settings.Speed, text, output))
            return;

        await EnsureWorkerAsync(cancellationToken);
        StreamWriter input;
        Process worker;
        lock (_workerLock)
        {
            input = _workerInput ?? throw new InvalidOperationException("Silero worker input is unavailable.");
            worker = _worker ?? throw new InvalidOperationException("Silero worker is unavailable.");
        }

        await input.WriteLineAsync(JsonSerializer.Serialize(new
        {
            op = "synthesize",
            id,
            text,
            voice,
            sample_rate = _settings.SampleRate,
            speed = _settings.Speed,
            output
        }));
        await input.FlushAsync(cancellationToken);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null) throw new InvalidOperationException("Silero worker exited before producing audio.");
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            string? eventName = root.TryGetProperty("event", out JsonElement eventValue) ? eventValue.GetString() : null;
            if (eventName == "done" && root.TryGetProperty("id", out JsonElement responseId) && responseId.GetInt64() == id)
            {
                if (!File.Exists(output)) throw new InvalidOperationException("Silero worker returned no WAV file.");
                return;
            }
            if (eventName == "error" && (!root.TryGetProperty("id", out JsonElement errorId) || errorId.GetInt64() == id))
                throw new InvalidOperationException(root.TryGetProperty("error", out JsonElement error) ? error.GetString() : "Silero worker error.");
        }
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        await _workerStartGate.WaitAsync(cancellationToken);
        try
        {
        lock (_workerLock)
        {
            if (_worker is { HasExited: false } && _workerInput is not null) return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = _settings.LocalPythonPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(_settings.LocalWorkerPath) ?? @"C:\EDITH"
        };
        startInfo.ArgumentList.Add(_settings.LocalWorkerPath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(_settings.LocalModelPath);
        startInfo.ArgumentList.Add("--threads");
        startInfo.ArgumentList.Add(_settings.LocalNumThreads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(_settings.LocalAccentDir) && Directory.Exists(_settings.LocalAccentDir))
        {
            startInfo.ArgumentList.Add("--accent-dir");
            startInfo.ArgumentList.Add(_settings.LocalAccentDir);
        }

        Process worker = new() { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!worker.Start()) throw new InvalidOperationException("Failed to start Silero worker.");
            worker.ErrorDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                try
                {
                    Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
                    File.AppendAllText(@"C:\EDITH\data\diagnostics\silero-worker.log", $"{DateTimeOffset.Now:O} {args.Data}{Environment.NewLine}");
                }
                catch { }
            };
            worker.BeginErrorReadLine();

            string? readyLine = await worker.StandardOutput.ReadLineAsync(cancellationToken);
            if (readyLine is null) throw new InvalidOperationException("Silero worker did not become ready.");
            using (JsonDocument ready = JsonDocument.Parse(readyLine))
            {
                if (!ready.RootElement.TryGetProperty("event", out JsonElement eventName) || eventName.GetString() != "ready")
                    throw new InvalidOperationException("Silero worker returned an invalid ready message.");

                bool accent = ready.RootElement.TryGetProperty("accent", out JsonElement a) && a.ValueKind == JsonValueKind.True;
                string accentError = ready.RootElement.TryGetProperty("accent_error", out JsonElement e) ? e.GetString() ?? "" : "";
                try
                {
                    Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
                    File.AppendAllText(@"C:\EDITH\data\diagnostics\silero-worker.log",
                        $"{DateTimeOffset.Now:O} ready accent={accent} {accentError}{Environment.NewLine}");
                }
                catch { }
            }

            lock (_workerLock)
            {
                _worker = worker;
                _workerInput = worker.StandardInput;
            }
        }
        catch
        {
            try
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: true);
            }
            catch { }
            worker.Dispose();
            throw;
        }
        }
        finally
        {
            _workerStartGate.Release();
        }
    }

    private static void WriteWorkerDiagnostic(string phase, double elapsedMilliseconds, string? detail = null)
    {
        try
        {
            Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
            File.AppendAllText(@"C:\EDITH\data\diagnostics\tts-worker.txt",
                $"{DateTimeOffset.Now:O} phase={phase} elapsed-ms={elapsedMilliseconds:F1}" +
                (string.IsNullOrWhiteSpace(detail) ? "" : $" detail={detail}") + Environment.NewLine);
        }
        catch { }
    }

    public void Stop()
    {
        WasStopped = true;
        lock (_workerLock)
        {
            try { _currentCancellation?.Cancel(); } catch { }
            // Keep a healthy worker resident.  Cancelling a spoken request
            // must not force the next short direct answer through a cold
            // model load; stale worker replies are ignored by request id.
            if (_worker is { HasExited: true })
            {
                _workerInput = null;
                _worker = null;
            }
        }
        _player.Stop();
    }

    private readonly WavPlayer _player = new();

    public void Dispose()
    {
        WasStopped = true;
        lock (_workerLock)
        {
            bool idle = _currentCancellation is null;
            try { _currentCancellation?.Cancel(); } catch { }

            if (idle && _worker is { HasExited: false } && _workerInput is not null)
            {
                try
                {
                    _workerInput.WriteLine(JsonSerializer.Serialize(new { op = "shutdown" }));
                    _workerInput.Flush();
                    _worker.WaitForExit(500);
                }
                catch { }
            }

            try
            {
                if (_worker is { HasExited: false }) _worker.Kill(entireProcessTree: true);
            }
            catch { }
            _workerInput = null;
            _worker = null;
        }
        _player.Stop();
        _gate.Dispose();
        _playbackGate.Dispose();
        _workerStartGate.Dispose();
    }

}

public static class SileroTextChunker
{
    public static IReadOnlyList<string> Split(string text, int maxCharacters = 280)
    {
        text = text.Trim();
        if (text.Length == 0) return [];
        var chunks = new List<string>();
        string current = "";
        foreach (System.Text.RegularExpressions.Match sentence in System.Text.RegularExpressions.Regex.Matches(text, @"[^.!?;]+[.!?;]?"))
        {
            string part = sentence.Value.Trim();
            if (part.Length == 0) continue;
            if (current.Length > 0 && current.Length + 1 + part.Length > maxCharacters)
            {
                chunks.Add(current);
                current = "";
            }
            if (part.Length <= maxCharacters)
            {
                current = current.Length == 0 ? part : current + " " + part;
            }
            else
            {
                foreach (string word in part.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (current.Length > 0 && current.Length + 1 + word.Length > maxCharacters)
                    {
                        chunks.Add(current);
                        current = "";
                    }
                    current = current.Length == 0 ? word : current + " " + word;
                }
            }
        }
        if (current.Length > 0) chunks.Add(current);
        return chunks;
    }
}
