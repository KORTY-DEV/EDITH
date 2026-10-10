namespace Edith.Audio.Wake;

/// <summary>
/// Second opinion on an acoustic wake candidate: what did the accurate STT actually hear in
/// the last ~2 s? Stage 1 + verifier are fast but fire on dense speech (a Pushkin reading
/// produced two accepts in 11 s); GigaAM hears "эдит" in real wakes and never in that reading.
/// </summary>
public static class WakeTranscriptConfirmer
{
    private static readonly string[] Canonical = ["эдит"];

    // Common words one edit away from "эдит" that must not wake EDITH.
    private static readonly HashSet<string> Blocked = ["едет", "идет", "едят", "эдем", "это", "эти"];

    /// <summary>True when any word of <paramref name="transcript"/> is "эдит" or one edit away from it.</summary>
    public static bool Confirms(string transcript) =>
        transcript
            .ToLowerInvariant()
            .Replace('ё', 'е')
            .Split([' ', ',', '.', '!', '?', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Any(IsWakeToken);

    // Real words close to "эдит" that people say on their own.
    private static readonly HashSet<string> CommonNearWords =
        ["видит", "едет", "идет", "идти", "иди", "эти", "это", "дети", "едим", "едят", "эдем", "кредит", "вдруг", "ответ", "итак",
         "ведет", "вредит", "ездит", "едете"];

    /// <summary>
    /// A whole short phrase that is only one word, 2 edits or less from "эдит" and not a common
    /// word, is a call: GigaAM heard "видет" for a clear "Эдит" live.
    /// </summary>
    public static bool ConfirmsUtterance(string transcript)
    {
        string[] words = transcript.ToLowerInvariant().Replace('ё', 'е')
            .Split([' ', ',', '.', '!', '?', '-'], StringSplitOptions.RemoveEmptyEntries);

        // A call opens the phrase ("Эдит, открой…", "слушай, Эдит…") or is nearly all of it
        // ("привет, Эдит"). A name-like word at the end of a long sentence is just talk:
        // "что как в жизни делал эрди" woke her live.
        int index = Array.FindIndex(words, IsWakeToken);
        if (index >= 0)
            return index <= 1 || words.Length <= 3;

        // One mis-heard word on its own: GigaAM heard "видет" for a clear "Эдит" live,
        // but "нет" must not pass. Name-shaped only: starts like it, ends in -дит/-дет/-дид.
        return words.Length == 1 &&
               words[0].Length is >= 4 and <= 6 &&
               "эеив".Contains(words[0][0]) &&
               System.Text.RegularExpressions.Regex.IsMatch(words[0], "д[еи][тд]$") &&
               !CommonNearWords.Contains(words[0]) &&
               new[] { "эдит", "эдет", "эдид" }.Any(form => Distance(words[0], form) <= 2);
    }

    /// <summary>
    /// "Эдит, открой телеграм" → "открой телеграм": everything after the last wake word.
    /// Empty when nothing follows (the user will say the command after a pause).
    /// </summary>
    public static string CommandAfterWake(string transcript)
    {
        string[] words = transcript
            .Split([' ', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

        int last = Array.FindLastIndex(words, IsWakeToken);
        return last < 0 ? "" : string.Join(' ', words[(last + 1)..]).Trim();
    }

    public static bool IsWakeToken(string word)
    {
        word = word.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (word.Length < 3 || Blocked.Contains(word))
            return false;

        // "эдита", "эдиту" — the name in another case is still a call.
        if (word.StartsWith("эдит", StringComparison.Ordinal) && word.Length <= 6)
            return true;

        // "эди" is the name clipped at the end; only exactly that ("эрди" is not a call).
        return word == "эди" || Canonical.Any(c => Distance(word, c) <= 1);
    }

    private static int Distance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}

/// <summary>Last few seconds of microphone audio, for re-reading what was just said.</summary>
public sealed class AudioRingBuffer(int capacity)
{
    private readonly float[] _data = new float[capacity];
    private readonly object _gate = new();
    private int _next;
    private int _count;

    public void Add(float[] samples, int count)
    {
        lock (_gate)
        {
            for (int i = 0; i < count; i++)
            {
                _data[_next] = samples[i];
                _next = (_next + 1) % _data.Length;
            }

            _count = Math.Min(_data.Length, _count + count);
        }
    }

    /// <summary>The most recent <paramref name="samples"/> samples, oldest first.</summary>
    public float[] Last(int samples)
    {
        lock (_gate)
        {
            int take = Math.Min(samples, _count);
            var result = new float[take];
            int start = (_next - take + _data.Length) % _data.Length;
            for (int i = 0; i < take; i++)
                result[i] = _data[(start + i) % _data.Length];
            return result;
        }
    }
}
