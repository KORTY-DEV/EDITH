using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Edith.Audio.Wake;

// Контракт openWakeWord-style pipeline. Значения взяты из РЕАЛЬНОГО
// trainer-а (scripts/train.py + openwakeword/utils.py) и
// сверены с metadata ONNX-моделей:
//
//   melspectrogram.onnx  input  [batch, samples] float (int16-scale)
//                        output [time, 1, frames, 32]
//   embedding_model.onnx input  [n, 76, 32, 1]   float
//                        output [n, 1, 1, 96]
//   <phrase>.onnx        input  [1, 16, 96]      float
//                        output [1, 1]           float, sigmoid внутри
public static class WakeContract
{
    public const int SampleRate = 16000;

    // 80 ms — шаг pipeline (8 mel-кадров, 1 embedding-кадр).
    public const int ChunkSamples = 1280;

    // Mel-модель на окне ChunkSamples + 480 даёт ровно 8 кадров
    // (frames = samples / 160 - 3).
    public const int MelContextSamples = 480;

    public const int MelBins = 32;
    public const int MelFramesPerChunk = 8;

    // Embedding-модель смотрит на 76 mel-кадров (0.76 s).
    public const int EmbeddingWindowMelFrames = 76;

    public const int EmbeddingDim = 96;

    // train.py: TARGET = 32000  # 2 s -> exactly 16 embedding frames
    public const int DefaultClassifierFrames = 16;
}


public sealed record WakeModelPaths(
    string Mel,
    string Embedding,
    string Classifier)
{
    public static WakeModelPaths From(
        WakeConfig config)
    {
        return new WakeModelPaths(
            config.MelModel,
            config.EmbeddingModel,
            config.CustomModel);
    }
}


public sealed record WakeTensorInfo(
    string Name,
    string ElementType,
    int[] Dims)
{
    public int Rank =>
        Dims.Length;

    public string DimsText =>
        "[" +
        string.Join(
            ", ",
            Dims.Select(
                d => d < 0
                    ? "?"
                    : d.ToString())) +
        "]";

    public override string ToString() =>
        $"{Name} {ElementType} {DimsText}";
}


public sealed record WakeModelFileReport(
    string Role,
    string Path,
    bool Exists,
    long SizeBytes,
    bool Valid,
    IReadOnlyList<WakeTensorInfo> Inputs,
    IReadOnlyList<WakeTensorInfo> Outputs,
    string? Error,
    double LoadMs);


public sealed record WakeModelSetReport(
    WakeModelFileReport Mel,
    WakeModelFileReport Embedding,
    WakeModelFileReport Classifier,
    string OnnxRuntimeVersion)
{
    public bool AllValid =>
        Mel.Valid &&
        Embedding.Valid &&
        Classifier.Valid;

    public bool AllPresent =>
        Mel.Exists &&
        Embedding.Exists &&
        Classifier.Exists;

    public double TotalLoadMs =>
        Mel.LoadMs +
        Embedding.LoadMs +
        Classifier.LoadMs;

    public string FirstError =>
        new[] { Mel, Embedding, Classifier }
            .Where(r => !r.Valid)
            .Select(r => $"{r.Role}: {r.Error ?? "invalid"}")
            .FirstOrDefault() ?? "";

    public string Describe()
    {
        var text =
            new StringBuilder();

        text.AppendLine(
            "onnxruntime=" +
            OnnxRuntimeVersion);

        text.AppendLine(
            "all-valid=" +
            AllValid);

        text.AppendLine(
            "total-load-ms=" +
            TotalLoadMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

        foreach (var file in
                 new[] { Mel, Embedding, Classifier })
        {
            text.AppendLine();

            text.AppendLine(
                $"[{file.Role}]");

            text.AppendLine(
                $"path={file.Path}");

            text.AppendLine(
                $"exists={file.Exists}");

            text.AppendLine(
                $"size={file.SizeBytes}");

            text.AppendLine(
                $"valid={file.Valid}");

            foreach (var input in file.Inputs)
            {
                text.AppendLine(
                    $"input={input}");
            }

            foreach (var output in file.Outputs)
            {
                text.AppendLine(
                    $"output={output}");
            }

            text.AppendLine(
                "load-ms=" +
                file.LoadMs.ToString("F1", System.Globalization.CultureInfo.InvariantCulture));

            if (file.Error is not null)
            {
                text.AppendLine(
                    $"error={file.Error}");
            }
        }

        return text.ToString();
    }
}


// Чистые проверки контракта по metadata.
// Не зависят от ORT — тестируются на синтетических WakeTensorInfo.
public static class WakeModelContractValidator
{
    private const string Float =
        "float32";


    public static string? ValidateMel(
        IReadOnlyList<WakeTensorInfo> inputs,
        IReadOnlyList<WakeTensorInfo> outputs)
    {
        string? single =
            RequireSingle(
                inputs,
                outputs);

        if (single is not null)
            return single;

        WakeTensorInfo input =
            inputs[0];

        WakeTensorInfo output =
            outputs[0];

        if (input.ElementType != Float ||
            input.Rank != 2)
        {
            return
                "melspectrogram input must be float32 " +
                $"[batch, samples], got {input}";
        }

        if (output.ElementType != Float ||
            output.Rank != 4 ||
            output.Dims[3] !=
            WakeContract.MelBins)
        {
            return
                "melspectrogram output must be float32 " +
                $"[time, 1, frames, {WakeContract.MelBins}], " +
                $"got {output}";
        }

        return null;
    }


    public static string? ValidateEmbedding(
        IReadOnlyList<WakeTensorInfo> inputs,
        IReadOnlyList<WakeTensorInfo> outputs)
    {
        string? single =
            RequireSingle(
                inputs,
                outputs);

        if (single is not null)
            return single;

        WakeTensorInfo input =
            inputs[0];

        WakeTensorInfo output =
            outputs[0];

        if (input.ElementType != Float ||
            input.Rank != 4 ||
            input.Dims[1] !=
            WakeContract.EmbeddingWindowMelFrames ||
            input.Dims[2] !=
            WakeContract.MelBins ||
            input.Dims[3] != 1)
        {
            return
                "embedding_model input must be float32 " +
                $"[n, {WakeContract.EmbeddingWindowMelFrames}, " +
                $"{WakeContract.MelBins}, 1], got {input}";
        }

        if (output.ElementType != Float ||
            output.Rank != 4 ||
            output.Dims[3] !=
            WakeContract.EmbeddingDim)
        {
            return
                "embedding_model output must be float32 " +
                $"[n, 1, 1, {WakeContract.EmbeddingDim}], " +
                $"got {output}";
        }

        return null;
    }


    // frames — сколько embedding-кадров ожидает классификатор
    // (по metadata; для trainer-а это 16).
    public static string? ValidateClassifier(
        IReadOnlyList<WakeTensorInfo> inputs,
        IReadOnlyList<WakeTensorInfo> outputs,
        out int frames)
    {
        frames = 0;

        string? single =
            RequireSingle(
                inputs,
                outputs);

        if (single is not null)
            return single;

        WakeTensorInfo input =
            inputs[0];

        WakeTensorInfo output =
            outputs[0];

        if (input.ElementType != Float ||
            input.Rank != 3 ||
            (input.Dims[0] != 1 &&
             input.Dims[0] != -1) ||
            input.Dims[1] <= 0 ||
            input.Dims[2] !=
            WakeContract.EmbeddingDim)
        {
            return
                "wake model input must be float32 " +
                $"[1, frames, {WakeContract.EmbeddingDim}] " +
                $"with a static frame count, got {input}";
        }

        // Ровно одна вероятность на выходе.
        bool singleValue =
            output.ElementType == Float &&
            output.Rank is >= 1 and <= 2 &&
            output.Dims.All(
                d => d == 1 || d == -1) &&
            output.Dims.Count(d => d == -1) <= 1;

        if (!singleValue)
        {
            return
                "wake model output must be one float32 " +
                $"probability ([1, 1]), got {output}";
        }

        frames =
            input.Dims[1];

        return null;
    }


    private static string? RequireSingle(
        IReadOnlyList<WakeTensorInfo> inputs,
        IReadOnlyList<WakeTensorInfo> outputs)
    {
        if (inputs.Count != 1)
        {
            return
                $"expected exactly 1 input, found {inputs.Count}";
        }

        if (outputs.Count != 1)
        {
            return
                $"expected exactly 1 output, found {outputs.Count}";
        }

        return null;
    }
}


// Три загруженных ORT-сессии + метаданные, нужные pipeline.
// Владеет сессиями; Dispose освобождает unmanaged-ресурсы.
public sealed class LoadedWakeModels : IDisposable
{
    internal LoadedWakeModels(
        InferenceSession mel,
        InferenceSession embedding,
        InferenceSession classifier,
        string melInput,
        string melOutput,
        string embeddingInput,
        string embeddingOutput,
        string classifierInput,
        string classifierOutput,
        int classifierFrames,
        WakeModelSetReport report)
    {
        Mel = mel;
        Embedding = embedding;
        Classifier = classifier;

        MelInput = melInput;
        MelOutput = melOutput;

        EmbeddingInput = embeddingInput;
        EmbeddingOutput = embeddingOutput;

        ClassifierInput = classifierInput;
        ClassifierOutput = classifierOutput;

        ClassifierFrames = classifierFrames;

        Report = report;
    }

    internal InferenceSession Mel { get; }
    internal InferenceSession Embedding { get; }
    internal InferenceSession Classifier { get; }

    internal string MelInput { get; }
    internal string MelOutput { get; }

    internal string EmbeddingInput { get; }
    internal string EmbeddingOutput { get; }

    internal string ClassifierInput { get; }
    internal string ClassifierOutput { get; }

    public int ClassifierFrames { get; }

    public WakeModelSetReport Report { get; }

    public void Dispose()
    {
        Mel.Dispose();
        Embedding.Dispose();
        Classifier.Dispose();
    }
}


public static class WakeModelLoader
{
    public static string OnnxRuntimeVersion()
    {
        try
        {
            return OrtEnv.Instance()
                .GetVersionString();
        }
        catch (Exception ex)
        {
            return
                "unavailable (" +
                ex.GetType().Name +
                ")";
        }
    }


    // Никогда не бросает наружу: любая проблема
    // (нет файла, битый ONNX, чужой контракт, нет нативной DLL)
    // превращается в report с Valid=false.
    //
    // models != null только если ВСЕ три модели валидны.
    public static WakeModelSetReport TryLoad(
        WakeModelPaths paths,
        out LoadedWakeModels? models)
    {
        models = null;

        string ortVersion =
            OnnxRuntimeVersion();

        InferenceSession? mel = null;
        InferenceSession? embedding = null;
        InferenceSession? classifier = null;

        try
        {
            var melReport =
                LoadOne(
                    "melspectrogram",
                    paths.Mel,
                    (i, o) =>
                        (WakeModelContractValidator
                            .ValidateMel(i, o),
                         0),
                    out mel,
                    out _);

            var embeddingReport =
                LoadOne(
                    "embedding_model",
                    paths.Embedding,
                    (i, o) =>
                        (WakeModelContractValidator
                            .ValidateEmbedding(i, o),
                         0),
                    out embedding,
                    out _);

            var classifierReport =
                LoadOne(
                    "wake_model",
                    paths.Classifier,
                    (i, o) =>
                    {
                        string? error =
                            WakeModelContractValidator
                                .ValidateClassifier(
                                    i,
                                    o,
                                    out int f);

                        return (error, f);
                    },
                    out classifier,
                    out int classifierFrames);

            var report =
                new WakeModelSetReport(
                    melReport,
                    embeddingReport,
                    classifierReport,
                    ortVersion);

            if (!report.AllValid ||
                mel is null ||
                embedding is null ||
                classifier is null)
            {
                mel?.Dispose();
                embedding?.Dispose();
                classifier?.Dispose();

                return report;
            }

            models =
                new LoadedWakeModels(
                    mel,
                    embedding,
                    classifier,
                    Only(mel.InputMetadata.Keys),
                    Only(mel.OutputMetadata.Keys),
                    Only(embedding.InputMetadata.Keys),
                    Only(embedding.OutputMetadata.Keys),
                    Only(classifier.InputMetadata.Keys),
                    Only(classifier.OutputMetadata.Keys),
                    classifierFrames,
                    report);

            // Владение сессиями перешло к LoadedWakeModels.
            mel = null;
            embedding = null;
            classifier = null;

            return report;
        }
        catch (Exception ex)
        {
            mel?.Dispose();
            embedding?.Dispose();
            classifier?.Dispose();

            models = null;

            var failed =
                new WakeModelFileReport(
                    "loader",
                    "",
                    false,
                    0,
                    false,
                    [],
                    [],
                    ex.GetType().Name +
                    ": " +
                    ex.Message,
                    0);

            return new WakeModelSetReport(
                failed,
                failed,
                failed,
                ortVersion);
        }
    }


    private static WakeModelFileReport LoadOne(
        string role,
        string path,
        Func<IReadOnlyList<WakeTensorInfo>,
             IReadOnlyList<WakeTensorInfo>,
             (string? Error, int Frames)> validate,
        out InferenceSession? session,
        out int frames)
    {
        session = null;
        frames = 0;

        bool exists = false;
        long size = 0;

        try
        {
            var info =
                new FileInfo(path);

            exists =
                info.Exists;

            size =
                exists
                    ? info.Length
                    : 0;
        }
        catch
        {
        }

        if (!exists)
        {
            return new WakeModelFileReport(
                role,
                path,
                false,
                0,
                false,
                [],
                [],
                "file not found",
                0);
        }

        if (size == 0)
        {
            return new WakeModelFileReport(
                role,
                path,
                true,
                0,
                false,
                [],
                [],
                "file is empty",
                0);
        }

        var timer =
            Stopwatch.StartNew();

        try
        {
            using var options =
                CreateSessionOptions();

            session =
                new InferenceSession(
                    path,
                    options);

            var inputs =
                Describe(
                    session.InputMetadata);

            var outputs =
                Describe(
                    session.OutputMetadata);

            timer.Stop();

            (string? error, int f) =
                validate(
                    inputs,
                    outputs);

            if (error is not null)
            {
                session.Dispose();
                session = null;
            }

            frames = f;

            return new WakeModelFileReport(
                role,
                path,
                true,
                size,
                error is null,
                inputs,
                outputs,
                error,
                timer.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            session?.Dispose();
            session = null;

            timer.Stop();

            return new WakeModelFileReport(
                role,
                path,
                true,
                size,
                false,
                [],
                [],
                "not a loadable ONNX model: " +
                ex.GetType().Name +
                ": " +
                FirstLine(ex.Message),
                timer.Elapsed.TotalMilliseconds);
        }
    }


    // CPU only, один поток, без spin-wait:
    // wake работает постоянно рядом с играми,
    // поэтому не должен держать ядра.
    internal static SessionOptions CreateSessionOptions()
    {
        var options =
            new SessionOptions
            {
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,

                ExecutionMode =
                    ExecutionMode.ORT_SEQUENTIAL,

                GraphOptimizationLevel =
                    GraphOptimizationLevel.ORT_ENABLE_ALL,

                LogSeverityLevel =
                    OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };

        options.AddSessionConfigEntry(
            "session.intra_op.allow_spinning",
            "0");

        options.AddSessionConfigEntry(
            "session.inter_op.allow_spinning",
            "0");

        return options;
    }


    private static IReadOnlyList<WakeTensorInfo> Describe(
        IReadOnlyDictionary<string, NodeMetadata> metadata)
    {
        var list =
            new List<WakeTensorInfo>();

        foreach (var pair in metadata)
        {
            NodeMetadata node =
                pair.Value;

            list.Add(
                new WakeTensorInfo(
                    pair.Key,
                    node.IsTensor
                        ? ElementName(
                            node.ElementDataType)
                        : "non-tensor",
                    node.IsTensor
                        ? node.Dimensions
                        : []));
        }

        return list;
    }


    private static string ElementName(
        TensorElementType type)
    {
        return type switch
        {
            TensorElementType.Float =>
                "float32",

            _ =>
                type
                    .ToString()
                    .ToLowerInvariant()
        };
    }


    private static string Only(
        IEnumerable<string> names)
    {
        return names.Single();
    }


    private static string FirstLine(
        string text)
    {
        int end =
            text.IndexOfAny(
                ['\r', '\n']);

        return end < 0
            ? text
            : text[..end];
    }
}
