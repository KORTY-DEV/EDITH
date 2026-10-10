using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SherpaOnnx;

namespace Edith.Audio;

public sealed class MicrophoneMonitor : IDisposable
{
    private const int TargetSampleRate = 16000;
    private const int VadWindowSize = 512;

    private const string PreferredDevice =
        "MCHOSE S9 PRO";

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;

    private readonly BufferedWaveProvider _buffered;
    private readonly ISampleProvider _resampled;

    private readonly VoiceActivityDetector _vad;

    private readonly SemaphoreSlim _dataSignal =
        new(0, 1);

    private readonly CancellationTokenSource _cts =
        new();

    private readonly float[] _readBuffer =
        new float[4096];

    private readonly float[] _vadWindow =
        new float[VadWindowSize];

    private int _vadWindowFill;

    private Task? _pumpTask;

    private bool _isSpeech;
    private bool _started;

    public string DeviceName =>
        _device.FriendlyName;

    public string CaptureFormat =>
        _capture.WaveFormat.ToString();

    public bool IsSpeech =>
        _isSpeech;

    // Поток уже:
    // mono
    // float
    // 16 kHz
    public event Action<float[], int>? SamplesReady;

    public event Action? SpeechStarted;
    public event Action? SpeechEnded;

    public MicrophoneMonitor(double speechEndSilenceSeconds = 0.85)
    {
        var enumerator =
            new MMDeviceEnumerator();

        var devices =
            enumerator.EnumerateAudioEndPoints(
                DataFlow.Capture,
                DeviceState.Active);

        MMDevice? preferred =
            null;

        foreach (var device in devices)
        {
            if (preferred is null &&
                device.FriendlyName.Contains(
                    PreferredDevice,
                    StringComparison.OrdinalIgnoreCase))
            {
                preferred = device;
            }
            else
            {
                device.Dispose();
            }
        }

        // The usual microphone is not there (unplugged, not enumerated yet after boot or sleep):
        // use the system's default one rather than stay deaf.
        if (preferred is null)
        {
            try
            {
                preferred = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                // no capture device at all
            }
        }

        enumerator.Dispose();

        if (preferred is null)
        {
            throw new InvalidOperationException(
                "E.D.I.T.H. не нашла микрофон (ни MCHOSE S9 PRO, ни устройство по умолчанию).");
        }

        _device =
            preferred;

        _capture =
            new WasapiCapture(_device);

        _buffered =
            new BufferedWaveProvider(
                _capture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = false
            };

        ISampleProvider source =
            _buffered.ToSampleProvider();

        if (source.WaveFormat.Channels == 2)
        {
            source =
                new StereoToMonoSampleProvider(
                    source)
                {
                    LeftVolume = 0.5f,
                    RightVolume = 0.5f
                };
        }
        else if (source.WaveFormat.Channels != 1)
        {
            throw new NotSupportedException(
                $"Unsupported channel count: " +
                $"{source.WaveFormat.Channels}");
        }

        _resampled =
            new WdlResamplingSampleProvider(
                source,
                TargetSampleRate);

        var config =
            new VadModelConfig();

        config.SampleRate =
            TargetSampleRate;

        config.SileroVad.Model =
            @"C:\EDITH\models\vad\silero_vad.onnx";

        config.SileroVad.Threshold =
            0.62f;

        config.SileroVad.MinSpeechDuration =
            0.10f;

        // A 250 ms pause was treated as end-of-command.  It made normal
        // speech feel clipped, especially just after a wake word.
        config.SileroVad.MinSilenceDuration =
            Math.Clamp((float)speechEndSilenceSeconds, 0.35f, 3.0f);

        config.SileroVad.MaxSpeechDuration =
            30.0f;

        config.SileroVad.WindowSize =
            VadWindowSize;

        config.Debug = 0;

        _vad =
            new VoiceActivityDetector(
                config,
                10);

        _capture.DataAvailable +=
            OnDataAvailable;
    }

    public void Start()
    {
        if (_started)
            return;

        _started = true;

        _pumpTask =
            Task.Run(
                PumpAudioAsync);

        _capture.StartRecording();
    }

    public void Stop()
    {
        if (!_started)
            return;

        _started = false;

        try
        {
            _capture.StopRecording();
        }
        catch
        {
        }

        _cts.Cancel();

        try
        {
            if (_dataSignal.CurrentCount == 0)
                _dataSignal.Release();
        }
        catch
        {
        }

        try
        {
            _pumpTask?.Wait(
                TimeSpan.FromMilliseconds(700));
        }
        catch
        {
        }
    }

    private void OnDataAvailable(
        object? sender,
        WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
            return;

        _buffered.AddSamples(
            e.Buffer,
            0,
            e.BytesRecorded);

        try
        {
            if (_dataSignal.CurrentCount == 0)
                _dataSignal.Release();
        }
        catch
        {
        }
    }

    private async Task PumpAudioAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _dataSignal.WaitAsync(
                    _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (!_cts.IsCancellationRequested)
            {
                int count;

                try
                {
                    count =
                        _resampled.Read(
                            _readBuffer.AsSpan());
                }
                catch
                {
                    break;
                }

                if (count <= 0)
                    break;

                ProcessSamples(
                    _readBuffer,
                    count);

                if (_buffered.BufferedBytes == 0)
                    break;
            }
        }
    }

    private void ProcessSamples(
        float[] samples,
        int count)
    {
        // Wake-word получает непрерывный поток.
        // Событие синхронное:
        // consumer не должен хранить ссылку на samples.
        SamplesReady?.Invoke(
            samples,
            count);

        for (int i = 0; i < count; i++)
        {
            _vadWindow[_vadWindowFill++] =
                Math.Clamp(
                    samples[i],
                    -1.0f,
                    1.0f);

            if (_vadWindowFill <
                VadWindowSize)
            {
                continue;
            }

            _vad.AcceptWaveform(
                _vadWindow);

            _vadWindowFill = 0;

            bool speechNow =
                _vad.IsSpeechDetected();

            if (speechNow &&
                !_isSpeech)
            {
                _isSpeech = true;
                SpeechStarted?.Invoke();
            }
            else if (!speechNow &&
                     _isSpeech)
            {
                _isSpeech = false;
                SpeechEnded?.Invoke();
            }

            while (!_vad.IsEmpty())
            {
                _vad.Pop();
            }
        }
    }

    public void Dispose()
    {
        Stop();

        _capture.DataAvailable -=
            OnDataAvailable;

        _capture.Dispose();
        _vad.Dispose();
        _device.Dispose();

        _dataSignal.Dispose();
        _cts.Dispose();
    }
}
