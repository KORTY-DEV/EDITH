using System.Numerics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Edith.Audio;

/// <summary>
/// Smart Turn v3.2 (pipecat, BSD-2): did the speaker finish, judging by how the last seconds
/// SOUND (falling pitch, finished phrase) rather than by silence length alone. 8 MB int8 ONNX,
/// ~40 ms on the CPU. Input: the last 8 s of 16 kHz mono audio as a Whisper log-mel.
/// </summary>
public sealed class SmartTurnDetector : IDisposable
{
    public const int SampleRate = 16000;
    public const int WindowSamples = 8 * SampleRate;

    private readonly InferenceSession _session;

    private SmartTurnDetector(InferenceSession session) => _session = session;

    /// <summary>Null when the model file is missing or does not load: callers fall back to pause length.</summary>
    public static SmartTurnDetector? TryCreate(string modelPath)
    {
        try
        {
            if (!File.Exists(modelPath))
                return null;

            using var options = new SessionOptions
            {
                InterOpNumThreads = 1,
                IntraOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            return new SmartTurnDetector(new InferenceSession(modelPath, options));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Probability (0..1) that the phrase is complete; the newest audio is at the end.</summary>
    public float Predict(ReadOnlySpan<float> audio)
    {
        float[] features = WhisperLogMel.Compute(audio, WindowSamples);
        var tensor = new DenseTensor<float>(features, [1, WhisperLogMel.MelBins, WhisperLogMel.Frames]);
        using var results = _session.Run([NamedOnnxValue.CreateFromTensor("input_features", tensor)]);
        return results.First().AsEnumerable<float>().First();
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>
/// Whisper's feature extractor (n_fft 400, hop 160, 80 slaney mels, log10, clamp to max-8, (x+4)/4)
/// as HF's WhisperFeatureExtractor(chunk_length=8, do_normalize=True) computes it. The audio is
/// placed at the END of an 8 s window (zeros in front) and normalised over the whole window.
/// </summary>
public static class WhisperLogMel
{
    public const int MelBins = 80;
    public const int Frames = 800;
    private const int NFft = 400;
    private const int Hop = 160;
    private const int Bins = NFft / 2 + 1;

    private static readonly float[] Window = BuildWindow();
    private static readonly float[] Cos = BuildTable(Math.Cos);
    private static readonly float[] Sin = BuildTable(Math.Sin);
    private static readonly float[] Filters = BuildMelFilters();

    public static float[] Compute(ReadOnlySpan<float> audio, int windowSamples)
    {
        var padded = new float[windowSamples];
        int take = Math.Min(audio.Length, windowSamples);
        audio[^take..].CopyTo(padded.AsSpan(windowSamples - take));
        int silence = windowSamples - take;

        // zero_mean_unit_var_norm over the whole window, padding included.
        double mean = 0;
        foreach (float v in padded) mean += v;
        mean /= windowSamples;
        double variance = 0;
        foreach (float v in padded) variance += (v - mean) * (v - mean);
        variance /= windowSamples;
        float scale = (float)(1.0 / Math.Sqrt(variance + 1e-7));
        for (int i = 0; i < padded.Length; i++)
            padded[i] = (float)(padded[i] - mean) * scale;

        var mel = new float[MelBins * Frames];
        var frame = new float[NFft];
        var power = new float[Bins];

        // Frames lying wholly in the leading silence are identical: compute one.
        float[]? silentColumn = null;
        for (int f = 0; f < Frames; f++)
        {
            int start = f * Hop - NFft / 2;
            bool inSilence = silence > 0 && f * Hop + NFft / 2 <= silence;
            if (inSilence && silentColumn is not null)
            {
                for (int m = 0; m < MelBins; m++)
                    mel[m * Frames + f] = silentColumn[m];
                continue;
            }

            for (int i = 0; i < NFft; i++)
                frame[i] = padded[Reflect(start + i, windowSamples)] * Window[i];

            for (int b = 0; b < Bins; b++)
            {
                float re = Dot(frame, Cos, b * NFft);
                float im = Dot(frame, Sin, b * NFft);
                power[b] = re * re + im * im;
            }

            var column = new float[MelBins];
            for (int m = 0; m < MelBins; m++)
            {
                float sum = Dot(power, Filters, m * Bins);
                column[m] = MathF.Log10(MathF.Max(sum, 1e-10f));
                mel[m * Frames + f] = column[m];
            }

            if (inSilence)
                silentColumn = column;
        }

        float max = float.MinValue;
        foreach (float v in mel) if (v > max) max = v;
        float floor = max - 8.0f;
        for (int i = 0; i < mel.Length; i++)
            mel[i] = (MathF.Max(mel[i], floor) + 4.0f) / 4.0f;

        return mel;
    }

    private static int Reflect(int index, int length)
    {
        if (index < 0) index = -index;
        if (index >= length) index = 2 * (length - 1) - index;
        return index;
    }

    private static float Dot(float[] a, float[] b, int offsetB)
    {
        int n = a.Length;
        int width = Vector<float>.Count;
        var sum = Vector<float>.Zero;
        int i = 0;
        for (; i <= n - width; i += width)
            sum += new Vector<float>(a, i) * new Vector<float>(b, offsetB + i);
        float result = Vector.Dot(sum, Vector<float>.One);
        for (; i < n; i++)
            result += a[i] * b[offsetB + i];
        return result;
    }

    // Periodic Hann, like scipy/numpy window_function("hann").
    private static float[] BuildWindow()
    {
        var w = new float[NFft];
        for (int i = 0; i < NFft; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / NFft));
        return w;
    }

    private static float[] BuildTable(Func<double, double> trig)
    {
        var table = new float[Bins * NFft];
        for (int b = 0; b < Bins; b++)
            for (int n = 0; n < NFft; n++)
                table[b * NFft + n] = (float)trig(2 * Math.PI * b * n / NFft);
        return table;
    }

    // Slaney mel scale and slaney-normalised triangles, as librosa/HF build them.
    private static float[] BuildMelFilters()
    {
        const double fSp = 200.0 / 3.0, minLogHz = 1000.0, minLogMel = minLogHz / fSp;
        double logStep = Math.Log(6.4) / 27.0;

        static double ToMel(double hz, double fSp, double minLogHz, double minLogMel, double logStep) =>
            hz >= minLogHz ? minLogMel + Math.Log(hz / minLogHz) / logStep : hz / fSp;
        static double ToHz(double mel, double fSp, double minLogHz, double minLogMel, double logStep) =>
            mel >= minLogMel ? minLogHz * Math.Exp(logStep * (mel - minLogMel)) : mel * fSp;

        double lowMel = ToMel(0, fSp, minLogHz, minLogMel, logStep);
        double highMel = ToMel(8000, fSp, minLogHz, minLogMel, logStep);
        var edges = new double[MelBins + 2];
        for (int i = 0; i < edges.Length; i++)
            edges[i] = ToHz(lowMel + (highMel - lowMel) * i / (MelBins + 1), fSp, minLogHz, minLogMel, logStep);

        var filters = new float[MelBins * Bins];
        for (int m = 0; m < MelBins; m++)
        {
            double enorm = 2.0 / (edges[m + 2] - edges[m]);
            for (int b = 0; b < Bins; b++)
            {
                double freq = 8000.0 * b / (Bins - 1);
                double down = (freq - edges[m]) / (edges[m + 1] - edges[m]);
                double up = (edges[m + 2] - freq) / (edges[m + 2] - edges[m + 1]);
                filters[m * Bins + b] = (float)(Math.Max(0, Math.Min(down, up)) * enorm);
            }
        }

        return filters;
    }
}
