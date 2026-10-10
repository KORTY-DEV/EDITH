using System.Text.Json;
using Vosk;

namespace Edith.Audio;

public sealed record CommandTranscript(
    string RawText,
    string NormalizedText,
    string Engine,
    double? Confidence);

public sealed class CommandRecognizer : ICommandRecognizer
{
    private const float SampleRate = 16000.0f;

    private readonly VoskRecognizer _recognizer;

    private float[] _scaledBuffer =
        new float[8192];

    private bool _active;

    // The microphone thread feeds audio while the command thread resets/finishes: native Vosk must not be entered by both.
    private readonly object _gate = new();

    public CommandRecognizer(
        Model sharedModel)
    {
        _recognizer =
            new VoskRecognizer(
                sharedModel,
                SampleRate);

        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(true);
    }

    public void StartSession()
    {
        lock (_gate)
        {
            _recognizer.Reset();
            _active = true;
        }
    }

    public void CancelSession()
    {
        lock (_gate)
        {
            _active = false;
            _recognizer.Reset();
        }
    }

    public void AcceptAudio(
        float[] samples,
        int count)
    {
        lock (_gate)
        {
            if (!_active)
                return;

            EnsureBuffer(count);

            for (int i = 0; i < count; i++)
            {
                _scaledBuffer[i] =
                    samples[i] * 32768.0f;
            }

            _recognizer.AcceptWaveform(
                _scaledBuffer,
                count);
        }
    }

    public string FinishSession()
    {
        return FinishSessionDetailed().NormalizedText;
    }

    public CommandTranscript FinishSessionDetailed()
    {
        string json;
        lock (_gate)
        {
            if (!_active)
                return new CommandTranscript("", "", "vosk-command", null);

            _active = false;
            json = _recognizer.FinalResult();
            _recognizer.Reset();
        }

        return ParseText(json);
    }

    private static CommandTranscript ParseText(
        string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new CommandTranscript("", "", "vosk-command", null);

        try
        {
            using var document =
                JsonDocument.Parse(json);

            string raw = "";
            if (document.RootElement.TryGetProperty(
                    "text",
                    out var text))
            {
                raw = text.GetString() ?? "";
            }

            double? confidence = null;
            if (document.RootElement.TryGetProperty("result", out JsonElement words) && words.ValueKind == JsonValueKind.Array)
            {
                double[] values = words.EnumerateArray()
                    .Where(x => x.TryGetProperty("conf", out JsonElement conf) && conf.TryGetDouble(out _))
                    .Select(x => x.GetProperty("conf").GetDouble())
                    .ToArray();
                if (values.Length > 0) confidence = values.Average();
            }

            return new CommandTranscript(raw, Normalize(raw), "vosk-command", confidence);
        }
        catch
        {
        }

        return new CommandTranscript("", "", "vosk-command", null);
    }

    private static string Normalize(
        string value)
    {
        return value
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');
    }

    private void EnsureBuffer(
        int count)
    {
        if (_scaledBuffer.Length >= count)
            return;

        _scaledBuffer =
            new float[
                Math.Max(
                    count,
                    _scaledBuffer.Length * 2)];
    }

    public void Dispose()
    {
        lock (_gate)
            _recognizer.Dispose();
    }
}
