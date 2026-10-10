using Edith.Core;

namespace Edith.Core.Tests;

public sealed class ConversationEntityTests
{
    [Fact]
    public void Authoritative_gpu_evidence_becomes_canonical_referent()
    {
        var state = new ConversationEntityState();
        var sources = new[]
        {
            new ResearchSource("NVIDIA GeForce RTX 5090", "nvidia.com", "2026-09-19", "https://nvidia.com/5090", 100) { EvidenceType = ResearchEvidenceType.ProductExistence }
        };
        ConversationEntity? entity = state.ObserveAuthoritativeEvidence(sources);
        Assert.Equal("NVIDIA GeForce RTX 5090", entity?.CanonicalName);
        Assert.Equal("Сколько сейчас, 2026-09-19, стоит NVIDIA GeForce RTX 5090 в России?", state.ResolvePriceFollowUp("а сколько она сейчас стоит в России?", new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Сколько сейчас, 2026-09-19, стоит NVIDIA GeForce RTX 5090 в России?", state.ResolvePriceFollowUp("а сколько они сейчас стоят в России?", new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Сколько сейчас, 2026-09-19, стоит NVIDIA GeForce RTX 5090 в России?", state.ResolvePriceFollowUp("сколько сейчас стоит", new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Unauthoritative_future_product_is_not_accepted()
    {
        var state = new ConversationEntityState();
        var sources = new[]
        {
            new ResearchSource("RTX 6090 leaked price", "example.invalid", "", "https://example.invalid", 20)
        };
        Assert.Null(state.ObserveAuthoritativeEvidence(sources));
    }

    [Fact]
    public void Assistant_answer_can_refresh_current_referent()
    {
        var state = new ConversationEntityState();
        ConversationEntity? entity = state.ObserveAssistantAnswer("Подтверждённая модель — RTX 5090.", DateTimeOffset.UtcNow);
        Assert.Equal("NVIDIA GeForce RTX 5090", entity?.CanonicalName);
    }

    [Fact]
    public void Grounding_rejects_future_model_from_brain_answer()
    {
        var entity = new ConversationEntity("GPU", "NVIDIA GeForce RTX 5090", ["RTX 5090"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0.95);
        string answer = ProductGroundingPolicy.Apply("Лучшая карта — RTX 6090.", "какая сейчас лучшая видеокарта", entity);
        Assert.Contains("RTX 5090", answer);
        Assert.DoesNotContain("RTX 6090", answer);
    }

    [Fact]
    public void Grounding_completes_truncated_canonical_answer()
    {
        var entity = new ConversationEntity("GPU", "NVIDIA GeForce RTX 5090", ["RTX 5090"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0.95);
        string answer = ProductGroundingPolicy.Apply("Сейчас по тестам лидирует NVIDIA GeForce RTX 5090", "какая сейчас лучшая видеокарта", entity);
        Assert.EndsWith(".", answer);
        Assert.Contains("RTX 5090", answer);
    }

    [Fact]
    public void New_topic_can_clear_previous_entity()
    {
        var state = new ConversationEntityState();
        state.ObserveAssistantAnswer("Подтверждённая модель — RTX 5090.", DateTimeOffset.UtcNow);
        state.Clear();
        Assert.Null(state.Current);
    }
}
