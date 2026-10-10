using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Edith.Audio.Echo;

/// <summary>
/// What the speakers are playing right now (WASAPI loopback of the default output), as mono 16 kHz
/// samples for <see cref="EchoCanceller.PushFarEnd"/>. Everything that plays counts - EDITH's own
/// voice, music, a video - so all of it can be taken out of the microphone signal.
/// </summary>
public sealed class EchoReference : IDisposable
{
    private readonly WasapiLoopbackCapture _capture;
    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _resampled;
    private readonly EchoCanceller _canceller;
    private readonly float[] _read = new float[4096];
    private readonly object _gate = new();

    public EchoReference(EchoCanceller canceller)
    {
        _canceller = canceller;
        _capture = new WasapiLoopbackCapture();
        WaveFormat format = _capture.WaveFormat;
        _channels = format.Channels;
        _bytesPerSample = format.BitsPerSample / 8;
        _isFloat = format.Encoding is WaveFormatEncoding.IeeeFloat ||
                   (format.Encoding is WaveFormatEncoding.Extensible && format.BitsPerSample == 32);

        // The output may be stereo or 7.1: whatever it is, it is averaged to one channel first.
        _buffered = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, 1))
        {
            DiscardOnBufferOverflow = true,
            ReadFully = false,
        };
        _resampled = new WdlResamplingSampleProvider(_buffered.ToSampleProvider(), EchoCanceller.SampleRate);
        _capture.DataAvailable += OnData;
    }

    private readonly int _channels;
    private readonly int _bytesPerSample;
    private readonly bool _isFloat;

    public string FormatDescription => _capture.WaveFormat.ToString();

    public void Start() => _capture.StartRecording();

    public void Stop()
    {
        try { _capture.StopRecording(); } catch { }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        int frameBytes = _channels * _bytesPerSample;
        int frames = e.BytesRecorded / frameBytes;
        var mono = new byte[frames * sizeof(float)];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0;
            for (int c = 0; c < _channels; c++)
            {
                int at = f * frameBytes + c * _bytesPerSample;
                sum += _isFloat
                    ? BitConverter.ToSingle(e.Buffer, at)
                    : _bytesPerSample == 2 ? BitConverter.ToInt16(e.Buffer, at) / 32768f : 0f;
            }

            BitConverter.TryWriteBytes(mono.AsSpan(f * sizeof(float)), sum / _channels);
        }

        lock (_gate)
        {
            _buffered.AddSamples(mono, 0, mono.Length);
            int count;
            while ((count = _resampled.Read(_read.AsSpan())) > 0)
                _canceller.PushFarEnd(_read.AsSpan(0, count));
        }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        Stop();
        _capture.Dispose();
    }
}
