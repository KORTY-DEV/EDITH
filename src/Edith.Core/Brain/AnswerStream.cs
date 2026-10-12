using System.Text;
using System.Text.Json;

namespace Edith.Core.Brain;

/// <summary>
/// Where the words of an answer go while the model is still writing it, so that speech can start
/// with the first sentence instead of after the last one. One model turn = Delta… then End.
/// </summary>
public interface IAnswerStream
{
    /// <summary>The next piece of the model's text.</summary>
    void Delta(string text);

    /// <summary>The model turn is over: a final answer, or a tool call (then what was said is dropped).</summary>
    void End(bool wasFinalAnswer);
}

/// <summary>Cuts a growing text into sentences: ". ", "! ", "? ", "… " or a line break end one.</summary>
public sealed class SentenceSplitter
{
    public const int MinSentenceChars = 24;

    private static readonly HashSet<string> Abbreviations =
        ["тыс", "млн", "млрд", "руб", "коп", "кв", "ул", "им", "см", "рис", "стр", "табл", "напр", "проч", "обл", "экз", "пос", "англ", "нем", "франц"];

    private readonly StringBuilder _buffer = new();

    /// <summary>Adds text; returns the sentences that are complete now.</summary>
    public IReadOnlyList<string> Add(string text)
    {
        _buffer.Append(text);
        var done = new List<string>();
        string all = _buffer.ToString();

        int start = 0;
        for (int i = 0; i < all.Length - 1; i++)
        {
            char c = all[i];
            bool ends = (c is '.' or '!' or '?' or '…' && char.IsWhiteSpace(all[i + 1])) || c == '\n';
            if (!ends)
                continue;

            // "т. е.", "3. пункт", "ул. Мира": too short to be a sentence by itself.
            if (i + 1 - start < MinSentenceChars && c != '\n')
                continue;

            // A one- or two-letter word before the full stop is an abbreviation ("т. е.", "др.").
            if (c == '.')
            {
                int wordStart = i;
                while (wordStart > start && char.IsLetter(all[wordStart - 1]))
                    wordStart--;
                if (i - wordStart is >= 1 and <= 2 || Abbreviations.Contains(all[wordStart..i].ToLowerInvariant()))
                    continue;
            }

            string sentence = all[start..(i + 1)].Trim();
            if (sentence.Length > 0)
                done.Add(sentence);
            start = i + 1;
        }

        _buffer.Clear().Append(all[start..]);
        return done;
    }

    /// <summary>What is left when the text ends: the last sentence, even without a full stop.</summary>
    public string Flush()
    {
        string rest = _buffer.ToString().Trim();
        _buffer.Clear();
        return rest;
    }

    public void Reset() => _buffer.Clear();
}

/// <summary>Reads the server-sent events of a GigaChat streamed answer ("data: {json}" lines).</summary>
public sealed class GigaChatStreamReader
{
    private readonly StringBuilder _content = new();
    private readonly StringBuilder _argumentText = new();
    private string? _functionName;
    private string? _argumentObject;
    private int _promptTokens, _completionTokens;

    public bool Finished { get; private set; }

    /// <summary>One "data:" payload; returns the new text it carried (empty for none).</summary>
    public string Add(string payload)
    {
        payload = payload.Trim();
        if (payload.Length == 0)
            return "";
        if (payload == "[DONE]")
        {
            Finished = true;
            return "";
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("usage", out JsonElement usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement p)) p.TryGetInt32(out _promptTokens);
                if (usage.TryGetProperty("completion_tokens", out JsonElement q)) q.TryGetInt32(out _completionTokens);
            }

            if (!root.TryGetProperty("choices", out JsonElement choices) || choices.GetArrayLength() == 0)
                return "";

            JsonElement choice = choices[0];
            JsonElement delta = choice.TryGetProperty("delta", out JsonElement d) ? d : choice.TryGetProperty("message", out JsonElement m) ? m : default;
            string text = "";
            if (delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String)
                {
                    text = content.GetString() ?? "";
                    _content.Append(text);
                }

                if (delta.TryGetProperty("function_call", out JsonElement call) && call.ValueKind == JsonValueKind.Object)
                {
                    if (call.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                        _functionName = name.GetString();
                    if (call.TryGetProperty("arguments", out JsonElement args))
                    {
                        if (args.ValueKind == JsonValueKind.Object)
                            _argumentObject = args.GetRawText();
                        else if (args.ValueKind == JsonValueKind.String)
                            _argumentText.Append(args.GetString());
                    }
                }
            }

            if (choice.TryGetProperty("finish_reason", out JsonElement finish) && finish.ValueKind == JsonValueKind.String)
                Finished = true;

            return text;
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public GigaChatProtocol.Reply Build()
    {
        var calls = new List<Agent.ToolCall>();
        if (!string.IsNullOrWhiteSpace(_functionName))
        {
            string raw = _argumentObject ?? (_argumentText.Length > 0 ? _argumentText.ToString() : "{}");
            JsonElement arguments;
            try
            {
                arguments = JsonDocument.Parse(raw).RootElement.Clone();
                if (arguments.ValueKind == JsonValueKind.String)
                    arguments = JsonDocument.Parse(arguments.GetString() ?? "{}").RootElement.Clone();
            }
            catch (JsonException)
            {
                arguments = JsonDocument.Parse("{}").RootElement.Clone();
            }

            calls.Add(new Agent.ToolCall(_functionName!.Trim().ToLowerInvariant(), arguments));
        }

        return new GigaChatProtocol.Reply(_content.ToString(), calls, _promptTokens, _completionTokens);
    }
}
