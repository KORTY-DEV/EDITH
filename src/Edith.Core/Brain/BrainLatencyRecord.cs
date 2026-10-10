using System.Globalization;
using System.Text;

namespace Edith.Core.Brain;

/// <summary>One line of brain-latency.txt: one LLM step (or one timed-out step).</summary>
public sealed record BrainLatencyRecord
{
    public string Profile { get; init; } = "normal";

    public string Model { get; init; } = "";

    /// <summary>"cold" when the model had to be loaded for this step, otherwise "warm".</summary>
    public string State { get; init; } = "warm";

    /// <summary>"cpu", "gpu(100%)", "gpu/cpu(40%)" or "unknown".</summary>
    public string Mode { get; init; } = "unknown";

    public double LoadMs { get; init; }

    public double PromptEvalMs { get; init; }

    public double GenerationMs { get; init; }

    /// <summary>Total time reported by Ollama.</summary>
    public double TotalMs { get; init; }

    /// <summary>Wall-clock time of the HTTP call as seen by EDITH.</summary>
    public double WallMs { get; init; }

    public int PromptTokens { get; init; }

    public int GeneratedTokens { get; init; }

    /// <summary>"0:tool=system_snapshot", "1:answer", "0:none" ...</summary>
    public string ToolStep { get; init; } = "0:none";

    /// <summary>"none", "step-timeout", "total-timeout", "cancelled" or "error".</summary>
    public string TimeoutReason { get; init; } = "none";

    public string Game { get; init; } = "none";

    public string Format(DateTimeOffset at)
    {
        var sb = new StringBuilder();

        void Add(string key, string value) =>
            sb.Append(key).Append('=').Append(value).Append(' ');

        static string Ms(double v) => Math.Round(v).ToString(CultureInfo.InvariantCulture);

        sb.Append(at.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture)).Append(' ');
        Add("profile", Profile);
        Add("model", Model);
        Add("state", State);
        Add("mode", Mode);
        Add("load_ms", Ms(LoadMs));
        Add("prompt_eval_ms", Ms(PromptEvalMs));
        Add("generation_ms", Ms(GenerationMs));
        Add("total_ms", Ms(TotalMs));
        Add("wall_ms", Ms(WallMs));
        Add("prompt_tokens", PromptTokens.ToString(CultureInfo.InvariantCulture));
        Add("generated_tokens", GeneratedTokens.ToString(CultureInfo.InvariantCulture));
        Add("tool_step", ToolStep);
        Add("timeout", TimeoutReason);
        Add("game", Game);

        return sb.ToString().TrimEnd();
    }

    /// <summary>Describes Ollama's placement from /api/ps sizes.</summary>
    public static string DescribeMode(long size, long sizeVram)
    {
        if (size <= 0)
        {
            return "unknown";
        }

        if (sizeVram <= 0)
        {
            return "cpu";
        }

        int pct = (int)Math.Round(100d * sizeVram / size);

        return pct >= 99
            ? "gpu(100%)"
            : $"gpu/cpu({pct}%)";
    }
}
