namespace Edith.Core.Memory;

public enum MemoryType
{
    UserFact,
    Preference,
    Person,
    Project,
    Decision,
    Todo,
    Correction
}

/// <summary>Canonical, local structured memory. Values never go to diagnostics.</summary>
public sealed record MemoryItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public MemoryType Type { get; init; }
    public string Key { get; init; } = "";
    public string Content { get; init; } = "";
    public double Confidence { get; init; } = 0.9;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsedAt { get; init; }
    public string Source { get; init; } = "voice";
    public bool Active { get; init; } = true;
}
