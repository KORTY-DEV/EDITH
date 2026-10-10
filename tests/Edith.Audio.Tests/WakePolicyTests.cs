using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class WakePolicyTests
{
    // 80 ms — шаг pipeline.
    private const long Step = 80;

    private static WakeDetectionPolicy Policy(
        float threshold = 0.7f,
        int hits = 2,
        int cooldown = 1500) =>
        new(threshold, hits, cooldown);


    [Fact]
    public void BelowThreshold_NeverFires()
    {
        var policy = Policy();

        for (int i = 0; i < 50; i++)
        {
            Assert.False(
                policy.Evaluate(0.69f, i * Step));
        }
    }


    [Fact]
    public void SingleHit_IsNotEnough_WhenTwoRequired()
    {
        var policy = Policy();

        Assert.False(policy.Evaluate(0.95f, 0));
        Assert.False(policy.Evaluate(0.10f, Step));
        Assert.False(policy.Evaluate(0.95f, 2 * Step));
        Assert.False(policy.Evaluate(0.10f, 3 * Step));
    }


    [Fact]
    public void TwoConsecutiveHits_Fire_OnTheSecond()
    {
        var policy = Policy();

        Assert.False(policy.Evaluate(0.80f, 0));
        Assert.True(policy.Evaluate(0.90f, Step));
    }


    [Fact]
    public void Threshold_IsInclusive()
    {
        var policy = Policy(hits: 1);

        Assert.True(policy.Evaluate(0.7f, 0));
    }


    [Fact]
    public void HitCounter_ResetsOnMiss()
    {
        var policy = Policy(hits: 3);

        Assert.False(policy.Evaluate(0.9f, 0));
        Assert.False(policy.Evaluate(0.9f, Step));
        Assert.False(policy.Evaluate(0.1f, 2 * Step));
        Assert.False(policy.Evaluate(0.9f, 3 * Step));
        Assert.False(policy.Evaluate(0.9f, 4 * Step));
        Assert.True(policy.Evaluate(0.9f, 5 * Step));
    }


    [Fact]
    public void OneMinimumHit_FiresImmediately()
    {
        var policy = Policy(hits: 1);

        Assert.True(policy.Evaluate(0.99f, 0));
    }


    [Fact]
    public void LongUtterance_FiresOnlyOnce_NoDuplicateAfterCooldown()
    {
        // Слово держит score выше порога дольше cooldown:
        // без latch wake выстрелил бы второй раз на "хвосте".
        var policy = Policy(cooldown: 500);

        int fired = 0;

        for (int i = 0; i < 100; i++)
        {
            if (policy.Evaluate(0.95f, i * Step))
                fired++;
        }

        Assert.Equal(1, fired);
    }


    [Fact]
    public void Cooldown_BlocksNewUtteranceUntilExpired()
    {
        var policy = Policy(cooldown: 1500);

        Assert.False(policy.Evaluate(0.9f, 0));
        Assert.True(policy.Evaluate(0.9f, Step));

        // Score упал и снова вырос внутри cooldown.
        Assert.False(policy.Evaluate(0.1f, 2 * Step));

        for (long t = 3 * Step; t < Step + 1500; t += Step)
        {
            Assert.False(policy.Evaluate(0.95f, t));
            Assert.False(policy.Evaluate(0.10f, t + 1));
        }
    }


    [Fact]
    public void AfterCooldown_NewUtterance_Fires()
    {
        var policy = Policy(cooldown: 1500);

        policy.Evaluate(0.9f, 0);
        Assert.True(policy.Evaluate(0.9f, Step));

        policy.Evaluate(0.1f, 2 * Step);

        long later = Step + 1500 + Step;

        Assert.False(policy.Evaluate(0.9f, later));
        Assert.True(policy.Evaluate(0.9f, later + Step));
    }


    [Fact]
    public void HitsDuringCooldown_DoNotAccumulate()
    {
        var policy = Policy(hits: 2, cooldown: 1000);

        policy.Evaluate(0.9f, 0);
        Assert.True(policy.Evaluate(0.9f, Step));

        // Ещё один "хит" ровно до конца cooldown.
        policy.Evaluate(0.1f, 2 * Step);
        Assert.False(policy.Evaluate(0.9f, 900));

        // Первый хит после cooldown — снова только первый.
        Assert.False(policy.Evaluate(0.9f, 1100));
        Assert.True(policy.Evaluate(0.9f, 1180));
    }


    [Fact]
    public void Reset_DropsPendingHits()
    {
        var policy = Policy(hits: 3);

        policy.Evaluate(0.9f, 0);
        policy.Evaluate(0.9f, Step);

        policy.Reset();

        Assert.Equal(0, policy.ConsecutiveHits);
        Assert.False(policy.Evaluate(0.9f, 2 * Step));
        Assert.False(policy.Evaluate(0.9f, 3 * Step));
        Assert.True(policy.Evaluate(0.9f, 4 * Step));
    }


    [Fact]
    public void Reset_KeepsCooldown_ByDefault()
    {
        var policy = Policy(hits: 1, cooldown: 1500);

        Assert.True(policy.Evaluate(0.9f, 0));

        policy.Reset();

        // Duplicate wake одного произнесения после Reset.
        Assert.False(policy.Evaluate(0.9f, 200));
        Assert.False(policy.Evaluate(0.9f, 1400));
        Assert.True(policy.Evaluate(0.9f, 1600));
    }


    [Fact]
    public void Reset_CanClearCooldown_Explicitly()
    {
        var policy = Policy(hits: 1, cooldown: 1500);

        Assert.True(policy.Evaluate(0.9f, 0));

        policy.Reset(clearCooldown: true);

        Assert.True(policy.Evaluate(0.9f, 200));
    }


    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void NonFiniteScore_IsNeverAHit(float score)
    {
        var policy = Policy(hits: 1);

        Assert.False(policy.Evaluate(score, 0));
    }


    [Theory]
    [InlineData(0f, 1, 0)]
    [InlineData(-0.1f, 1, 0)]
    [InlineData(1.1f, 1, 0)]
    [InlineData(float.NaN, 1, 0)]
    [InlineData(0.7f, 0, 0)]
    [InlineData(0.7f, 1, -1)]
    public void InvalidArguments_Throw(
        float threshold,
        int hits,
        int cooldown)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WakeDetectionPolicy(
                threshold,
                hits,
                cooldown));
    }
}
