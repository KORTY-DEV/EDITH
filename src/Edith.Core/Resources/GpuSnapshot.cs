using System.Globalization;

namespace Edith.Core.Resources;

/// <summary>One reading of the (first) NVIDIA GPU.</summary>
public sealed record GpuSnapshot(
    string Name,
    int UtilizationPercent,
    int MemoryUsedMb,
    int MemoryTotalMb)
{
    public int MemoryFreeMb => Math.Max(0, MemoryTotalMb - MemoryUsedMb);
}

/// <summary>
/// Parses <c>nvidia-smi --query-gpu=name,utilization.gpu,memory.used,memory.total
/// --format=csv,noheader,nounits</c>. Also tolerates the header line and unit suffixes
/// ("45 %", "3120 MiB") so the same parser works for the <c>gpu_status</c> tool output.
/// </summary>
public static class NvidiaSmiParser
{
    public const string QueryArguments =
        "--query-gpu=name,utilization.gpu,memory.used,memory.total --format=csv,noheader,nounits";

    public static GpuSnapshot? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return null;

        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("name", StringComparison.OrdinalIgnoreCase))
                continue;

            string[] parts = line.Split(',');
            if (parts.Length < 4)
                continue;

            if (TryNumber(parts[^3], out int utilization) &&
                TryNumber(parts[^2], out int used) &&
                TryNumber(parts[^1], out int total) &&
                total > 0)
            {
                // The name itself may contain commas; everything before the last 3 columns is the name.
                string name = string.Join(',', parts[..^3]).Trim();
                return new GpuSnapshot(name, utilization, used, total);
            }
        }

        return null;
    }

    private static bool TryNumber(string value, out int number)
    {
        string digits = new(value.Trim().TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        bool ok = double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed);
        number = ok ? (int)Math.Round(parsed) : 0;
        return ok;
    }
}
