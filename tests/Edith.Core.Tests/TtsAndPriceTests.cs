using Edith.Core;

namespace Edith.Core.Tests;

public sealed class TtsAndPriceTests
{
    [Fact]
    public void OneExactPriceSource_UsesOfferWording()
    {
        var source = new ResearchSource(
            "RTX 5090 в наличии",
            "regard.ru",
            "RTX 5090 — 624990 ₽, в наличии",
            "https://www.regard.ru/item",
            80)
        { EvidenceType = ResearchEvidenceType.CurrentPrice };
        var result = new BackgroundResearchResult(
            "RTX 5090 сколько стоит",
            [source.ToString()],
            [source]);

        string? reply = result.SinglePriceOfferReply("NVIDIA GeForce RTX 5090");

        Assert.Equal("Нашла актуальное предложение за 624 990 рублей в магазине Регард.", reply);
    }

    [Fact]
    public void RetailerRubleSnippet_ProducesExactRangeWithoutCallingItAnAverage()
    {
        var first = new ResearchSource("GeForce RTX 5090", "dns-shop.ru", "RTX 5090 624 990 руб. в наличии", "https://www.dns-shop.ru/item", 80)
        { EvidenceType = ResearchEvidenceType.CurrentPrice };
        var second = new ResearchSource("GeForce RTX 5090", "citilink.ru", "RTX 5090 699990 рублей в наличии", "https://www.citilink.ru/item", 80)
        { EvidenceType = ResearchEvidenceType.CurrentPrice };
        var result = new BackgroundResearchResult("RTX 5090 сколько сейчас стоит", [first.ToString(), second.ToString()], [first, second]);

        Assert.Equal("624 990 руб", first.PriceText);
        Assert.Equal("Нашла несколько актуальных предложений: от 624 990 до 699 990 рублей. Цена зависит от версии карты и магазина.",
            result.PriceRangeReply("NVIDIA GeForce RTX 5090"));
    }
}
