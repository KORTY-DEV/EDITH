using System.Speech.Synthesis;

namespace Edith.Speech;

public sealed class WindowsTtsService : IDisposable
{
    private readonly SpeechSynthesizer _synthesizer;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string SelectedVoice { get; private set; }

    public WindowsTtsService()
    {
        _synthesizer =
            new SpeechSynthesizer();

        _synthesizer.SetOutputToDefaultAudioDevice();

        SelectedVoice =
            SelectBestRussianVoice();

        // Чуть спокойнее стандартного системного голоса.
        _synthesizer.Rate = 1;
        _synthesizer.Volume = 90;
    }

    private string SelectBestRussianVoice()
    {
        var voices =
            _synthesizer
                .GetInstalledVoices()
                .Where(v => v.Enabled)
                .ToList();

        var russian =
            voices.FirstOrDefault(
                v =>
                    v.VoiceInfo.Culture.Name
                        .StartsWith(
                            "ru",
                            StringComparison.OrdinalIgnoreCase));

        if (russian is not null)
        {
            _synthesizer.SelectVoice(
                russian.VoiceInfo.Name);

            return russian.VoiceInfo.Name;
        }

        return _synthesizer.Voice.Name;
    }

    public async Task SpeakAsync(
        string text,
        CancellationToken cancellationToken = default,
        Action? firstAudioStarted = null,
        Action? requestStarted = null,
        Action? audioReady = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        await _gate.WaitAsync(
            cancellationToken);

        try
        {
            requestStarted?.Invoke();
            var tcs =
                new TaskCompletionSource(
                    TaskCreationOptions
                        .RunContinuationsAsynchronously);

            EventHandler<SpeakCompletedEventArgs>?
                handler = null;
            EventHandler<SpeakStartedEventArgs>?
                startedHandler = null;

            handler =
                (_, args) =>
                {
                    _synthesizer.SpeakCompleted -=
                        handler;
                    _synthesizer.SpeakStarted -=
                        startedHandler;

                    if (args.Cancelled)
                        tcs.TrySetCanceled();
                    else if (args.Error is not null)
                        tcs.TrySetException(
                            args.Error);
                    else
                        tcs.TrySetResult();
                };

            _synthesizer.SpeakCompleted +=
                handler;

            startedHandler =
                (_, _) => firstAudioStarted?.Invoke();
            _synthesizer.SpeakStarted +=
                startedHandler;

            using var registration =
                cancellationToken.Register(
                    () =>
                    {
                        try
                        {
                            _synthesizer
                                .SpeakAsyncCancelAll();
                        }
                        catch
                        {
                        }
                    });

            audioReady?.Invoke();
            _synthesizer.SpeakAsync(text);

            await tcs.Task;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Stop()
    {
        try
        {
            _synthesizer.SpeakAsyncCancelAll();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        Stop();
        _synthesizer.Dispose();
        _gate.Dispose();
    }
}
