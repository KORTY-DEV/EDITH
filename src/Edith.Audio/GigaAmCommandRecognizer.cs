using System.Diagnostics;
using System.Text.Json;
using SherpaOnnx;

namespace Edith.Audio;

/// <summary>What the voice pipeline needs from a command recognizer.</summary>
public interface ICommandRecognizer : IDisposable
{
    void StartSession();

    void CancelSession();

    /// <summary>16 kHz mono samples in [-1, 1].</summary>
    void AcceptAudio(float[] samples, int count);

    CommandTranscript FinishSessionDetailed();
}

public sealed record SttSettings
{
    /// <summary>"gigaam" (default) or "vosk".</summary>
    public string Engine { get; init; } = "gigaam";

    public string ModelDirectory { get; init; } =
        @"C:\EDITH\models\stt\sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16";

    public int Threads { get; init; } = 2;

    /// <summary>Acoustic-only wakes (custom / custom-v3) must also be heard as "эдит" by GigaAM.</summary>
    public bool ConfirmWake { get; init; } = true;

    public static SttSettings Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<SttSettings>(
                      File.ReadAllText(path),
                      new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                  ?? new SttSettings()
                : new SttSettings();
        }
        catch
        {
            return new SttSettings();
        }
    }
}

/// <summary>
/// GigaAM v3 (Sber, CTC, int8) through sherpa-onnx: offline, CPU only, ~0.2 s for a
/// 3 s command on two threads. The whole utterance is buffered and decoded at speech end.
/// </summary>
public sealed class GigaAmCommandRecognizer : ICommandRecognizer
{
    public const string EngineName = "gigaam-v3";

    private const int SampleRate = 16000;
    private const int MaxSamples = SampleRate * 30;

    private readonly OfflineRecognizer _recognizer;
    private readonly object _gate = new();
    private readonly List<float> _buffer = new(SampleRate * 8);
    private bool _active;

    public GigaAmCommandRecognizer(SttSettings settings)
    {
        string model = Path.Combine(settings.ModelDirectory, "model.int8.onnx");
        string tokens = Path.Combine(settings.ModelDirectory, "tokens.txt");
        if (!File.Exists(model) || !File.Exists(tokens))
            throw new FileNotFoundException("GigaAM model not found", model);

        var config = new OfflineRecognizerConfig();
        config.ModelConfig.NeMoCtc.Model = model;
        config.ModelConfig.Tokens = tokens;
        config.ModelConfig.NumThreads = Math.Clamp(settings.Threads, 1, 8);
        config.ModelConfig.Provider = "cpu";
        config.DecodingMethod = "greedy_search";

        _recognizer = new OfflineRecognizer(config);
    }

    public void StartSession()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _active = true;
        }
    }

    public void CancelSession()
    {
        lock (_gate)
        {
            _buffer.Clear();
            _active = false;
        }
    }

    public void AcceptAudio(float[] samples, int count)
    {
        lock (_gate)
        {
            if (!_active || _buffer.Count >= MaxSamples)
                return;

            int take = Math.Min(count, MaxSamples - _buffer.Count);
            for (int i = 0; i < take; i++)
                _buffer.Add(samples[i]);
        }
    }

    public CommandTranscript FinishSessionDetailed()
    {
        float[] audio;
        lock (_gate)
        {
            if (!_active)
                return new CommandTranscript("", "", EngineName, null);

            _active = false;
            audio = _buffer.ToArray();
            _buffer.Clear();
        }

        return Decode(audio);
    }

    public CommandTranscript Decode(float[] audio)
    {
        if (audio.Length < SampleRate / 5)
            return new CommandTranscript("", "", EngineName, null);

        using OfflineStream stream = _recognizer.CreateStream();
        stream.AcceptWaveform(SampleRate, audio);
        _recognizer.Decode(stream);

        string raw = stream.Result.Text.Trim();
        return new CommandTranscript(raw, raw.ToLowerInvariant().Replace('ё', 'е'), EngineName, null);
    }

    public void Dispose() => _recognizer.Dispose();
}

/// <summary>
/// GigaAM first, Vosk alongside as the safety net: both hear the same audio; an empty or
/// failed GigaAM result falls back to Vosk, so a broken model never costs a command.
/// </summary>
public sealed class HybridCommandRecognizer(ICommandRecognizer primary, ICommandRecognizer fallback) : ICommandRecognizer
{
    public string? LastError { get; private set; }

    public void StartSession()
    {
        primary.StartSession();
        fallback.StartSession();
    }

    public void CancelSession()
    {
        primary.CancelSession();
        fallback.CancelSession();
    }

    public void AcceptAudio(float[] samples, int count)
    {
        primary.AcceptAudio(samples, count);
        fallback.AcceptAudio(samples, count);
    }

    public CommandTranscript FinishSessionDetailed()
    {
        CommandTranscript backup = fallback.FinishSessionDetailed();
        try
        {
            CommandTranscript main = primary.FinishSessionDetailed();
            LastError = null;
            return string.IsNullOrWhiteSpace(main.NormalizedText) ? backup : main;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return backup;
        }
    }

    public void Dispose()
    {
        primary.Dispose();
        fallback.Dispose();
    }
}
