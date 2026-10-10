using NAudio.Wave;

namespace Edith.Speech;

/// <summary>
/// Plays one WAV file on the default output and can be stopped at once from any thread.
/// (winmm PlaySound could not: stopping it from another thread waited for the sound to end.)
/// </summary>
public sealed class WavPlayer
{
    private readonly object _gate = new();
    private WaveOutEvent? _output;

    /// <summary>
    /// Loudness of what is being played (0..1, about 20 times a second; 0 when playback ends): the orb moves with her voice.
    /// Raised on the playback thread.
    /// </summary>
    public static event Action<float>? LevelChanged;

    /// <summary>Returns when the file has played to the end; throws <see cref="OperationCanceledException"/> when stopped.</summary>
    public async Task PlayAsync(string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var reader = new AudioFileReader(file);
        using var output = new WaveOutEvent();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, e) =>
        {
            if (e.Exception is not null)
                finished.TrySetException(e.Exception);
            else
                finished.TrySetResult();
        };

        // A meter between the file and the speakers: it only measures, the sound is not changed.
        var meter = new NAudio.Wave.SampleProviders.MeteringSampleProvider(reader, Math.Max(1, reader.WaveFormat.SampleRate / 20));
        meter.StreamVolume += (_, e) => LevelChanged?.Invoke(e.MaxSampleValues.Length == 0 ? 0 : e.MaxSampleValues.Max());
        output.Init(meter);
        lock (_gate)
            _output = output;

        try
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(Stop);
            output.Play();
            await finished.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_output, output))
                    _output = null;
            }

            LevelChanged?.Invoke(0);
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>Silences whatever is playing right now.</summary>
    public void Stop()
    {
        WaveOutEvent? output;
        lock (_gate)
            output = _output;

        try
        {
            output?.Stop();
        }
        catch
        {
            // Already stopped or disposed: nothing left to silence.
        }
    }
}
