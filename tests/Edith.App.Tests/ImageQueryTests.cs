using Edith.App;

namespace Edith.App.Tests;

public sealed class ImageQueryTests
{
    [Fact]
    public void Variants_GoFromSpecificToLoose()
    {
        IReadOnlyList<string> variants = BrainToolRouter.ImageQueryVariants("best graphics card in the world 2026");

        Assert.Equal("best graphics card in the world 2026", variants[0]);
        Assert.Contains("graphics card", variants);
        Assert.DoesNotContain(variants, v => v.Contains("best", StringComparison.OrdinalIgnoreCase) && v != variants[0]);
    }

    [Fact]
    public void SpecificProduct_IsTriedFirstAndOnlyOnce()
    {
        IReadOnlyList<string> variants = BrainToolRouter.ImageQueryVariants("NVIDIA GeForce RTX 5090");

        Assert.Equal("NVIDIA GeForce RTX 5090", variants[0]);
        Assert.Equal(variants.Count, variants.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Theory]
    [InlineData("best graphics card in the world", true)]
    [InlineData("latest iPhone", true)]
    [InlineData("самая новая видеокарта", true)]
    [InlineData("NVIDIA GeForce RTX 5090", false)]
    [InlineData("landscape sea", false)]
    public void Superlatives_NeedALookupFirst(string query, bool expected) =>
        Assert.Equal(expected, BrainToolRouter.IsSuperlative(query));
}
