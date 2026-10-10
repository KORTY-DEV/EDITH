using Edith.Core;
using Edith.Core.Memory;

namespace Edith.Core.Tests;

public sealed class ConversationPolicyTests
{
    [Fact]
    public async Task CancelledOwner_ReleasesBrainGateBeforeSuccessorRuns()
    {
        var gate = new CancellableRequestGate();
        using var cancelledOwner = new CancellationTokenSource();
        using IDisposable first = await gate.EnterAsync(cancelledOwner.Token);
        Task<IDisposable> successor = gate.EnterAsync(CancellationToken.None);

        cancelledOwner.Cancel();
        Assert.False(successor.IsCompleted);

        first.Dispose();
        using IDisposable second = await successor.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(successor.IsCompletedSuccessfully);
    }

    [Fact]
    public void Wake_PauseThenSpeech_IsAcceptedInsideSixSecondWindow()
    {
        var machine = new ConversationStateMachine();

        machine.WakeDetected();
        Assert.Equal(TimeSpan.FromSeconds(6), machine.Settings.SpeechStartTimeout);
        Assert.Equal(ConversationState.WaitingForSpeech, machine.State);

        // A two-second pause is deliberately still inside the speech-start window.
        machine.SpeechStarted();
        machine.SpeechEnded();

        Assert.Equal(ConversationState.Thinking, machine.State);
    }

    [Fact]
    public void Wake_WithoutSpeech_TimesOutSilentlyToSleeping()
    {
        var machine = new ConversationStateMachine();
        machine.WakeDetected();
        machine.NoSpeechTimedOut();

        Assert.Equal(ConversationState.Sleeping, machine.State);
    }

    [Fact]
    public void TenCompletedWakeCycles_RearmToSleepingEveryTime()
    {
        var machine = new ConversationStateMachine();

        for (int i = 0; i < 10; i++)
        {
            machine.WakeDetected();
            machine.SpeechStarted();
            machine.SpeechEnded();
            machine.StartSpeaking();
            machine.CompleteInteraction();

            Assert.Equal(ConversationState.Sleeping, machine.State);
        }
    }

    [Fact]
    public void BargeInDuringThinking_StartsANewNormalSpeechWindow()
    {
        var machine = new ConversationStateMachine();
        machine.WakeDetected();
        machine.SpeechStarted();
        machine.SpeechEnded();
        Assert.Equal(ConversationState.Thinking, machine.State);

        machine.BargeIn();
        machine.SpeechStarted();
        machine.SpeechEnded();

        Assert.Equal(ConversationState.Thinking, machine.State);
    }

    [Fact]
    public void BargeInThenSilence_SleepsWithoutCreatingCommand()
    {
        var machine = new ConversationStateMachine();
        machine.StartSpeaking();
        machine.BargeIn();
        machine.NoSpeechTimedOut();

        Assert.Equal(ConversationState.Sleeping, machine.State);
    }

    [Fact]
    public void CompletedNormalResponse_DoesNotReopenListening()
    {
        var machine = new ConversationStateMachine();
        machine.WakeDetected();
        machine.SpeechStarted();
        machine.SpeechEnded();
        machine.StartSpeaking();
        machine.CompleteInteraction();

        Assert.Equal(ConversationState.Sleeping, machine.State);
    }

    [Fact]
    public void FreshInformation_UsesBackgroundResearchAndRuntimeYear()
    {
        const string question = "Какая сейчас лучшая видеокарта?";
        DateTimeOffset now = new(2026, 9, 19, 12, 0, 0, TimeSpan.FromHours(4));

        Assert.Equal(IntentRoute.BackgroundWebResearch, IntentRouter.Classify(question));
        Assert.Equal("Какая сейчас лучшая видеокарта? 2026",
            IntentRouter.BuildFreshSearchQuery(question, now));
    }

    [Theory]
    [InlineData("что ты помнишь обо мне")]
    [InlineData("что ты знаешь обо мне")]
    [InlineData("что я просил запомнить")]
    [InlineData("расскажи, что ты обо мне помнишь")]
    public void Memory_recall_phrases_are_classified_directly(string phrase)
    {
        Assert.True(MemoryPolicy.IsRecallQuery(phrase));
    }

    [Fact]
    public void New_topic_does_not_inherit_unrelated_gpu_turns()
    {
        var context = new ConversationContext();
        context.Add("какая сейчас лучшая видеокарта", "NVIDIA GeForce RTX 5090.");
        context.Add("а сколько она стоит", "Цена зависит от магазина.");

        Assert.DoesNotContain("RTX 5090", context.BuildForPrompt("чем SSD отличается от HDD"));
        Assert.Contains("RTX 5090", context.BuildForPrompt("а сколько она стоит"));
    }

    [Theory]
    [InlineData("объясни чем отличается от аш ди ди", "объясни чем отличается от HDD")]
    [InlineData("объясни чем отличается от аж ди ди", "объясни чем отличается от HDD")]
    [InlineData("какая видеокарта с ртх", "какая видеокарта с RTX")]
    [InlineData("энвидиа джифорс", "NVIDIA GeForce")]
    public void Tech_terms_are_normalized_after_stt(string heard, string expected)
    {
        Assert.Equal(expected, TechTermNormalizer.Normalize(heard));
    }

    [Theory]
    [InlineData("я готов помочь", "я готова помочь")]
    [InlineData("я нашел ответ", "я нашла ответ")]
    [InlineData("я проверил данные", "я проверила данные")]
    public void Persona_is_feminine_in_model_slips(string text, string expected)
    {
        Assert.Equal(expected, PersonaLanguage.Apply(text));
    }

    [Fact]
    public void Memory_summary_preserves_preference_key_in_natural_form()
    {
        var item = new MemoryItem
        {
            Type = MemoryType.Preference,
            Key = "любимый цвет",
            Content = "синий"
        };

        Assert.Equal("Помню, что твой любимый цвет — синий.",
            MemorySummaryFormatter.Format([item]));
    }

    [Fact]
    public void ExplicitBrowserRequest_AllowsForegroundUiAction()
    {
        Assert.Equal(IntentRoute.ForegroundUiAction,
            IntentRouter.Classify("Покажи мне в браузере лучшие видеокарты"));
        Assert.True(IntentRouter.TryExtractForegroundSearchQuery(
            "Покажи мне в браузере лучшие видеокарты", out string query));
        Assert.Equal("лучшие видеокарты", query);
    }

    [Fact]
    public void GamingInformationalQuestion_HasNoForegroundRoute()
    {
        Assert.NotEqual(IntentRoute.ForegroundUiAction,
            IntentRouter.Classify("какая сейчас лучшая видеокарта"));
    }

    [Fact]
    public void FreshResearchMock_ExtractsSourcesWithoutForegroundAction()
    {
        const string html = """
            <a class="result__a">RTX 5090 review 2026</a>
            <div class="result__snippet">Current flagship performance.</div>
            <a class="result__a">Best GPUs 2026</a>
            <div class="result__snippet">Choose by budget and resolution.</div>
            """;

        var result = new BackgroundResearchResult(
            IntentRouter.BuildFreshSearchQuery("лучшая видеокарта сейчас", new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero)),
            BackgroundResearchExtractor.ExtractDuckDuckGo(html));

        Assert.Equal("лучшая видеокарта сейчас 2026", result.Query);
        Assert.Equal(2, result.Sources.Count);
        Assert.Contains("RTX 5090", result.SourceBackedReply());
    }

    [Fact]
    public void ResearchEvidence_ranks_known_retailers_above_unknown_sources()
    {
        const string html = """
            <a class="result__a" href="https://example.invalid/cheap">Cheap card</a>
            <div class="result__snippet">RTX 5060 45 000 рублей.</div>
            <a class="result__a" href="https://www.dns-shop.ru/product/5080">RTX 5080 in stock</a>
            <div class="result__snippet">RTX 5080 180 000 рублей.</div>
            """;
        IReadOnlyList<ResearchSource> sources = BackgroundResearchExtractor.ExtractEvidence(html);
        Assert.Equal("dns-shop.ru", sources[1].Domain);
        Assert.True(sources[1].TrustScore > sources[0].TrustScore);
        var result = new BackgroundResearchResult("лучшая видеокарта 2026", sources.Select(x => x.ToString()).ToList(), sources);
        Assert.Contains("best overall", result.FormatForBrain());
    }

    [Fact]
    public void Direct_price_evidence_keeps_exact_product_and_listing_metadata()
    {
        const string html = """
            {"name":"NVIDIA GeForce RTX 5090 Gainward","price":624990,"priceCurrency":"RUB","availability":"InStock"}
            {"name":"NVIDIA GeForce RTX 5090 ASUS","price":699990,"priceCurrency":"RUB","availability":"InStock"}
            """;
        IReadOnlyList<ResearchSource> evidence = BackgroundResearchExtractor.ExtractDirectEvidence(html, "https://regard.ru/catalog", 80, ResearchEvidenceType.CurrentPrice);
        Assert.Equal(2, evidence.Count);
        Assert.All(evidence, x => Assert.Equal("NVIDIA GeForce RTX 5090", x.CanonicalProduct));
        Assert.All(evidence, x => Assert.Equal("RUB", x.Currency));
    }
}
