using System.Text;
using System.Text.Json;
using Vosk;

namespace Edith.Audio;

public sealed class InterruptRecognizer : IDisposable
{
    private const float SampleRate = 16000.0f;

    private readonly VoskRecognizer _recognizer;
    private readonly object _sync = new();

    private readonly StringBuilder _segments =
        new();

    private float[] _scaledBuffer =
        new float[4096];

    private string _lastPartial =
        string.Empty;

    private bool _active;
    private bool _triggerFired;

    public event Action<string, string>?
        InterruptDetected;

    public InterruptRecognizer(
        Model sharedModel)
    {
        // НИКАКОЙ grammar.
        // Обычный русский recognizer.
        _recognizer =
            new VoskRecognizer(
                sharedModel,
                SampleRate);

        _recognizer.SetMaxAlternatives(0);
        _recognizer.SetWords(false);
    }

    public void StartListening()
    {
        lock (_sync)
        {
            _recognizer.Reset();

            _segments.Clear();

            _lastPartial =
                string.Empty;

            _triggerFired =
                false;

            _active =
                true;
        }
    }

    public void StopListening()
    {
        lock (_sync)
        {
            _active =
                false;

            _triggerFired =
                false;

            _segments.Clear();

            _lastPartial =
                string.Empty;

            _recognizer.Reset();
        }
    }

    public void AcceptAudio(
        float[] samples,
        int count)
    {
        string? triggerToFire =
            null;

        string? textToFire =
            null;

        lock (_sync)
        {
            if (!_active)
                return;

            EnsureBuffer(count);

            for (int i = 0; i < count; i++)
            {
                _scaledBuffer[i] =
                    samples[i] * 32768.0f;
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

                    if (!_triggerFired &&
                        TryFindTrigger(
                            text,
                            _echo,
                            out string trigger))
                    {
                        _triggerFired =
                            true;

                        triggerToFire =
                            trigger;

                        textToFire =
                            text;
                    }
                }

                _lastPartial =
                    string.Empty;
            }
            else
            {
                string partial =
                    Parse(
                        _recognizer.PartialResult(),
                        "partial");

                partial =
                    Normalize(partial);

                if (!string.IsNullOrWhiteSpace(
                        partial))
                {
                    _lastPartial =
                        partial;

                    if (!_triggerFired &&
                        TryFindTrigger(
                            partial,
                            _echo,
                            out string trigger))
                    {
                        _triggerFired =
                            true;

                        triggerToFire =
                            trigger;

                        textToFire =
                            partial;
                    }
                }
            }
        }

        // Callback вне lock.
        if (triggerToFire is not null)
        {
            InterruptDetected?.Invoke(
                triggerToFire,
                textToFire ?? "");
        }
    }

    public string FinishUtterance()
    {
        lock (_sync)
        {
            if (!_active)
                return "";

            string final =
                Parse(
                    _recognizer.FinalResult(),
                    "text");

            final =
                Normalize(final);

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
                    _lastPartial.Trim();
            }

            _recognizer.Reset();

            _segments.Clear();

            _lastPartial =
                string.Empty;

            _triggerFired =
                false;

            return Normalize(result);
        }
    }

    /// <summary>
    /// Words EDITH is saying right now. The mic hears her through the speakers, so "стоп" the
    /// user says lands after her words in the same phrase; a stop word she says herself is not
    /// the user's.
    /// </summary>
    public void SetEchoText(string? spoken)
    {
        lock (_sync)
        {
            _echo = string.IsNullOrWhiteSpace(spoken)
                ? []
                : Normalize(spoken).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        }
    }

    private HashSet<string> _echo = [];

    /// <summary>The stop word in <paramref name="text"/>, ignoring what EDITH herself is saying.</summary>
    public bool FindTrigger(string text, out string trigger)
    {
        HashSet<string> echo;
        lock (_sync)
            echo = _echo;
        return TryFindTrigger(text, echo, out trigger);
    }

    /// <summary>Like <see cref="TryFindTrigger(string, out string)"/>, plus a stop word among the newest words.</summary>
    public static bool TryFindTrigger(
        string text,
        IReadOnlySet<string> echo,
        out string canonicalTrigger)
    {
        if (TryFindTrigger(text, out canonicalTrigger) && !echo.Contains(canonicalTrigger))
            return true;

        string[] words = Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = Math.Max(0, words.Length - 2); i < words.Length; i++)
        {
            if (!echo.Contains(words[i]) && TryMapTrigger(words[i], out canonicalTrigger))
                return true;
        }

        canonicalTrigger = "";
        return false;
    }

    public static bool TryFindTrigger(
        string text,
        out string canonicalTrigger)
    {
        canonicalTrigger = "";

        text =
            Normalize(text);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (WakePhraseMatcher.TryFindWake(
                text,
                out _))
        {
            canonicalTrigger =
                "эдит";

            return true;
        }

        string[] words =
            text.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries);

        if (words.Length == 0)
            return false;

        int start =
            0;

        // "ну стой"
        // "эй стоп"
        // "да хватит"
        if (words.Length > 1 &&
            IsFiller(words[0]))
        {
            start = 1;
        }

        // Ищем только в самом начале.
        // Случайное "стой" в середине чужого разговора
        // не считается обращением.
        int limit =
            Math.Min(
                words.Length,
                start + 2);

        for (int i = start;
             i < limit;
             i++)
        {
            if (TryMapTrigger(
                    words[i],
                    out canonicalTrigger))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryMapTrigger(
        string word,
        out string canonical)
    {
        canonical = "";

        // Vosk может иногда написать "стой" как "стои".
        switch (word)
        {
            case "стой":
            case "стои":
            case "стойте":
                canonical = "стой";
                return true;

            case "стоп":
                canonical = "стоп";
                return true;

            case "остановись":
            case "останови":
            case "остановитесь":
                canonical = "остановись";
                return true;

            case "прекрати":
            case "перестань":
                canonical = "прекрати";
                return true;

            case "хватит":
            case "достаточно":
                canonical = "хватит";
                return true;

            case "тихо":
            case "тише":
                canonical = "тихо";
                return true;

            case "замолчи":
            case "умолкни":
            case "молчи":
            case "помолчи":
                canonical = "замолчи";
                return true;

            case "закройся":
            case "заткнись":
                canonical = "замолчи";
                return true;

            case "отмена":
            case "отбой":
                canonical = "отмена";
                return true;

            // Повторное обращение тоже можно использовать.
            case "эдит":
            case "едит":
            case "эди":
            case "еди":
                canonical = "эдит";
                return true;
        }

        return false;
    }

    private static bool IsFiller(
        string word)
    {
        return word is
            "ну" or
            "эй" or
            "да" or
            "так" or
            "ладно";
    }

    private void AppendSegment(
        string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (_segments.Length > 0)
            _segments.Append(' ');

        _segments.Append(text);
    }

    private static string Parse(
        string json,
        string property)
    {
        if (string.IsNullOrWhiteSpace(json))
            return "";

        try
        {
            using var document =
                JsonDocument.Parse(json);

            if (document.RootElement
                .TryGetProperty(
                    property,
                    out var value))
            {
                return value.GetString() ?? "";
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
        StopListening();
        _recognizer.Dispose();
    }
}
