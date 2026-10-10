using System.Text.Json;

namespace Edith.Core.Resources;

public sealed record OllamaLoadedModel(string Name, long SizeVramBytes);

/// <summary>
/// Reads Ollama's /api/ps. Only EDITH's own models (the ones named in brain.json) count as
/// EDITH's VRAM; any other model loaded in Ollama belongs to someone else and is load like
/// any foreign program.
/// </summary>
public static class OllamaPs
{
    public static IReadOnlyList<OllamaLoadedModel> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) ||
                models.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var result = new List<OllamaLoadedModel>();
            foreach (JsonElement model in models.EnumerateArray())
            {
                string name = model.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                long vram = model.TryGetProperty("size_vram", out JsonElement v) && v.TryGetInt64(out long value) ? value : 0;
                if (name.Length > 0)
                    result.Add(new OllamaLoadedModel(name, vram));
            }

            return result;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>MB of VRAM held by models whose name is one of <paramref name="edithModels"/>.</summary>
    public static int EdithVramMb(IEnumerable<OllamaLoadedModel> loaded, IEnumerable<string> edithModels)
    {
        HashSet<string> own = edithModels
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(Canonical)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        long bytes = loaded.Where(m => own.Contains(Canonical(m.Name))).Sum(m => m.SizeVramBytes);
        return (int)(bytes / (1024 * 1024));
    }

    /// <summary>"qwen3:4b" stays, "mistral" becomes "mistral:latest" — Ollama's own naming.</summary>
    private static string Canonical(string name)
    {
        name = name.Trim();
        return name.Contains(':') ? name : name + ":latest";
    }
}
