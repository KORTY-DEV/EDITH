namespace Edith.Core.Memory;

/// <summary>Long-term structured memory about the user and their world.</summary>
public interface IMemoryStore
{
    MemoryItem Upsert(MemoryType type, string key, string content, string source = "voice");

    /// <summary>Deactivates every active item whose key or content contains <paramref name="query"/>.</summary>
    int Forget(string query);

    IReadOnlyList<MemoryItem> Search(string query, int max = 5);

    IReadOnlyList<MemoryItem> ActiveItems();
}

public sealed record ConversationTurn(
    DateTimeOffset Timestamp,
    long Session,
    string User,
    string Assistant,
    string Route);

/// <summary>Every exchange, kept locally, so EDITH can recall "о чём мы говорили вчера".</summary>
public interface IConversationLog
{
    void AddTurn(ConversationTurn turn);

    IReadOnlyList<ConversationTurn> Turns(DateTimeOffset from, DateTimeOffset to, int max = 200);
}
