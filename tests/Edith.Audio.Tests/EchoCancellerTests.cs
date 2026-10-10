using Edith.Audio.Echo;

namespace Edith.Audio.Tests;

public sealed class EchoCancellerTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const int Rate = 16000;

    // Deterministic noise (the same on every run).
    private sealed class Noise(uint seed)
    {
        private uint _s = seed;

        public float Next()
        {
            _s = _s * 1664525u + 1013904223u;
            return (_s >> 8) / (float)(1 << 24) * 2f - 1f;
        }
    }

    /// <summary>Speech-like far end: coloured noise whose loudness swells and fades about four times a second.</summary>
    private static float[] FarEnd(int seconds)
    {
        var noise = new Noise(7);
        var x = new float[seconds * Rate];
        float lowpass = 0;
        for (int i = 0; i < x.Length; i++)
        {
            lowpass += 0.45f * (noise.Next() - lowpass);
            float envelope = 0.55f + 0.45f * MathF.Sin(2 * MathF.PI * 4f * i / Rate);
            x[i] = 0.5f * lowpass * envelope;
        }

        return x;
    }

    /// <summary>A desk: 75 ms to the microphone, then a decaying room tail of 250 ms; gain well below 1.</summary>
    private static float[] RoomResponse()
    {
        var noise = new Noise(99);
        var h = new float[5200];
        for (int i = 0; i < 4000; i++)
            h[1200 + i] = 0.35f * MathF.Exp(-i / 700f) * noise.Next();
        h[1200] = 0.5f;
        return h;
    }

    private static float[] Convolve(float[] x, float[] h)
    {
        var y = new float[x.Length];
        for (int n = 0; n < x.Length; n++)
        {
            double sum = 0;
            int kMax = Math.Min(h.Length - 1, n);
            for (int k = 0; k <= kMax; k++)
                sum += h[k] * x[n - k];
            y[n] = (float)sum;
        }

        return y;
    }

    private static double Db(double ratio) => 10 * Math.Log10(Math.Max(ratio, 1e-12));

    private static (float[] Cleaned, EchoCanceller Canceller) Run(float[] far, float[] near)
    {
        var canceller = new EchoCanceller();
        var cleaned = new List<float>();
        for (int at = 0; at < near.Length; at += 160) // 10 ms pieces, as a microphone delivers them
        {
            int n = Math.Min(160, near.Length - at);
            canceller.PushFarEnd(far.AsSpan(at, n));
            cleaned.AddRange(canceller.ProcessNear(near.AsSpan(at, n)));
        }

        return ([.. cleaned], canceller);
    }

    [Fact]
    public void RemovesEchoOfAKnownRoom()
    {
        float[] far = FarEnd(25);
        float[] echo = Convolve(far, RoomResponse());

        (float[] cleaned, EchoCanceller canceller) = Run(far, echo);

        // After it has converged (last 5 s) the residual is far below the echo that came in.
        int from = 20 * Rate;
        double before = 0, after = 0;
        for (int i = from; i < cleaned.Length; i++)
        {
            before += echo[i] * echo[i];
            after += cleaned[i] * cleaned[i];
        }

        double erle = Db(before / after);
        output.WriteLine($"ERLE after convergence: {erle:F1} dB");
        Assert.True(erle > 18, $"echo reduced by only {erle:F1} dB");
        Assert.True(canceller.FarEndActive);
    }

    [Fact]
    public void KeepsTheUsersVoiceWhileEchoIsRemoved()
    {
        float[] far = FarEnd(30);
        float[] echo = Convolve(far, RoomResponse());

        // The user says something loud over the answer in the last 4 seconds.
        var voice = new float[far.Length];
        for (int i = 26 * Rate; i < far.Length; i++)
            voice[i] = 0.45f * MathF.Sin(2 * MathF.PI * 440f * i / Rate) * (0.6f + 0.4f * MathF.Sin(2 * MathF.PI * 3f * i / Rate));
        var near = new float[far.Length];
        for (int i = 0; i < near.Length; i++)
            near[i] = echo[i] + voice[i];

        (float[] cleaned, _) = Run(far, near);

        // Inside the double-talk window: the cleaned signal is the voice (not the echo).
        int from = 27 * Rate, to = 30 * Rate;
        double voiceEnergy = 0, residualEcho = 0, error = 0;
        for (int i = from; i < to; i++)
        {
            voiceEnergy += voice[i] * voice[i];
            residualEcho += (cleaned[i] - voice[i]) * (cleaned[i] - voice[i]);
            error += echo[i] * echo[i];
        }

        double kept = Db(voiceEnergy / residualEcho);
        double echoAfter = Db(error / residualEcho);
        output.WriteLine($"voice vs what is left besides it: {kept:F1} dB; echo reduced by {echoAfter:F1} dB");
        Assert.True(kept > 10, $"the voice is only {kept:F1} dB above the remaining echo and distortion");
    }

    [Fact]
    public void SilentFarEnd_LeavesTheMicrophoneAlone()
    {
        var noise = new Noise(5);
        var near = new float[3 * Rate];
        for (int i = 0; i < near.Length; i++)
            near[i] = 0.2f * noise.Next();

        (float[] cleaned, EchoCanceller canceller) = Run(new float[near.Length], near);

        Assert.False(canceller.FarEndActive);
        for (int i = 0; i < cleaned.Length; i++)
            Assert.Equal(near[i], cleaned[i], 5);
    }
}
