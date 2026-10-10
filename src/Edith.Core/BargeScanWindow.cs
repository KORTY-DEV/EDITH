namespace Edith.Core;

/// <summary>
/// Which stretch of the microphone the "стоп / Эдит" scan may listen to while a command runs: only sound recorded after the owner's own
/// phrase has settled (it once heard "Эдит, покажи…" from the buffer and cancelled the command it started).
/// </summary>
public static class BargeScanWindow
{
    public const int SampleRate = 16000;
    public const int SettleSamples = SampleRate * 2;
    public const int MinSamples = SampleRate / 2;
    public const int MaxSamples = SampleRate * 3;

    /// <summary>First sample the scan may use for a command that ended at <paramref name="totalSamples"/>.</summary>
    public static long FloorAfterCommand(long totalSamples) => totalSamples + SettleSamples;

    /// <summary>How many of the latest samples to decode; 0 while there is too little new sound.</summary>
    public static int Available(long totalSamples, long floor)
    {
        long fresh = totalSamples - floor;
        return fresh < MinSamples ? 0 : (int)Math.Min(MaxSamples, fresh);
    }
}
