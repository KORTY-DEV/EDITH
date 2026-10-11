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

public sealed class CaptionMatchTests
{
    [Fact]
    public void BoothPhoto_IsNotATank_ButEverestIsEverest()
    {
        Assert.False(Edith.App.AnswerImages.CaptionMatches("Astron Rex World of Tanks", "E3 2011 - World of Tanks booth (Wargaming.net)"));
        Assert.True(Edith.App.AnswerImages.CaptionMatches("Astron Rex World of Tanks", "ASTRON Rex & Alembic Skin World Of Tanks - YouTube"));
        Assert.True(Edith.App.AnswerImages.CaptionMatches("Mount Everest", "Everest, Himalayas"));
    }
}

public sealed class HideWindowPhraseTests
{
    [Theory]
    [InlineData("убери окно")]
    [InlineData("Убери окно.")]
    [InlineData("окно")]
    [InlineData("убери картинку")]
    public void Phrases_HideTheWindow_AndStaySpoken(string phrase)
    {
        bool hidden = false;
        void OnHide() => hidden = true;
        Edith.App.UiModes.HideRequested += OnHide;
        try
        {
            Assert.True(Edith.App.UiModes.TryHandle(phrase, out string reply));
            Assert.Equal("Убрала.", reply);
            Assert.True(hidden);
        }
        finally
        {
            Edith.App.UiModes.HideRequested -= OnHide;
        }
    }
}

public sealed class GreetingTests
{
    [Theory]
    [InlineData("привет", true)]
    [InlineData("эдит привет", true)]
    [InlineData("добрый вечер", true)]
    [InlineData("доброе утро эдит", true)]
    [InlineData("что мне сказать теще чтобы ей понравилось как мне ее поприветствовать какие комплименты ей сказать", false)]
    [InlineData("как мне поприветствовать гостей", false)]
    [InlineData("расскажи как приветствовать клиента на встрече", false)]
    public void OnlyShortHellosAreGreetings(string phrase, bool expected) =>
        Assert.Equal(expected, Edith.App.ResponseEngine.IsGreeting(phrase));
}
