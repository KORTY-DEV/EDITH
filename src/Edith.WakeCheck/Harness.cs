using System.Diagnostics;
using System.Globalization;
using Edith.Audio;
using Edith.Audio.Wake;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Vosk;

namespace Edith.WakeCheck;

// Упрощённый стенд поверх НАСТОЯЩЕГО WakeRuntime + Vosk + (для live)
// MicrophoneMonitor. Нужен, чтобы проверить wake-runtime без запуска
// WPF-приложения (например, когда рабочий EDITH уже запущен и держит
// hotkey / DLL). Логика session здесь намеренно минимальная и
// зеркалит только wake-часть App.
internal static class Harness
{
    private const string VoskModelPath =
        @"C:\EDITH\models\wake\vosk-model-small-ru-0.22";

    private static WakeRuntime CreateRuntime(
        Dictionary<string, string> options,
        out Model model)
    {
        Vosk.Vosk.SetLogLevel(-1);

        model =
            new Model(VoskModelPath);

        return WakeRuntime.Create(
            new WakeWordDetector(model),
            new WakeRuntimeOptions
            {
                ConfigPath =
                    options.GetValueOrDefault(
                        "config",
                        WakePaths.ResolveConfigFile()),

                DiagnosticsDirectory =
                    options.GetValueOrDefault(
                        "diag",
                        WakePaths.DiagnosticsDirectory),

                PollIntervalMs =
                    options.ContainsKey("poll")
                        ? int.Parse(
                            options["poll"],
                            CultureInfo.InvariantCulture)
                        : 0
            });
    }


    // Прогоняет WAV: тишина -> запись -> тишина, как это увидел бы App.
    public static int RunWav(
        Dictionary<string, string> options)
    {
        if (!options.TryGetValue("wav", out string? wav))
            return 2;

        using var runtime =
            CreateRuntime(options, out Model model);

        using (model)
        {
            Console.WriteLine(
                $"engine={runtime.EngineLabel}");

            runtime.Start();

            bool active = false;
            int wakes = 0;

            runtime.WakeDetected += d =>
            {
                wakes++;
                active = true;

                Console.WriteLine(
                    $"  WAKE engine={d.Engine} " +
                    $"score={d.Score:F3} text='{d.Text}'");
            };

            float[] audio =
                LoadWithPadding(wav, 1.5, 2.0);

            var chunk = new float[1280];

            bool speech = false;
            int quiet = 0;

            for (int i = 0;
                 i + 1280 <= audio.Length;
                 i += 1280)
            {
                Array.Copy(audio, i, chunk, 0, 1280);

                double rms = Rms(chunk);

                if (active)
                    runtime.AcceptActiveAudio(chunk, 1280);
                else
                    runtime.AcceptWakeAudio(chunk, 1280, speech);

                if (rms > 0.01)
                {
                    quiet = 0;

                    if (!speech)
                    {
                        speech = true;

                        if (!active)
                            runtime.NotifySpeechStarted();
                    }
                }
                else if (speech && ++quiet >= 4)
                {
                    speech = false;
                    quiet = 0;

                    if (active || runtime.UsesTextWakeHeuristics)
                    {
                        string phrase =
                            runtime.FinishUtterance();

                        bool ok =
                            runtime.TryExtractCommand(
                                phrase,
                                out string command);

                        Console.WriteLine(
                            $"  speech-end phrase='{phrase}' " +
                            $"wake-command={ok} " +
                            $"command='{command}'");

                        runtime.Reset();

                        if (active)
                        {
                            active = false;
                        }
                    }
                }
            }

            Console.WriteLine($"wakes={wakes}");

            return 0;
        }
    }


    // Живой микрофон MCHOSE + настоящий runtime.
    public static int RunLive(
        Dictionary<string, string> options)
    {
        int seconds =
            options.ContainsKey("seconds")
                ? int.Parse(
                    options["seconds"],
                    CultureInfo.InvariantCulture)
                : 15;

        using var runtime =
            CreateRuntime(options, out Model model);

        using (model)
        using (var mic = new MicrophoneMonitor())
        {
            Console.WriteLine($"engine={runtime.EngineLabel}");
            Console.WriteLine($"mic={mic.DeviceName} {mic.CaptureFormat}");

            long chunks = 0;
            int wakes = 0;
            int voskHits = 0;
            int weakVoskCustom = 0;

            runtime.WakeDetected += d =>
            {
                wakes++;

                if (d.Source is "vosk" or "strong-vosk")
                    Interlocked.Increment(ref voskHits);

                Console.WriteLine(
                    $"  WAKE engine={d.Engine} score={d.Score:F3}");

                // как в App: после wake сессия, затем Reset
                Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    runtime.Reset();
                });
            };

            bool verbose =
                options.ContainsKey("verbose");

            double secondRms = 0;
            float secondScore = 0;
            long secondStart = Stopwatch.GetTimestamp();

            mic.SamplesReady += (s, n) =>
            {
                chunks++;
                runtime.AcceptWakeAudio(s, n, mic.IsSpeech);

                if (!verbose)
                    return;

                double sum = 0;

                for (int i = 0; i < n; i++)
                    sum += s[i] * s[i];

                secondRms =
                    Math.Max(secondRms, Math.Sqrt(sum / n));

                secondScore =
                    Math.Max(
                        secondScore,
                        runtime.DetectorStatus?.LastScore ?? 0);

                if (Stopwatch.GetElapsedTime(secondStart)
                    .TotalSeconds >= 1)
                {
                    Console.WriteLine(
                        $"  t={chunks} rms={secondRms:F4} " +
                        $"score={secondScore:F3} " +
                        $"speech={mic.IsSpeech}");

                    secondRms = 0;
                    secondScore = 0;
                    secondStart = Stopwatch.GetTimestamp();
                }
            };

            mic.SpeechStarted += runtime.NotifySpeechStarted;
            mic.SpeechEnded += () =>
            {
                string final = runtime.FinishUtterance();
                if (runtime.TryAcceptWeakVoskFinal(final, out WakeDetection fusion))
                {
                    Interlocked.Increment(ref wakes);
                    Interlocked.Increment(ref weakVoskCustom);
                    Console.WriteLine(
                        $"  WAKE source={fusion.Source} token={fusion.Text} " +
                        $"score={fusion.Score:F3}");
                }
            };

            runtime.Start();
            mic.Start();

            var process = Process.GetCurrentProcess();

            TimeSpan cpuStart = process.TotalProcessorTime;
            var wall = Stopwatch.StartNew();

            Thread.Sleep(TimeSpan.FromSeconds(seconds));

            process.Refresh();

            double cpu =
                (process.TotalProcessorTime - cpuStart)
                .TotalSeconds /
                wall.Elapsed.TotalSeconds *
                100.0;

            mic.Stop();

            var status = runtime.DetectorStatus;
            WakeHybridStatus hybrid = runtime.HybridStatus;
            WakeV3Status? v3 = runtime.V3Status;
            int expected = options.TryGetValue("expected", out string? value)
                ? int.Parse(value, CultureInfo.InvariantCulture)
                : 0;
            int misses = expected == 0 ? 0 : Math.Max(0, expected - wakes);

            Console.WriteLine(
                $"seconds={seconds} pump-callbacks={chunks} wakes={wakes} " +
                $"strong-vosk={voskHits} weak-vosk+custom={weakVoskCustom} " +
                $"both={hybrid.BothObserved} misses={misses} " +
                $"stage1-candidates={v3?.Stage1Candidates ?? 0} " +
                $"verifier-accepted={v3?.VerifierAccepted ?? 0} " +
                $"verifier-rejected={v3?.VerifierRejected ?? 0} " +
                $"verifier-latency-ms={v3?.LastVerifierMilliseconds ?? 0:F3} " +
                $"false-wakes=manual-review-required custom-only-rejected={hybrid.CustomOnlyRejected} " +
                $"vad-rejected={runtime.RejectedDetections} " +
                $"cpu-of-one-core={cpu:F1}% " +
                $"frames={status?.FramesProcessed} " +
                $"state={status?.State} last-score={status?.LastScore:F4}");

            return 0;
        }
    }


    private static float[] LoadWithPadding(
        string wav,
        double leadSeconds,
        double tailSeconds)
    {
        using var reader = new AudioFileReader(wav);

        ISampleProvider source = reader;

        if (source.WaveFormat.Channels == 2)
            source = new StereoToMonoSampleProvider(source);

        if (source.WaveFormat.SampleRate != 16000)
            source = new WdlResamplingSampleProvider(source, 16000);

        var samples = new List<float>();

        var random = new Random(3);

        void Silence(double seconds)
        {
            for (int i = 0; i < (int)(seconds * 16000); i++)
            {
                samples.Add(
                    (float)(random.NextDouble() - 0.5) * 2e-3f);
            }
        }

        Silence(leadSeconds);

        var buffer = new float[4096];
        int read;

        while ((read = source.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        Silence(tailSeconds);

        return samples.ToArray();
    }


    private static double Rms(float[] chunk)
    {
        double sum = 0;

        foreach (float v in chunk)
            sum += v * v;

        return Math.Sqrt(sum / chunk.Length);
    }
}
