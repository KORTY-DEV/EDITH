using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Edith.Audio.Wake;

// Stage 2 is intentionally a compact ONNX classifier over the exact 16x96
// streaming embedding sequence that produced the Stage 1 candidate. It never
// captures audio and never launches Python in the production process.
public sealed class OnnxWakeVerifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _input;
    private readonly string _output;
    private readonly int _frames;
    private readonly float _threshold;
    private bool _disposed;

    public OnnxWakeVerifier(string path, int frames, float threshold)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("wake-v3 verifier model not found", path);
        if (!(threshold > 0 && threshold <= 1))
            throw new ArgumentOutOfRangeException(nameof(threshold));

        _session = new InferenceSession(path, new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL
        });

        if (_session.InputMetadata.Count != 1 || _session.OutputMetadata.Count != 1)
            throw new InvalidOperationException("wake-v3 verifier must have exactly one input and one output");

        var input = _session.InputMetadata.Single();
        var output = _session.OutputMetadata.Single();
        int[] dims = input.Value.Dimensions;
        int[] outDims = output.Value.Dimensions;
        if (input.Value.ElementType != typeof(float) ||
            dims.Length != 3 || dims[0] != 1 || dims[1] != frames || dims[2] != WakeContract.EmbeddingDim ||
            output.Value.ElementType != typeof(float) ||
            outDims.Length != 2 || outDims[0] != 1 || outDims[1] != 1)
        {
            throw new InvalidOperationException(
                $"wake-v3 verifier contract must be float [1,{frames},96] -> [1,1]");
        }

        _input = input.Key;
        _output = output.Key;
        _frames = frames;
        _threshold = threshold;

        // A bad personal model must never make quiet startup audio a wake.
        WakeVerifierResult baseline = Verify(new float[frames * WakeContract.EmbeddingDim]);
        if (baseline.Accepted)
            throw new InvalidOperationException("wake-v3 verifier accepts baseline embeddings");
    }

    public WakeVerifierResult Verify(ReadOnlySpan<float> features)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(OnnxWakeVerifier));
        if (features.Length != _frames * WakeContract.EmbeddingDim)
            throw new ArgumentException("unexpected embedding sequence length", nameof(features));

        long start = Stopwatch.GetTimestamp();
        var tensor = new DenseTensor<float>(features.ToArray(), [1, _frames, WakeContract.EmbeddingDim]);
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> result =
            _session.Run([NamedOnnxValue.CreateFromTensor(_input, tensor)]);
        float score = result.Single(x => x.Name == _output).AsEnumerable<float>().Single();
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

        if (!float.IsFinite(score) || score < -0.001f || score > 1.001f)
            throw new InvalidOperationException($"wake-v3 verifier returned invalid probability: {score}");

        score = Math.Clamp(score, 0, 1);
        return new WakeVerifierResult(score, score >= _threshold, elapsed);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.Dispose();
    }
}


public readonly record struct WakeVerifierResult(
    float Score,
    bool Accepted,
    double Milliseconds);
