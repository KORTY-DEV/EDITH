using System.Text.RegularExpressions;

namespace Edith.Core;

public sealed record ConversationEntity(
    string Type,
    string CanonicalName,
    IReadOnlyList<string> Aliases,
    DateTimeOffset SourceAt,
    DateTimeOffset LastReferencedAt,
    double Confidence);

/// <summary>Small, recent entity register for factual follow-ups; not a second memory system.</summary>
public sealed class ConversationEntityState
{
    private ConversationEntity? _current;
    public ConversationEntity? Current => _current;
    public void Clear() => _current = null;

    public ConversationEntity? ObserveAuthoritativeEvidence(IEnumerable<ResearchSource> sources)
    {
        ResearchSource? source = sources.FirstOrDefault(x => x.IsAuthoritativeProductSource && x.CanonicalProduct is not null);
        if (source?.CanonicalProduct is null) return _current;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _current = new ConversationEntity("GPU", source.CanonicalProduct, [source.CanonicalProduct], source.EffectiveDate ?? now, now, 0.95);
        return _current;
    }

    public ConversationEntity? ObserveAssistantAnswer(string answer, DateTimeOffset now)
    {
        Match match = Regex.Match(answer ?? "", @"\b(?:NVIDIA\s+)?RTX\s+(?<model>5090|5080|5070\s*Ti|5070|5060(?:\s*Ti)?)\b", RegexOptions.IgnoreCase);
        if (!match.Success) return _current;
        string model = "NVIDIA GeForce RTX " + Regex.Replace(match.Groups["model"].Value, @"\s+", " ").ToUpperInvariant();
        _current = new ConversationEntity("GPU", model, [model, match.Value], now, now, 0.9);
        return _current;
    }

    public string? ResolvePriceFollowUp(string text, DateTimeOffset now)
    {
        if (_current is null || now - _current.LastReferencedAt > TimeSpan.FromMinutes(10)) return null;
        if (!Regex.IsMatch(text, @"\b(она|он|они|эта|этот|эти)\b|сколько\s+(?:(?:она|он|они)\s+)?(?:сейчас\s+)?стоит|цена", RegexOptions.IgnoreCase)) return null;
        _current = _current with { LastReferencedAt = now };
        return $"Сколько сейчас, {now:yyyy-MM-dd}, стоит {_current.CanonicalName} в России?";
    }
}
