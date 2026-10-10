using System.Text.Json;

namespace Edith.Core;

/// <summary>
/// Timings of a voice turn.  Kept outside the audio implementation so the
/// conversation contract can be tested without a microphone or Vosk.
/// </summary>
public sealed record ConversationSettings
{
    public int SpeechStartTimeoutSeconds { get; init; } = 4;
    public int SpeechEndSilenceMilliseconds { get; init; } = 650;

    /// <summary>
    /// Smart Turn (an 8 MB model that judges from the sound whether the phrase is finished):
    /// a short pause is enough to ask it; "not finished" keeps listening up to the hold time.
    /// </summary>
    public bool SmartTurn { get; init; } = true;
    public string SmartTurnModelPath { get; init; } = @"C:\EDITH\models\smart-turn\smart-turn-v3.2-cpu.onnx";
    public int TurnEndSilenceMilliseconds { get; init; } = 550;
    public int TurnMaxHoldMilliseconds { get; init; } = 1500;
    public double TurnCompleteThreshold { get; init; } = 0.5;

    /// <summary>
    /// Software echo cancellation for the stop word (what the speakers play is taken out of the
    /// microphone). Off by default: the MCHOSE S9 PRO already suppresses its own speakers in hardware
    /// (measured 2026-10-02: the filter only added noise). For other speakers or headsets.
    /// </summary>
    public bool EchoCancel { get; init; }

    /// <summary>After an answer that ends with a question, the next phrase is heard without "Эдит" (7 s).</summary>
    public bool AnswerFollowUp { get; init; } = true;

    /// <summary>The plain conversation answer is spoken sentence by sentence while the model still writes it.</summary>
    public bool StreamSpeech { get; init; } = true;

    /// <summary>A phrase that ends on "и", "в", "что"… is not finished: wait once more for the rest.</summary>
    public bool ContinueAfterDanglingWord { get; init; } = true;

    public TimeSpan TurnEndSilence =>
        TimeSpan.FromMilliseconds(Math.Clamp(TurnEndSilenceMilliseconds, 350, 1500));

    public TimeSpan TurnMaxHold =>
        TimeSpan.FromMilliseconds(Math.Clamp(TurnMaxHoldMilliseconds, 300, 4000));

    public TimeSpan SpeechStartTimeout =>
        TimeSpan.FromSeconds(Math.Clamp(SpeechStartTimeoutSeconds, 2, 15));

    public TimeSpan SpeechEndSilence =>
        TimeSpan.FromMilliseconds(Math.Clamp(SpeechEndSilenceMilliseconds, 350, 3000));
}

public static class ConversationSettingsLoader
{
    public static ConversationSettings Load(string path)
    {
        if (!File.Exists(path))
            return new ConversationSettings();

        try
        {
            return JsonSerializer.Deserialize<ConversationSettings>(File.ReadAllText(path),
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new ConversationSettings();
        }
        catch
        {
            return new ConversationSettings();
        }
    }
}

public enum ConversationState
{
    Sleeping,
    WaitingForSpeech,
    CapturingSpeech,
    Thinking,
    Speaking
}

/// <summary>
/// Pure state contract for a single voice interaction.  The app owns audio and
/// cancellation; this type makes the user-visible transitions deterministic.
/// </summary>
public sealed class ConversationStateMachine
{
    public ConversationStateMachine(ConversationSettings? settings = null)
    {
        Settings = settings ?? new ConversationSettings();
    }

    public ConversationSettings Settings { get; }
    public ConversationState State { get; private set; } = ConversationState.Sleeping;

    public void WakeDetected() => State = ConversationState.WaitingForSpeech;
    public void SpeechStarted()
    {
        if (State == ConversationState.WaitingForSpeech)
            State = ConversationState.CapturingSpeech;
    }

    public void SpeechEnded()
    {
        if (State == ConversationState.CapturingSpeech)
            State = ConversationState.Thinking;
    }

    public void NoSpeechTimedOut()
    {
        if (State == ConversationState.WaitingForSpeech)
            State = ConversationState.Sleeping;
    }

    public void StartSpeaking() => State = ConversationState.Speaking;
    public void CompleteInteraction() => State = ConversationState.Sleeping;

    public void BargeIn() => State = ConversationState.WaitingForSpeech;
}

public enum IntentRoute
{
    DirectFastPath,
    LocalTool,
    BackgroundWebResearch,
    ForegroundUiAction,
    AiBrain
}

public static class IntentRouter
{
    private static readonly string[] FreshTerms =
    {
        "сейчас", "сегодня", "актуаль", "текущ", "последн", "лучший сейчас", "новост"
    };

    private static readonly string[] UiTerms =
    {
        "открой", "запусти", "покажи", "в браузере", "открой сайт", "открой результаты", "найди это в браузере"
    };

    public static IntentRoute Classify(string text)
    {
        string normalized = Normalize(text);
        if (string.IsNullOrWhiteSpace(normalized))
            return IntentRoute.AiBrain;

        if (HasExplicitUiIntent(normalized))
            return IntentRoute.ForegroundUiAction;

        if (FreshTerms.Any(normalized.Contains) ||
            normalized.StartsWith("найди ") ||
            normalized.StartsWith("поищи ") ||
            normalized.StartsWith("загугли "))
        {
            return IntentRoute.BackgroundWebResearch;
        }

        return IntentRoute.AiBrain;
    }

    public static bool HasExplicitUiIntent(string text) =>
        UiTerms.Any(Normalize(text).Contains);

    public static string BuildFreshSearchQuery(string query, DateTimeOffset now)
    {
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return query;

        string normalized = Normalize(query);
        bool fresh = FreshTerms.Any(normalized.Contains);
        return fresh && !normalized.Contains(now.Year.ToString())
            ? $"{query} {now:yyyy}"
            : query;
    }

    public static bool TryExtractForegroundSearchQuery(string text, out string query)
    {
        string normalized = Normalize(text);
        string[] prefixes =
        {
            "покажи мне в браузере ", "покажи в браузере ",
            "найди это в браузере ", "найди в браузере ",
            "открой результаты поиска "
        };

        foreach (string prefix in prefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                query = normalized[prefix.Length..].Trim();
                return !string.IsNullOrWhiteSpace(query);
            }
        }

        query = string.Empty;
        return false;
    }

    public static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('ё', 'е');
}
