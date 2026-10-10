namespace Edith.Audio.Echo;

/// <summary>
/// Acoustic echo canceller: takes EDITH's own voice (what the speakers play, the "far end") out of
/// the microphone signal (the "near end"), so a stop word said over her answer can be heard.
/// A partitioned-block frequency-domain adaptive filter (PBFDAF, the family speex and WebRTC build
/// on): 16 kHz mono, blocks of 256 samples, 24 partitions = 384 ms of echo path, per-bin
/// normalised steps, and adaptation frozen while the user talks over the echo (Geigel detector).
/// Linear only, no residual suppression: it removes what a room impulse response can explain.
/// </summary>
public sealed class EchoCanceller
{
    public const int SampleRate = 16000;
    public const int Block = 256;
    private const int Fft = 2 * Block;

    private readonly int _partitions;
    private readonly float _step;

    // Frequency-domain filter weights and far-end spectra history, [partition][bin].
    private readonly float[][] _weightRe, _weightIm, _farRe, _farIm;
    private readonly float[] _power = new float[Block + 1];
    private readonly float[] _prevFar = new float[Block];
    private readonly Fft512 _fft = new();

    // The last half second of the far end, for the double-talk (Geigel) decision.
    private readonly float[] _farPeaks = new float[32];
    private int _peakIndex;

    private const double ConvergedDb = 9;
    private const int HoldBlocks = 10;
    private int _hold, _frozenBlocks;

    private static double Db(double near, double error) => 10 * Math.Log10(Math.Max(near, 1e-12) / Math.Max(error, 1e-12));

    private readonly Queue<float> _nearQueue = new();
    private readonly Queue<float> _farQueue = new();
    private readonly object _gate = new();

    public EchoCanceller(int partitions = 24, float step = 0.4f)
    {
        _partitions = partitions;
        _step = step;
        _weightRe = Create(partitions);
        _weightIm = Create(partitions);
        _farRe = Create(partitions);
        _farIm = Create(partitions);
    }

    /// <summary>Echo return loss enhancement of the last blocks, in dB (how much quieter the residual is than the microphone).</summary>
    public double RecentErleDb { get; private set; }

    /// <summary>Whether the filter currently sees far-end sound (something is playing).</summary>
    public bool FarEndActive { get; private set; }

    /// <summary>
    /// What the speakers play, mono 16 kHz, in time with the microphone stream (silence included:
    /// the caller fills gaps with zeros). Thread-safe against <see cref="ProcessNear"/>.
    /// </summary>
    public void PushFarEnd(ReadOnlySpan<float> samples)
    {
        lock (_gate)
            foreach (float s in samples)
                _farQueue.Enqueue(s);
    }

    /// <summary>
    /// Microphone samples in, the same number of cleaned samples out (delayed by at most one block;
    /// the first block after start-up returns silence while the queue fills).
    /// </summary>
    public float[] ProcessNear(ReadOnlySpan<float> near)
    {
        var output = new List<float>(near.Length);
        lock (_gate)
        {
            foreach (float s in near)
                _nearQueue.Enqueue(s);

            while (_nearQueue.Count >= Block)
            {
                var d = new float[Block];
                var x = new float[Block];
                for (int i = 0; i < Block; i++)
                {
                    d[i] = _nearQueue.Dequeue();
                    // A far end that did not arrive (nothing playing) is silence.
                    x[i] = _farQueue.Count > 0 ? _farQueue.Dequeue() : 0f;
                }

                // The far end must not run ahead of the microphone for long: that would delay it.
                while (_farQueue.Count > SampleRate)
                    _farQueue.Dequeue();

                output.AddRange(ProcessBlock(d, x));
            }
        }

        return [.. output];
    }

    private float[] ProcessBlock(float[] d, float[] x)
    {
        // Far-end spectrum of [previous block, this block].
        var re = new float[Fft];
        var im = new float[Fft];
        Array.Copy(_prevFar, 0, re, 0, Block);
        Array.Copy(x, 0, re, Block, Block);
        Array.Copy(x, _prevFar, Block);
        _fft.Forward(re, im);

        // Newest spectrum goes to slot 0; the others move up.
        float[] lastRe = _farRe[_partitions - 1], lastIm = _farIm[_partitions - 1];
        for (int p = _partitions - 1; p > 0; p--)
        {
            _farRe[p] = _farRe[p - 1];
            _farIm[p] = _farIm[p - 1];
        }

        _farRe[0] = lastRe;
        _farIm[0] = lastIm;
        Array.Copy(re, 0, _farRe[0], 0, Block + 1);
        Array.Copy(im, 0, _farIm[0], 0, Block + 1);

        // Echo estimate: sum over partitions of weights × far spectra, back to time domain.
        var yRe = new float[Fft];
        var yIm = new float[Fft];
        for (int p = 0; p < _partitions; p++)
        {
            float[] wr = _weightRe[p], wi = _weightIm[p], xr = _farRe[p], xi = _farIm[p];
            for (int k = 0; k <= Block; k++)
            {
                yRe[k] += wr[k] * xr[k] - wi[k] * xi[k];
                yIm[k] += wr[k] * xi[k] + wi[k] * xr[k];
            }
        }

        MirrorConjugate(yRe, yIm);
        _fft.Inverse(yRe, yIm);

        var e = new float[Block];
        double nearEnergy = 0, errorEnergy = 0, farEnergy = 0;
        float farPeak = 0, nearPeak = 0;
        for (int i = 0; i < Block; i++)
        {
            e[i] = d[i] - yRe[Block + i]; // overlap-save: the last half is the valid one
            nearEnergy += d[i] * d[i];
            errorEnergy += e[i] * e[i];
            farEnergy += x[i] * x[i];
            farPeak = Math.Max(farPeak, Math.Abs(x[i]));
            nearPeak = Math.Max(nearPeak, Math.Abs(d[i]));
        }

        _farPeaks[_peakIndex] = farPeak;
        _peakIndex = (_peakIndex + 1) % _farPeaks.Length;
        float recentFarPeak = _farPeaks.Max();

        FarEndActive = recentFarPeak > 0.003f;
        bool farPlaying = FarEndActive && farEnergy > 1e-8;

        // Double talk: once the filter has converged, a block whose residual stays as loud as the
        // microphone is not echo (the filter would have removed it): it is the user. Adaptation
        // is frozen for a moment so the user's voice does not teach the filter to cancel it.
        bool converged = RecentErleDb > ConvergedDb;
        if (converged && farPlaying && nearEnergy > 1e-7 && errorEnergy > 0.5 * nearEnergy)
            _hold = HoldBlocks;

        if (_hold > 0)
        {
            _hold--;
            _frozenBlocks++;
            // Frozen for seconds on end: the room or the volume changed, not the user. Learn again.
            if (_frozenBlocks > 4 * SampleRate / Block)
            {
                RecentErleDb = 0;
                _frozenBlocks = 0;
                _hold = 0;
            }
        }
        else
        {
            _frozenBlocks = 0;
            if (farPlaying)
            {
                RecentErleDb = 0.9 * RecentErleDb + 0.1 * Db(nearEnergy, errorEnergy);
                Adapt(e);
            }
        }

        return e;
    }

    private void Adapt(float[] e)
    {
        // Error spectrum: [zeros, error] so that only the valid half drives the update.
        var eRe = new float[Fft];
        var eIm = new float[Fft];
        Array.Copy(e, 0, eRe, Block, Block);
        _fft.Forward(eRe, eIm);

        // Per-bin power of the far end over all partitions: the step is normalised by it.
        for (int k = 0; k <= Block; k++)
        {
            float sum = 0;
            for (int p = 0; p < _partitions; p++)
                sum += _farRe[p][k] * _farRe[p][k] + _farIm[p][k] * _farIm[p][k];
            _power[k] = 0.7f * _power[k] + 0.3f * sum;
        }

        var gRe = new float[Fft];
        var gIm = new float[Fft];
        for (int p = 0; p < _partitions; p++)
        {
            float[] xr = _farRe[p], xi = _farIm[p];
            for (int k = 0; k <= Block; k++)
            {
                float norm = _step / (_power[k] + 1e-3f);
                // conj(X) × E
                gRe[k] = (xr[k] * eRe[k] + xi[k] * eIm[k]) * norm;
                gIm[k] = (xr[k] * eIm[k] - xi[k] * eRe[k]) * norm;
            }

            // Gradient constraint: the update may only be a block-long filter.
            MirrorConjugate(gRe, gIm);
            _fft.Inverse(gRe, gIm);
            Array.Clear(gRe, Block, Block);
            Array.Clear(gIm, 0, Fft);
            _fft.Forward(gRe, gIm);

            float[] wr = _weightRe[p], wi = _weightIm[p];
            for (int k = 0; k <= Block; k++)
            {
                wr[k] += gRe[k];
                wi[k] += gIm[k];
            }
        }
    }

    /// <summary>The spectrum of a real signal is conjugate-symmetric: fill the upper half from the lower.</summary>
    private static void MirrorConjugate(float[] re, float[] im)
    {
        for (int k = 1; k < Block; k++)
        {
            re[Fft - k] = re[k];
            im[Fft - k] = -im[k];
        }
    }

    private static float[][] Create(int partitions)
    {
        var table = new float[partitions][];
        for (int i = 0; i < partitions; i++)
            table[i] = new float[Fft];
        return table;
    }

    /// <summary>In-place radix-2 FFT of size 512 (float).</summary>
    private sealed class Fft512
    {
        private readonly float[] _cos = new float[Fft / 2];
        private readonly float[] _sin = new float[Fft / 2];
        private readonly int[] _reverse = new int[Fft];

        public Fft512()
        {
            for (int i = 0; i < Fft / 2; i++)
            {
                _cos[i] = (float)Math.Cos(2 * Math.PI * i / Fft);
                _sin[i] = (float)-Math.Sin(2 * Math.PI * i / Fft);
            }

            int bits = (int)Math.Log2(Fft);
            for (int i = 0; i < Fft; i++)
            {
                int r = 0;
                for (int b = 0; b < bits; b++)
                    if ((i & (1 << b)) != 0)
                        r |= 1 << (bits - 1 - b);
                _reverse[i] = r;
            }
        }

        public void Forward(float[] re, float[] im) => Transform(re, im, inverse: false);

        public void Inverse(float[] re, float[] im)
        {
            Transform(re, im, inverse: true);
            for (int i = 0; i < Fft; i++)
            {
                re[i] /= Fft;
                im[i] /= Fft;
            }
        }

        private void Transform(float[] re, float[] im, bool inverse)
        {
            for (int i = 0; i < Fft; i++)
            {
                int j = _reverse[i];
                if (j > i)
                {
                    (re[i], re[j]) = (re[j], re[i]);
                    (im[i], im[j]) = (im[j], im[i]);
                }
            }

            for (int size = 2; size <= Fft; size <<= 1)
            {
                int half = size / 2, step = Fft / size;
                for (int start = 0; start < Fft; start += size)
                {
                    for (int k = 0; k < half; k++)
                    {
                        float wr = _cos[k * step];
                        float wi = inverse ? -_sin[k * step] : _sin[k * step];
                        int a = start + k, b = a + half;
                        float tr = re[b] * wr - im[b] * wi;
                        float ti = re[b] * wi + im[b] * wr;
                        re[b] = re[a] - tr;
                        im[b] = im[a] - ti;
                        re[a] += tr;
                        im[a] += ti;
                    }
                }
            }
        }
    }
}
