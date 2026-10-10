namespace Edith.Core.Memory;

/// <summary>Meaning of a text as a unit-length vector (implemented in Edith.Audio, which owns ONNX Runtime).</summary>
public interface ITextEmbedder
{
    int Dimensions { get; }

    /// <summary>A question or search phrase.</summary>
    float[] EmbedQuery(string text);

    /// <summary>A stored text: a note section, a remembered fact.</summary>
    float[] EmbedPassage(string text);
}

/// <summary>
/// Where the notes index and the memory get the embedder: the app plugs in the ONNX model; while it
/// is missing or still loading the provider answers null and search stays on words.
/// </summary>
public static class EmbeddingService
{
    public static Func<ITextEmbedder?> Provider { get; set; } = () => null;
}

/// <summary>Vector helpers shared by the notes index and the memory.</summary>
public static class Vectors
{
    /// <summary>Cosine of two unit vectors.</summary>
    public static float Dot(float[] a, float[] b)
    {
        float sum = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
            sum += a[i] * b[i];
        return sum;
    }

    public static byte[] ToBytes(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static float[] FromBytes(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    /// <summary>
    /// Reciprocal rank fusion: merges rankings (best first) from word search and meaning search
    /// without comparing their scores, which live on different scales.
    /// </summary>
    public static IReadOnlyList<T> Fuse<T>(IEnumerable<IReadOnlyList<T>> rankings, int take) where T : notnull
    {
        const double k = 60;
        var score = new Dictionary<T, double>();
        foreach (IReadOnlyList<T> ranking in rankings)
            for (int i = 0; i < ranking.Count; i++)
                score[ranking[i]] = score.GetValueOrDefault(ranking[i]) + 1.0 / (k + i + 1);

        return score.OrderByDescending(p => p.Value).Take(take).Select(p => p.Key).ToList();
    }
}
