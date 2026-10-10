using System.Text;
using System.Text.Json;

namespace Edith.Audio.Embedding;

/// <summary>
/// The XLM-R / SentencePiece "Unigram" tokenizer of multilingual-e5, read from the model's
/// tokenizer.json: NFKC, spaces to "▁", each word cut into the pieces whose log-probabilities add
/// up best (Viterbi), then &lt;s&gt; … &lt;/s&gt;. Verified against Hugging Face's tokenizer on mixed
/// Russian/English text (see tests).
/// </summary>
public sealed class UnigramTokenizer
{
    public const int BeginId = 0;
    public const int EndId = 2;
    public const int UnknownId = 3;

    private readonly Dictionary<string, (int Id, float Score)> _pieces = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Id, float Score)>.AlternateLookup<ReadOnlySpan<char>> _lookup;
    private readonly int _maxPieceChars;
    private readonly float _unknownScore;

    private UnigramTokenizer(Dictionary<string, (int, float)> pieces, int maxPieceChars, float minScore)
    {
        _pieces = pieces;
        _lookup = _pieces.GetAlternateLookup<ReadOnlySpan<char>>();
        _maxPieceChars = maxPieceChars;
        _unknownScore = minScore - 10f; // as in Hugging Face's Unigram
    }

    public static UnigramTokenizer Load(string tokenizerJsonPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(tokenizerJsonPath));
        JsonElement vocab = document.RootElement.GetProperty("model").GetProperty("vocab");

        var pieces = new Dictionary<string, (int, float)>(vocab.GetArrayLength(), StringComparer.Ordinal);
        int id = 0, longest = 1;
        float min = 0;
        foreach (JsonElement entry in vocab.EnumerateArray())
        {
            string piece = entry[0].GetString() ?? "";
            float score = (float)entry[1].GetDouble();
            // Special tokens (<s>, <pad>, </s>, <unk>) are not matched inside text.
            if (id > UnknownId && piece.Length > 0)
            {
                pieces[piece] = (id, score);
                longest = Math.Max(longest, piece.Length);
                min = Math.Min(min, score);
            }

            id++;
        }

        return new UnigramTokenizer(pieces, longest, min);
    }

    /// <summary>Token ids with &lt;s&gt; in front and &lt;/s&gt; at the end, cut to <paramref name="maxTokens"/>.</summary>
    public int[] Encode(string text, int maxTokens = 512)
    {
        var ids = new List<int> { BeginId };

        string normalized = Normalize(text);
        foreach (string word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            EncodeWord("▁" + word, ids);
            if (ids.Count >= maxTokens - 1)
                break;
        }

        if (ids.Count > maxTokens - 1)
            ids.RemoveRange(maxTokens - 1, ids.Count - (maxTokens - 1));

        ids.Add(EndId);
        return [.. ids];
    }

    private static string Normalize(string text)
    {
        // The model's own char map is SentencePiece's nmt_nfkc: NFKC, control characters as spaces.
        string kc = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(kc.Length);
        foreach (char c in kc)
            sb.Append(char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c);
        return sb.ToString().Trim();
    }

    private void EncodeWord(string word, List<int> ids)
    {
        // Viterbi over text elements (a surrogate pair is one character).
        int n = word.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        var piece = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;

        for (int start = 0; start < n; start++)
        {
            if (float.IsNegativeInfinity(best[start]))
                continue;

            bool matched = false;
            int limit = Math.Min(n, start + _maxPieceChars);
            for (int end = start + 1; end <= limit; end++)
            {
                if (_lookup.TryGetValue(word.AsSpan(start, end - start), out (int Id, float Score) found))
                {
                    float score = best[start] + found.Score;
                    if (score > best[end])
                    {
                        best[end] = score;
                        from[end] = start;
                        piece[end] = found.Id;
                    }

                    matched = true;
                }
            }

            if (!matched)
            {
                // A character the vocabulary lacks: one unknown token for it.
                int end = start + (char.IsHighSurrogate(word[start]) && start + 1 < n ? 2 : 1);
                float score = best[start] + _unknownScore;
                if (score > best[end])
                {
                    best[end] = score;
                    from[end] = start;
                    piece[end] = UnknownId;
                }
            }
        }

        var reversed = new List<int>();
        for (int at = n; at > 0; at = from[at])
            reversed.Add(piece[at]);
        reversed.Reverse();

        // Neighbouring unknowns fuse into one, as Hugging Face does.
        foreach (int id in reversed)
        {
            if (id == UnknownId && ids.Count > 0 && ids[^1] == UnknownId)
                continue;
            ids.Add(id);
        }
    }
}
