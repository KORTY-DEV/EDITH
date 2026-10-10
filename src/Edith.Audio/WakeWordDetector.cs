using System.Text;
using System.Text.Json;
using Edith.Audio.Wake;
using Vosk;

namespace Edith.Audio;

public sealed class WakeWordDetector : IWakeUtterance
{
    private const float SampleRate =
        16000.0f;

    private readonly VoskRecognizer _recognizer;

    private readonly object _sync =
        new();

    private readonly StringBuilder _segments =
        new();

    private float[] _scaledBuffer =
        new float[4096];

    private string _lastPartial =
        string.Empty;

    private bool _wakeRaised;

    private volatile bool _wakeEventsEnabled =
        true;


    // false = детектор только транскрибирует фразу,
    // а wake решает другой (ML) детектор.
    public bool WakeEventsEnabled
    {
        get => _wakeEventsEnabled;
        set => _wakeEventsEnabled = value;
    }


    public event Action<string>?
        WakeWordDetected;

    // Compact runtime diagnostics only; no audio is retained or emitted.
    public string LastPartial
    {
        get
        {
            lock (_sync)
                return _lastPartial;
        }
    }

    public string LastFinal
    {
        get
        {
            lock (_sync)
                return _segments.ToString();
        }
    }


    public WakeWordDetector(
        Model sharedModel)
    {
        _recognizer =
            new VoskRecognizer(
                sharedModel,
                SampleRate);

        _recognizer
            .SetMaxAlternatives(0);

        _recognizer
            .SetWords(false);
    }


    public void AcceptAudio(
        float[] samples,
        int count)
    {
        AcceptAudio(
            samples.AsSpan(0, count));
    }


    public void AcceptAudio(
        ReadOnlySpan<float> samples)
    {
        int count =
            samples.Length;

        string? wakeToFire =
            null;


        lock (_sync)
        {
            EnsureBuffer(count);


            for (int i = 0;
                 i < count;
                 i++)
            {
                _scaledBuffer[i] =
                    samples[i] *
                    32768.0f;
            }


            bool finished =
                _recognizer.AcceptWaveform(
                    _scaledBuffer,
                    count);


            if (finished)
            {
                string text =
                    Parse(
                        _recognizer.Result(),
                        "text");

                text =
                    Normalize(text);


                if (!string.IsNullOrWhiteSpace(
                        text))
                {
                    AppendSegment(text);
                }


                _lastPartial =
                    string.Empty;
            }
            else
            {
                _lastPartial =
                    Normalize(
                        Parse(
                            _recognizer.PartialResult(),
                            "partial"));
            }


            if (_wakeEventsEnabled &&
                !_wakeRaised)
            {
                string current =
                    BuildCurrentText();


                if (WakePhraseMatcher
                    .TryFindWake(
                        current,
                        out string observed))
                {
                    _wakeRaised =
                        true;

                    wakeToFire =
                        observed;
                }
            }
        }


        if (wakeToFire is not null)
        {
            WakeWordDetected?.Invoke(
                wakeToFire);
        }
    }


    public string FinishUtterance()
    {
        lock (_sync)
        {
            string final =
                Normalize(
                    Parse(
                        _recognizer.FinalResult(),
                        "text"));


            if (!string.IsNullOrWhiteSpace(
                    final))
            {
                AppendSegment(final);
            }


            string result =
                _segments
                    .ToString()
                    .Trim();


            if (string.IsNullOrWhiteSpace(
                    result))
            {
                result =
                    _lastPartial;
            }


            InternalReset();


            return
                Normalize(result);
        }
    }


    public void Reset()
    {
        lock (_sync)
        {
            _recognizer.Reset();

            InternalReset(
                resetRecognizer: false);
        }
    }


    private string BuildCurrentText()
    {
        if (_segments.Length == 0)
        {
            return _lastPartial;
        }


        if (string.IsNullOrWhiteSpace(
                _lastPartial))
        {
            return _segments
                .ToString();
        }


        return
            _segments +
            " " +
            _lastPartial;
    }


    private void AppendSegment(
        string text)
    {
        if (string.IsNullOrWhiteSpace(
                text))
        {
            return;
        }


        if (_segments.Length > 0)
        {
            _segments.Append(' ');
        }


        _segments.Append(text);
    }


    private void InternalReset(
        bool resetRecognizer = true)
    {
        if (resetRecognizer)
        {
            _recognizer.Reset();
        }


        _segments.Clear();

        _lastPartial =
            string.Empty;

        _wakeRaised =
            false;
    }


    private static string Parse(
        string json,
        string property)
    {
        if (string.IsNullOrWhiteSpace(
                json))
        {
            return "";
        }


        try
        {
            using var document =
                JsonDocument.Parse(json);


            if (document.RootElement
                .TryGetProperty(
                    property,
                    out JsonElement value))
            {
                return
                    value.GetString() ??
                    "";
            }
        }
        catch
        {
        }


        return "";
    }


    private static string Normalize(
        string text)
    {
        return text
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');
    }


    private void EnsureBuffer(
        int count)
    {
        if (_scaledBuffer.Length >=
            count)
        {
            return;
        }


        _scaledBuffer =
            new float[
                Math.Max(
                    count,
                    _scaledBuffer.Length *
                    2)];
    }


    public void Dispose()
    {
        lock (_sync)
        {
            _recognizer.Dispose();
        }
    }
}
