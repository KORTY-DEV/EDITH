using System.Globalization;
using System.Text.Json;
using Edith.Audio.Wake;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Edith.WakeCheck;

// Офлайн-проверка custom wake без микрофона и без запуска EDITH.
//
//   Edith.WakeCheck validate --model edith.onnx [--mel m.onnx] [--embedding e.onnx]
//   Edith.WakeCheck score    --wav clip.wav [--model ...] [--threshold 0.7] [--dump]
//
// validate: тот же загрузчик и тот же контракт, что в runtime EDITH
//           (metadata + прогон реального pipeline + проверка "модель
//           не стреляет на комнатной тишине"). Exit code 0 = годится.
// score:    прогоняет WAV через ТОТ ЖЕ OnnxWakeDetector и печатает
//           score по каждым 80 ms.
internal static class Program
{
    private static int Main(
        string[] args)
    {
        Console.OutputEncoding =
            System.Text.Encoding.UTF8;

        if (args.Length == 0)
        {
            return Usage();
        }

        var options =
            ParseOptions(
                args.Skip(1).ToArray());

        WakeConfig defaults =
            new();

        var config =
            defaults with
            {
                CustomModel =
                    options.GetValueOrDefault(
                        "model",
                        defaults.CustomModel),

                MelModel =
                    options.GetValueOrDefault(
                        "mel",
                        defaults.MelModel),

                EmbeddingModel =
                    options.GetValueOrDefault(
                        "embedding",
                        defaults.EmbeddingModel),

                Threshold =
                    options.TryGetValue(
                        "threshold",
                        out string? t)
                        ? float.Parse(
                            t,
                            CultureInfo.InvariantCulture)
                        : defaults.Threshold
            };

        return args[0].ToLowerInvariant() switch
        {
            "validate" =>
                Validate(config),

            "score" =>
                Score(
                    config,
                    options),

            "policy-report" =>
                PolicyReport(config, options),

            // Стенд поверх настоящего WakeRuntime + Vosk.
            "runtime" =>
                Harness.RunWav(options),

            "live" =>
                Harness.RunLive(options),

            _ =>
                Usage()
        };
    }


    private static int Validate(
        WakeConfig config)
    {
        WakeModelSetReport report =
            WakeEngineSelector.TryCreateCustom(
                config,
                null,
                null,
                out OnnxWakeDetector? detector,
                out string? error);

        Console.WriteLine(
            report.Describe());

        if (detector is null)
        {
            Console.WriteLine(
                "RESULT: INVALID");

            Console.WriteLine(
                error ??
                report.FirstError);

            return 1;
        }

        using (detector)
        {
            Console.WriteLine(
                "baseline-score=" +
                detector.BaselineScore
                    .ToString("F4"));

            Console.WriteLine(
                "RESULT: OK");

            return 0;
        }
    }


    private static int Score(
        WakeConfig config,
        Dictionary<string, string> options)
    {
        if (!options.TryGetValue(
                "wav",
                out string? wav))
        {
            return Usage();
        }

        bool dump =
            options.ContainsKey("dump");

        bool v3 = options.TryGetValue("verifier", out string? verifierPath);
        WakeConfig effectiveConfig = v3
            ? config with
            {
                Engine = WakeEngineMode.CustomV3,
                CustomV3Model = config.CustomModel,
                VerifierModel = verifierPath!,
                VerifierThreshold = options.TryGetValue("verifier-threshold", out string? vt)
                    ? float.Parse(vt, CultureInfo.InvariantCulture)
                    : config.VerifierThreshold
            }
            : config;

        WakeModelSetReport report;
        OnnxWakeDetector? detector;
        string? error;
        if (v3)
        {
            report = WakeEngineSelector.TryCreateCustomV3(
                effectiveConfig, null, null, out detector, out error);
        }
        else
        {
            report = WakeEngineSelector.TryCreateCustom(
                effectiveConfig, null, null, out detector, out error);
        }

        if (detector is null)
        {
            Console.Error.WriteLine(
                "model rejected: " +
                (error ?? report.FirstError));

            return 1;
        }

        using (detector)
        {
            var scores =
                new List<(int Chunk, float Score)>();

            string? parityOut =
                options.GetValueOrDefault("parity-out");
            List<WakeParitySnapshot>? parity =
                parityOut is null ? null : new();

            int detections = 0;

            detector.WakeDetected +=
                _ => detections++;

            detector.Start();
            parity?.Add(detector.CaptureParitySnapshot());

            using var reader =
                new AudioFileReader(wav);

            ISampleProvider source =
                reader;

            if (source.WaveFormat.Channels == 2)
            {
                source =
                    new StereoToMonoSampleProvider(
                        source);
            }

            if (source.WaveFormat.SampleRate !=
                WakeContract.SampleRate)
            {
                source =
                    new WdlResamplingSampleProvider(
                        source,
                        WakeContract.SampleRate);
            }

            var buffer =
                new float[
                    WakeContract.ChunkSamples];

            long previousFrames = 0;
            int chunk = 0;

            while (true)
            {
                int read =
                    ReadFull(
                        source,
                        buffer);

                if (read <= 0)
                    break;

                detector.AcceptAudio(
                    buffer.AsSpan(0, read));

                var status =
                    detector.Status;

                if (status.FramesProcessed >
                    previousFrames)
                {
                    scores.Add(
                        (chunk,
                         status.LastScore));

                    previousFrames =
                        status.FramesProcessed;
                }

                if (parity is not null &&
                    detector.Status.FramesProcessed > parity.Count - 1)
                {
                    parity.Add(detector.CaptureParitySnapshot());
                }

                chunk++;
            }

            foreach (var item in scores)
            {
                if (dump ||
                    item.Score >=
                    WakeScoreThrottle
                        .MeaningfulFloor)
                {
                    Console.WriteLine(
                        $"{item.Chunk}\t" +
                        $"{item.Chunk * 0.08:F2}s\t" +
                        item.Score.ToString(
                            "F4",
                        CultureInfo.InvariantCulture));
                }
            }

            if (v3 && detector.V3Status is WakeV3Status v3Status)
            {
                Console.WriteLine(
                    $"verifier-candidates={v3Status.Stage1Candidates} " +
                    $"accepted={v3Status.VerifierAccepted} " +
                    $"rejected={v3Status.VerifierRejected} " +
                    $"last-verifier={v3Status.LastVerifierScore.ToString("F6", CultureInfo.InvariantCulture)}");
            }

            float max =
                scores.Count == 0
                    ? 0
                    : scores.Max(s => s.Score);

            Console.WriteLine(
                $"chunks={scores.Count} " +
                $"max={max:F4} " +
                $"threshold={config.Threshold:F2} " +
                $"detections={detections} " +
                $"avg-chunk-ms=" +
                detector
                    .AverageChunkMilliseconds
                    .ToString("F3"));

            if (parityOut is not null)
            {
                string fullPath = Path.GetFullPath(parityOut);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, JsonSerializer.Serialize(parity));
                Console.WriteLine($"parity-out={fullPath}");
            }

            return 0;
        }
    }


    // Replays the existing labelled WAV corpus through the production ONNX
    // pipeline. It emits aggregate evidence only: no WAV/audio is copied.
    private static int PolicyReport(
        WakeConfig config,
        Dictionary<string, string> options)
    {
        string positives = options.GetValueOrDefault(
            "positives",
            @"C:\EDITH\data\wake-training\session-pos");
        string negatives = options.GetValueOrDefault(
            "negatives",
            @"C:\EDITH\data\wake-training\session-neg");
        string output = options.GetValueOrDefault(
            "out",
            @"C:\EDITH\docs\HYBRID_WAKE_V1_OFFLINE.md");
        string manifest = options.GetValueOrDefault(
            "manifest",
            @"C:\EDITH\data\diagnostics\wake-trainer-v2-scan.csv");

        string[] positiveFiles = Directory.Exists(positives)
            ? Directory.GetFiles(positives, "*.wav", SearchOption.AllDirectories)
            : [];
        string[] negativeFiles = Directory.Exists(negatives)
            ? Directory.GetFiles(negatives, "*.wav", SearchOption.AllDirectories)
            : [];

        // The current folder can contain newer unreviewed clips. The existing
        // v2 scan is the labelled 50-positive / 450-negative comparison set.
        if (File.Exists(manifest))
        {
            var selected = File.ReadLines(manifest)
                .Skip(1)
                .Select(line => line.Split(',')[0].Trim().Trim('"'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            positiveFiles = positiveFiles
                .Where(file => selected.Contains(Path.GetFileName(file)))
                .ToArray();
            negativeFiles = negativeFiles
                .Where(file => selected.Contains(Path.GetFileName(file)))
                .ToArray();
        }

        if (positiveFiles.Length == 0 || negativeFiles.Length == 0)
        {
            Console.Error.WriteLine("labelled wake corpus is missing");
            return 1;
        }

        WakeModelSetReport report = WakeEngineSelector.TryCreateCustom(
            config, null, null, out OnnxWakeDetector? detector, out string? error);
        if (detector is null)
        {
            Console.Error.WriteLine("model rejected: " + (error ?? report.FirstError));
            return 1;
        }

        using (detector)
        {
            detector.Start();
            var clips = new List<(bool Positive, string Category, float[] Scores)>();

            foreach (string file in positiveFiles)
                clips.Add((true, "positive", ReadScores(detector, file)));

            foreach (string file in negativeFiles)
                clips.Add((false, CategoryFor(file), ReadScores(detector, file)));

            var text = new System.Text.StringBuilder();
            text.AppendLine("# HYBRID FUSION V2 — offline custom confirmation policy");
            text.AppendLine();
            text.AppendLine("Generated by `Edith.WakeCheck policy-report` against the current ONNX model and v2 labelled manifest; no raw audio is retained.");
            text.AppendLine();
            text.AppendLine($"Corpus: {positiveFiles.Length} positives, {negativeFiles.Length} negatives.");
            text.AppendLine("Custom is VAD-gated confirmation only: strong Vosk wakes immediately; weak Vosk needs custom in the same speech segment.");
            text.AppendLine();
            text.AppendLine("| threshold | hits | positives | false triggers | rescue contribution |");
            text.AppendLine("|---:|---:|---:|---:|---:|");

            (float Threshold, int Hits)[] policies =
                [(0.55f, 1), (0.55f, 2), (0.60f, 1), (0.60f, 2),
                 (0.65f, 1), (0.65f, 2), (0.70f, 1), (0.70f, 2)];

            foreach (var policy in policies)
            {
                int pos = clips.Count(c => c.Positive && Fires(c.Scores, policy.Threshold, policy.Hits));
                int fp = clips.Count(c => !c.Positive && Fires(c.Scores, policy.Threshold, policy.Hits));
                text.AppendLine($"| {policy.Threshold.ToString("F2", CultureInfo.InvariantCulture)} | {policy.Hits} | {pos}/{positiveFiles.Length} | {fp}/{negativeFiles.Length} | {pos}/{positiveFiles.Length} custom candidates before Vosk overlap |");
            }

            const float selectedThreshold = 0.70f;
            const int selectedHits = 2;
            text.AppendLine();
            text.AppendLine("## Category breakdown for selected 0.70 / 2 policy");
            text.AppendLine();
            text.AppendLine("| negative category | false triggers | clips |");
            text.AppendLine("|---|---:|---:|");
            foreach (var group in clips.Where(c => !c.Positive).GroupBy(c => c.Category).OrderBy(g => g.Key))
            {
                int fires = group.Count(c => Fires(c.Scores, selectedThreshold, selectedHits));
                text.AppendLine($"| {group.Key} | {fires} | {group.Count()} |");
            }

            text.AppendLine();
            int[] positiveRuns = clips.Where(c => c.Positive)
                .Select(c => MaxRun(c.Scores, 0.65f))
                .ToArray();
            int[] falseRuns = clips.Where(c => !c.Positive && MaxRun(c.Scores, 0.65f) >= 2)
                .Select(c => MaxRun(c.Scores, 0.65f))
                .ToArray();
            text.AppendLine("## Run-length check at 0.65");
            text.AppendLine();
            text.AppendLine($"Positive maximum consecutive runs: min {positiveRuns.Min()}, max {positiveRuns.Max()} chunks.");
            text.AppendLine($"False-trigger maximum consecutive runs: {(falseRuns.Length == 0 ? "none" : $"min {falseRuns.Min()}, max {falseRuns.Max()} chunks")}.");
            text.AppendLine("Decision: run length is not added to production policy unless it separates these sets; V2 relies on Vosk token plus same-segment custom confirmation instead.");
            text.AppendLine();
            text.AppendLine("Decision: retain 0.70 threshold and two consecutive 80 ms hits for custom confirmation. Custom never opens a session by itself, and no global fuzzy text matching is enabled.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, text.ToString());
            Console.WriteLine($"report={output}");
            return 0;
        }
    }


    private static float[] ReadScores(OnnxWakeDetector detector, string wav)
    {
        detector.Reset();
        using var reader = new AudioFileReader(wav);
        ISampleProvider source = reader;
        if (source.WaveFormat.Channels == 2)
            source = new StereoToMonoSampleProvider(source);
        if (source.WaveFormat.SampleRate != WakeContract.SampleRate)
            source = new WdlResamplingSampleProvider(source, WakeContract.SampleRate);

        var scores = new List<float>();
        var buffer = new float[WakeContract.ChunkSamples];
        long frames = detector.Status.FramesProcessed;
        while (ReadFull(source, buffer) > 0)
        {
            detector.AcceptAudio(buffer);
            WakeDetectorStatus status = detector.Status;
            if (status.FramesProcessed > frames)
            {
                scores.Add(status.LastScore);
                frames = status.FramesProcessed;
            }
        }
        return scores.ToArray();
    }


    private static bool Fires(float[] scores, float threshold, int hits)
    {
        int consecutive = 0;
        foreach (float score in scores)
        {
            consecutive = score >= threshold ? consecutive + 1 : 0;
            if (consecutive >= hits)
                return true;
        }
        return false;
    }


    private static int MaxRun(float[] scores, float threshold)
    {
        int current = 0;
        int maximum = 0;
        foreach (float score in scores)
        {
            current = score >= threshold ? current + 1 : 0;
            maximum = Math.Max(maximum, current);
        }
        return maximum;
    }


    private static string CategoryFor(string file)
    {
        string name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        if (name.StartsWith("neg_"))
            name = name[4..];
        int last = name.LastIndexOf('_');
        return last > 0 ? name[..last] : "other";
    }


    private static int ReadFull(
        ISampleProvider source,
        float[] buffer)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int read =
                source.Read(
                    buffer.AsSpan(total));

            if (read <= 0)
                break;

            total += read;
        }

        // Хвост короче 80 ms не обрабатывается
        // (как и в потоковом режиме).
        return total == buffer.Length
            ? total
            : 0;
    }


    private static Dictionary<string, string>
        ParseOptions(string[] args)
    {
        var options =
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase);

        for (int i = 0;
             i < args.Length;
             i++)
        {
            if (!args[i].StartsWith("--"))
                continue;

            string name =
                args[i][2..];

            if (i + 1 < args.Length &&
                !args[i + 1].StartsWith("--"))
            {
                options[name] =
                    args[++i];
            }
            else
            {
                options[name] =
                    "true";
            }
        }

        return options;
    }


    private static int Usage()
    {
        Console.Error.WriteLine(
            "Usage:\n" +
            "  Edith.WakeCheck validate --model <edith.onnx> " +
            "[--mel <melspectrogram.onnx>] " +
            "[--embedding <embedding_model.onnx>]\n" +
            "  Edith.WakeCheck score --wav <clip.wav> " +
            "[--model ...] [--mel ...] [--embedding ...] " +
            "[--threshold 0.7] [--dump] [--parity-out <trace.json>] " +
            "[--verifier <verifier.onnx>] [--verifier-threshold 0.75]\n" +
            "  Edith.WakeCheck policy-report [--positives <dir>] [--negatives <dir>] [--manifest <csv>] [--out <report.md>]\n" +
            "  Edith.WakeCheck runtime --wav <clip.wav> " +
            "[--config wake.json] [--diag <dir>]\n" +
            "  Edith.WakeCheck live [--seconds 15] " +
            "[--config wake-v3.json] [--expected <wake-attempts>] [--diag <dir>]\n" +
            "  Wake V3 uses engine=custom-v3 plus customV3Model, verifierModel and verifierThreshold in a separate config; it never changes config/wake.json itself.");

        return 2;
    }
}
