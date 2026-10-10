using Edith.Core.Memory;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Edith.Audio.Embedding;

/// <summary>
/// multilingual-e5-small (int8 ONNX, 384 dimensions, CPU): meaning of a text as a vector, so that
/// "что у меня по улью" finds a note about ULEY even without a shared word. Texts get the e5
/// prefixes ("query: " for questions, "passage: " for stored text); mean pooling, unit length.
/// </summary>
public sealed class E5Embedder : ITextEmbedder, IDisposable
{
    public const int MaxTokens = 256;

    private readonly InferenceSession _session;
    private readonly UnigramTokenizer _tokenizer;
    private readonly object _gate = new();

    private E5Embedder(InferenceSession session, UnigramTokenizer tokenizer)
    {
        _session = session;
        _tokenizer = tokenizer;
    }

    public int Dimensions => 384;

    /// <summary>Null when the model files are missing: search then stays on words.</summary>
    public static E5Embedder? TryCreate(string folder)
    {
        try
        {
            string model = Path.Combine(folder, "model_quantized.onnx");
            string tokenizer = Path.Combine(folder, "tokenizer.json");
            if (!File.Exists(model) || !File.Exists(tokenizer))
                return null;

            // Two threads: a note takes ~30 ms, and the game or the voice must not feel it.
            using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
            return new E5Embedder(new InferenceSession(model, options), UnigramTokenizer.Load(tokenizer));
        }
        catch
        {
            return null;
        }
    }

    public float[] EmbedQuery(string text) => Embed("query: " + text);

    public float[] EmbedPassage(string text) => Embed("passage: " + text);

    /// <summary>Raw text, prefix included (tests compare with the reference implementation).</summary>
    public float[] Embed(string prefixedText)
    {
        int[] ids = _tokenizer.Encode(prefixedText, MaxTokens);
        int n = ids.Length;

        var inputIds = new DenseTensor<long>([1, n]);
        var mask = new DenseTensor<long>([1, n]);
        var types = new DenseTensor<long>([1, n]);
        for (int i = 0; i < n; i++)
        {
            inputIds[0, i] = ids[i];
            mask[0, i] = 1;
        }

        float[] hidden;
        lock (_gate)
        {
            using var results = _session.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                NamedOnnxValue.CreateFromTensor("attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("token_type_ids", types),
            ]);
            hidden = results.First().AsTensor<float>().ToArray();
        }

        // Mean over the tokens, then unit length.
        var vector = new float[Dimensions];
        for (int t = 0; t < n; t++)
            for (int d = 0; d < Dimensions; d++)
                vector[d] += hidden[t * Dimensions + d];

        double norm = 0;
        for (int d = 0; d < Dimensions; d++)
        {
            vector[d] /= n;
            norm += vector[d] * vector[d];
        }

        float scale = (float)(1.0 / Math.Max(Math.Sqrt(norm), 1e-12));
        for (int d = 0; d < Dimensions; d++)
            vector[d] *= scale;
        return vector;
    }

    public void Dispose() => _session.Dispose();
}
