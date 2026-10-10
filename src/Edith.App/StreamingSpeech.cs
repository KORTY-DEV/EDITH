using System.Threading.Channels;
using Edith.Core;
using Edith.Core.Brain;
using Edith.Speech;

namespace Edith.App;

/// <summary>
/// Speaks the model's answer sentence by sentence while it is still being written: the first
/// sentence is heard about a second earlier than after the full text. Offers like "Нужно что-то
/// ещё?" are held back and dropped if nothing follows them, as for a whole answer; the length cap
/// and the word fixes are the same as for a whole answer.
/// </summary>
public sealed class StreamingSpeech : IAnswerStream
{
    /// <summary>The same cut as <see cref="VoiceText.Sanitize"/> applies to a whole answer.</summary>
    private const int MaxSpokenChars = 700;

    private readonly HybridTtsService _tts;
    private readonly CancellationToken _token;
    private readonly long _session;
    private readonly Action<string> _onStart;
    private readonly Action<string> _onSentence;
    private readonly SentenceSplitter _splitter = new();
    private readonly Channel<string> _channel = Channel.CreateUnbounded<string>();
    private readonly List<string> _heldOffers = [];
    private readonly object _gate = new();
    private Task? _playback;
    private int _spokenChars;
    private bool _completed;

    /// <param name="onStart">Called once, with the first sentence, just before speech begins (state: speaking).</param>
    /// <param name="onSentence">Called for every sentence that goes to the voice (echo filter of the stop word).</param>
    public StreamingSpeech(HybridTtsService tts, CancellationToken token, long session, Action<string> onStart, Action<string> onSentence)
    {
        _tts = tts;
        _token = token;
        _session = session;
        _onStart = onStart;
        _onSentence = onSentence;
    }

    /// <summary>At least one sentence was handed to the voice.</summary>
    public bool Started { get; private set; }

    public string Spoken { get; private set; } = "";

    public void Delta(string text)
    {
        lock (_gate)
        {
            if (_completed)
                return;

            foreach (string sentence in _splitter.Add(text))
                Emit(sentence);
        }
    }

    public void End(bool wasFinalAnswer)
    {
        lock (_gate)
        {
            if (_completed)
                return;

            if (!wasFinalAnswer)
            {
                // A tool call follows: what was said before it is not an answer.
                _splitter.Reset();
                _heldOffers.Clear();
                return;
            }

            string rest = _splitter.Flush();
            if (rest.Length > 0)
                Emit(rest);

            _heldOffers.Clear();
            _completed = true;
            _channel.Writer.TryComplete();
        }
    }

    /// <summary>Closes the stream (if the model never ended it) and waits until everything is spoken.</summary>
    public async Task FinishAsync()
    {
        lock (_gate)
        {
            if (!_completed)
            {
                _completed = true;
                _heldOffers.Clear();
                _channel.Writer.TryComplete();
            }
        }

        if (_playback is not null)
        {
            try
            {
                await _playback;
            }
            catch (OperationCanceledException)
            {
                // stopped by the user
            }
        }
    }

    private void Emit(string sentence)
    {
        if (ReplyPolish.IsOffer(sentence))
        {
            _heldOffers.Add(sentence);
            return;
        }

        // An offer followed by more text was not a closing offer: say it after all.
        foreach (string held in _heldOffers)
            Speak(held);
        _heldOffers.Clear();
        Speak(sentence);
    }

    private void Speak(string sentence)
    {
        string text = VoiceText.ForSpeech(ReplyPolish.FixWords(sentence), maxChars: 600);
        if (text.Length == 0 || _spokenChars + text.Length > MaxSpokenChars)
            return;

        _spokenChars += text.Length;
        Spoken = Spoken.Length == 0 ? text : Spoken + " " + text;
        _onSentence(Spoken);

        if (!Started)
        {
            Started = true;
            _onStart(text);
            _playback = Task.Run(() => _tts.SpeakStreamAsync(_channel.Reader, _token, _session));
        }

        _channel.Writer.TryWrite(text);
    }
}
