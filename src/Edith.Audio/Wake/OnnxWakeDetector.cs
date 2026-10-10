using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace Edith.Audio.Wake;

// Локальный openWakeWord-style детектор на Microsoft.ML.OnnxRuntime:
//
//   16 kHz mono
//     -> melspectrogram.onnx   (окно 1280 + 480 samples -> 8 mel-кадров)
//     -> mel = x / 10 + 2
//     -> embedding_model.onnx  (последние 76 mel-кадров -> 96-мерный вектор)
//     -> rolling окно из 16 embedding-кадров (oldest -> newest)
//     -> wake model            (-> probability 0..1, sigmoid внутри модели)
//     -> WakeDetectionPolicy   (threshold / hits / cooldown)
//
// Порядок и константы повторяют openwakeword.utils.AudioFeatures
// (_streaming_features) и train.py (to_feat).
//
// Аллокации: все буферы и OrtValue создаются один раз.
// ORT-вызовы идут в предвыделенные output-тензоры.
public sealed class OnnxWakeDetector : IWakeWordDetector
{
    public const string EngineName =
        "custom";

    private const int MelWindow =
        WakeContract.ChunkSamples +
        WakeContract.MelContextSamples;

    private const int MelFrameFloats =
        WakeContract.MelBins;

    private const int MelBufferFloats =
        WakeContract.EmbeddingWindowMelFrames *
        WakeContract.MelBins;

    private const int MelChunkFloats =
        WakeContract.MelFramesPerChunk *
        WakeContract.MelBins;

    // Сколько чанков нужно, чтобы mel-буфер (76 кадров) и
    // окно признаков (16 кадров) целиком состояли из "тишины":
    // 10 чанков на mel + 16 на embeddings, с запасом.
    private const int BaselineChunks = 32;

    private readonly LoadedWakeModels _models;
    private readonly WakeDetectionPolicy _policy;
    private readonly IWakeDiagnostics? _diagnostics;
    private readonly OnnxWakeVerifier? _verifier;
    private readonly Func<long> _clockMs;

    private readonly object _sync =
        new();

    private readonly int _frames;
    private readonly int _featureFloats;

    // Рабочее состояние.
    private readonly float[] _rawWindow =
        new float[MelWindow];

    private readonly float[] _melBuffer =
        new float[MelBufferFloats];

    private readonly float[] _features;

    private readonly float[] _melOut =
        new float[MelChunkFloats];

    private readonly float[] _embeddingOut =
        new float[WakeContract.EmbeddingDim];

    private readonly float[] _scoreOut =
        new float[1];

    // Снимок "комнатной тишины". Reset = копирование снимка.
    private readonly float[] _baselineRaw =
        new float[MelWindow];

    private readonly float[] _baselineMel =
        new float[MelBufferFloats];

    private readonly float[] _baselineFeatures;

    private readonly OrtValue _melInput;
    private readonly OrtValue _melOutput;
    private readonly OrtValue _embeddingInput;
    private readonly OrtValue _embeddingOutput;
    private readonly OrtValue _classifierInput;
    private readonly OrtValue _classifierOutput;

    private readonly RunOptions _runOptions =
        new();

    private readonly string[] _melInputNames;
    private readonly string[] _melOutputNames;
    private readonly string[] _embeddingInputNames;
    private readonly string[] _embeddingOutputNames;
    private readonly string[] _classifierInputNames;
    private readonly string[] _classifierOutputNames;

    private readonly OrtValue[] _melInputValues;
    private readonly OrtValue[] _melOutputValues;
    private readonly OrtValue[] _embeddingInputValues;
    private readonly OrtValue[] _embeddingOutputValues;
    private readonly OrtValue[] _classifierInputValues;
    private readonly OrtValue[] _classifierOutputValues;

    private int _pending;

    private WakeDetectorState _state =
        WakeDetectorState.Stopped;

    private string? _lastError;
    private long _framesProcessed;
    private float _lastScore;
    private DateTimeOffset? _lastDetectionAt;
    private double _averageChunkMs;
    private long _stage1Candidates;
    private long _verifierAccepted;
    private bool _runVerified;
    private long _verifierRejected;
    private float _lastVerifierScore;
    private bool _lastVerifierDecision;
    private bool _lastVerifierCandidate;
    private string _lastVerifierReason = "not-configured";
    private double _lastVerifierMilliseconds;
    private bool _disposed;

    public event Action<WakeDetection>? WakeDetected;

    public int ConsecutiveHits =>
        _policy.ConsecutiveHits;
    public event Action<Exception>? Faulted;

    public string Engine =>
        EngineName;

    public float BaselineScore { get; }

    public WakeModelSetReport ModelReport =>
        _models.Report;

    // Explicitly diagnostic: callers opt in to a full copy of the rolling
    // state so offline parity tools can compare Python and C# tensors.
    public WakeParitySnapshot CaptureParitySnapshot()
    {
        lock (_sync)
        {
            return new WakeParitySnapshot(
                _framesProcessed,
                _pending,
                (float[])_rawWindow.Clone(),
                (float[])_melBuffer.Clone(),
                (float[])_features.Clone(),
                (float[])_melOut.Clone(),
                (float[])_embeddingOut.Clone(),
                _lastScore,
                _lastVerifierScore,
                _lastVerifierCandidate);
        }
    }


    // Бросает InvalidOperationException, если pipeline не
    // проходит проверку на реальном инференсе
    // (неверные shape'ы, NaN, модель "стреляет" на тишине).
    // Вызывающий обязан превратить это в fallback.
    public OnnxWakeDetector(
        LoadedWakeModels models,
        WakeDetectionPolicy policy,
        IWakeDiagnostics? diagnostics = null,
        Func<long>? clockMs = null,
        OnnxWakeVerifier? verifier = null)
    {
        _models =
            models ??
            throw new ArgumentNullException(
                nameof(models));

        _policy =
            policy ??
            throw new ArgumentNullException(
                nameof(policy));

        _diagnostics =
            diagnostics;

        _verifier = verifier;

        _clockMs =
            clockMs ??
            DefaultClockMs;

        _frames =
            models.ClassifierFrames;

        _featureFloats =
            _frames *
            WakeContract.EmbeddingDim;

        _features =
            new float[_featureFloats];

        _baselineFeatures =
            new float[_featureFloats];

        _melInputNames = [models.MelInput];
        _melOutputNames = [models.MelOutput];
        _embeddingInputNames = [models.EmbeddingInput];
        _embeddingOutputNames = [models.EmbeddingOutput];
        _classifierInputNames = [models.ClassifierInput];
        _classifierOutputNames = [models.ClassifierOutput];

        try
        {
            VerifyOutputShapes();

            _melInput =
                Tensor(
                    _rawWindow,
                    1,
                    MelWindow);

            _melOutput =
                Tensor(
                    _melOut,
                    1,
                    1,
                    WakeContract.MelFramesPerChunk,
                    WakeContract.MelBins);

            _embeddingInput =
                Tensor(
                    _melBuffer,
                    1,
                    WakeContract.EmbeddingWindowMelFrames,
                    WakeContract.MelBins,
                    1);

            _embeddingOutput =
                Tensor(
                    _embeddingOut,
                    1,
                    1,
                    1,
                    WakeContract.EmbeddingDim);

            _classifierInput =
                Tensor(
                    _features,
                    1,
                    _frames,
                    WakeContract.EmbeddingDim);

            _classifierOutput =
                Tensor(
                    _scoreOut,
                    1,
                    1);

            _melInputValues = [_melInput];
            _melOutputValues = [_melOutput];
            _embeddingInputValues = [_embeddingInput];
            _embeddingOutputValues = [_embeddingOutput];
            _classifierInputValues = [_classifierInput];
            _classifierOutputValues = [_classifierOutput];

            BaselineScore =
                BuildBaseline();
        }
        catch
        {
            DisposeTensors();

            _runOptions.Dispose();

            throw;
        }

        if (BaselineScore >= policy.Threshold)
        {
            DisposeTensors();

            _runOptions.Dispose();

            throw new InvalidOperationException(
                "wake model fires on room tone " +
                "(score " +
                BaselineScore.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) +
                " >= threshold " +
                policy.Threshold.ToString("F2", System.Globalization.CultureInfo.InvariantCulture) +
                "); refusing to use it");
        }

        RestoreBaseline();
    }


    public WakeDetectorStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new WakeDetectorStatus(
                    EngineName,
                    _state,
                    _lastError,
                    _framesProcessed,
                    _lastScore,
                    _lastDetectionAt);
            }
        }
    }

    public double AverageChunkMilliseconds
    {
        get
        {
            lock (_sync)
            {
                return _averageChunkMs;
            }
        }
    }


    public WakeV3Status? V3Status =>
        _verifier is null ? null : new WakeV3Status(
            Interlocked.Read(ref _stage1Candidates),
            Interlocked.Read(ref _verifierAccepted),
            Interlocked.Read(ref _verifierRejected),
            _lastScore,
            _lastVerifierScore,
            _lastVerifierDecision,
            _lastVerifierReason,
            _lastVerifierMilliseconds);


    public void Start()
    {
        lock (_sync)
        {
            if (_disposed ||
                _state ==
                WakeDetectorState.Faulted)
            {
                return;
            }

            if (_state ==
                WakeDetectorState.Running)
            {
                return;
            }

            // Аудио между Stop и Start не было — окно устарело.
            RestoreBaseline();

            _policy.Reset();

            _state =
                WakeDetectorState.Running;
        }
    }


    public void Stop()
    {
        lock (_sync)
        {
            if (_state ==
                WakeDetectorState.Running)
            {
                _state =
                    WakeDetectorState.Stopped;
            }
        }
    }


    public void Reset()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            RestoreBaseline();

            _policy.Reset();

            _lastScore = 0;
            _runVerified = false;
        }
    }


    public void AcceptAudio(
        ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return;

        WakeDetection? detection =
            null;

        Exception? fault =
            null;

        lock (_sync)
        {
            if (_disposed ||
                _state !=
                WakeDetectorState.Running)
            {
                return;
            }

            try
            {
                int offset = 0;

                while (offset < samples.Length)
                {
                    int take =
                        Math.Min(
                            WakeContract.ChunkSamples -
                            _pending,
                            samples.Length -
                            offset);

                    int target =
                        WakeContract.MelContextSamples +
                        _pending;

                    for (int i = 0; i < take; i++)
                    {
                        _rawWindow[target + i] =
                            ToInt16Scale(
                                samples[offset + i]);
                    }

                    _pending += take;
                    offset += take;

                    if (_pending <
                        WakeContract.ChunkSamples)
                    {
                        break;
                    }

                    long started =
                        Stopwatch.GetTimestamp();

                    float score =
                        ProcessChunk();

                    _pending = 0;

                    UpdateTiming(started);

                    _framesProcessed++;
                    _lastScore = score;

                    _diagnostics?.ReportScore(
                        score,
                        _policy.Threshold);

                    float policyScore = score;
                    _lastVerifierCandidate = false;

                    // A stage-1 run ends when the score drops: the next run is verified afresh.
                    // Stage-1 frames count as consecutive hits on their own; one verified frame
                    // anywhere in the run lets it fire. Gating every frame on the verifier
                    // (as before) dropped two of three live wakes.
                    if (score < _policy.Threshold)
                        _runVerified = false;

                    if (_verifier is not null &&
                        score >= _policy.Threshold &&
                        _runVerified)
                    {
                        policyScore = score;
                    }
                    else if (_verifier is not null &&
                        score >= _policy.Threshold)
                    {
                        _lastVerifierCandidate = true;
                        Interlocked.Increment(ref _stage1Candidates);

                        WakeVerifierResult verdict =
                            _verifier.Verify(_features);

                        _lastVerifierScore = verdict.Score;
                        _lastVerifierDecision = verdict.Accepted;
                        _lastVerifierMilliseconds = verdict.Milliseconds;
                        _lastVerifierReason = verdict.Accepted
                            ? "accepted"
                            : "below-verifier-threshold";

                        if (verdict.Accepted)
                            Interlocked.Increment(ref _verifierAccepted);
                        else
                            Interlocked.Increment(ref _verifierRejected);

                        _diagnostics?.ReportWakeV3(V3Status!);
                        _runVerified = verdict.Accepted;
                    }
                    else if (_verifier is not null)
                    {
                        _lastVerifierScore = 0;
                        _lastVerifierDecision = false;
                    }

                    if (_policy.Evaluate(
                            policyScore,
                            _clockMs(),
                            mayFire: _verifier is null || _runVerified) &&
                        detection is null)
                    {
                        DateTimeOffset now =
                            DateTimeOffset.Now;

                        _lastDetectionAt =
                            now;

                        detection =
                            new WakeDetection(
                                EngineName,
                                score,
                                "edith",
                                now);
                    }
                }
            }
            catch (Exception ex)
            {
                _state =
                    WakeDetectorState.Faulted;

                _lastError =
                    ex.GetType().Name +
                    ": " +
                    ex.Message;

                fault = ex;
                detection = null;
            }
        }

        if (fault is not null)
        {
            Faulted?.Invoke(fault);

            return;
        }

        if (detection is not null)
        {
            WakeDetected?.Invoke(
                detection.Value);
        }
    }


    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;

            if (_state !=
                WakeDetectorState.Faulted)
            {
                _state =
                    WakeDetectorState.Stopped;
            }

            DisposeTensors();

            _runOptions.Dispose();

            _models.Dispose();
            _verifier?.Dispose();
        }
    }


    // ---------------------------------------------------------
    // Pipeline
    // ---------------------------------------------------------

    // Одна порция 80 ms: _rawWindow уже содержит
    // [480 хвост предыдущего | 1280 новых].
    private float ProcessChunk()
    {
        _models.Mel.Run(
            _runOptions,
            _melInputNames,
            _melInputValues,
            _melOutputNames,
            _melOutputValues);

        // Сдвиг mel-буфера на 8 кадров + transform x / 10 + 2.
        Array.Copy(
            _melBuffer,
            MelChunkFloats,
            _melBuffer,
            0,
            MelBufferFloats -
            MelChunkFloats);

        int melTail =
            MelBufferFloats -
            MelChunkFloats;

        for (int i = 0;
             i < MelChunkFloats;
             i++)
        {
            _melBuffer[melTail + i] =
                _melOut[i] / 10.0f +
                2.0f;
        }

        _models.Embedding.Run(
            _runOptions,
            _embeddingInputNames,
            _embeddingInputValues,
            _embeddingOutputNames,
            _embeddingOutputValues);

        // Окно признаков: oldest -> newest.
        Array.Copy(
            _features,
            WakeContract.EmbeddingDim,
            _features,
            0,
            _featureFloats -
            WakeContract.EmbeddingDim);

        Array.Copy(
            _embeddingOut,
            0,
            _features,
            _featureFloats -
            WakeContract.EmbeddingDim,
            WakeContract.EmbeddingDim);

        _models.Classifier.Run(
            _runOptions,
            _classifierInputNames,
            _classifierInputValues,
            _classifierOutputNames,
            _classifierOutputValues);

        float score =
            _scoreOut[0];

        if (!float.IsFinite(score) ||
            score < -0.001f ||
            score > 1.001f)
        {
            throw new InvalidOperationException(
                "wake model returned an invalid " +
                $"probability: {score}");
        }

        // Хвост для следующего mel-окна.
        Array.Copy(
            _rawWindow,
            WakeContract.ChunkSamples,
            _rawWindow,
            0,
            WakeContract.MelContextSamples);

        return
            Math.Clamp(
                score,
                0.0f,
                1.0f);
    }


    // Mel-модель обучалась на int16-PCM, поданном как float32.
    // NAudio даёт float = int16 / 32768, поэтому обратное
    // преобразование x * 32768 с округлением возвращает исходные
    // int16-значения ТОЧНО (для 16-bit источников), а для float-
    // источников отличается от train.py не более чем на 1 LSB.
    private static float ToInt16Scale(
        float sample)
    {
        if (float.IsNaN(sample))
            return 0.0f;

        return
            MathF.Round(
                Math.Clamp(
                    sample * 32768.0f,
                    -32768.0f,
                    32767.0f));
    }


    // ---------------------------------------------------------
    // Baseline ("комнатная тишина") и валидация на реальном
    // инференсе
    // ---------------------------------------------------------

    // Проверяет, что реальные output shape'ы совпадают с
    // предвыделенными тензорами (иначе Run упал бы позже,
    // уже в рабочем режиме).
    private void VerifyOutputShapes()
    {
        var melInput =
            new float[MelWindow];

        using (OrtValue input =
               OrtValue.CreateTensorValueFromMemory(
                   melInput,
                   [1, MelWindow]))
        using (var outputs =
               _models.Mel.Run(
                   _runOptions,
                   _melInputNames,
                   [input],
                   _melOutputNames))
        {
            long[] shape =
                outputs[0]
                    .GetTensorTypeAndShape()
                    .Shape;

            if (!ShapeEquals(
                    shape,
                    1,
                    1,
                    WakeContract.MelFramesPerChunk,
                    WakeContract.MelBins))
            {
                throw new InvalidOperationException(
                    "melspectrogram output for a " +
                    $"{MelWindow}-sample window is " +
                    $"[{string.Join(", ", shape)}], " +
                    "expected [1, 1, " +
                    $"{WakeContract.MelFramesPerChunk}, " +
                    $"{WakeContract.MelBins}]");
            }
        }

        var embeddingInput =
            new float[MelBufferFloats];

        using (OrtValue input =
               OrtValue.CreateTensorValueFromMemory(
                   embeddingInput,
                   [
                       1,
                       WakeContract.EmbeddingWindowMelFrames,
                       WakeContract.MelBins,
                       1
                   ]))
        using (var outputs =
               _models.Embedding.Run(
                   _runOptions,
                   _embeddingInputNames,
                   [input],
                   _embeddingOutputNames))
        {
            long[] shape =
                outputs[0]
                    .GetTensorTypeAndShape()
                    .Shape;

            if (!ShapeEquals(
                    shape,
                    1,
                    1,
                    1,
                    WakeContract.EmbeddingDim))
            {
                throw new InvalidOperationException(
                    "embedding_model output is " +
                    $"[{string.Join(", ", shape)}], " +
                    "expected [1, 1, 1, " +
                    $"{WakeContract.EmbeddingDim}]");
            }
        }

        var classifierInput =
            new float[_featureFloats];

        using (OrtValue input =
               OrtValue.CreateTensorValueFromMemory(
                   classifierInput,
                   [
                       1,
                       _frames,
                       WakeContract.EmbeddingDim
                   ]))
        using (var outputs =
               _models.Classifier.Run(
                   _runOptions,
                   _classifierInputNames,
                   [input],
                   _classifierOutputNames))
        {
            long[] shape =
                outputs[0]
                    .GetTensorTypeAndShape()
                    .Shape;

            if (shape.Length == 0 ||
                shape.Aggregate(
                    1L,
                    (a, b) => a * b) != 1)
            {
                throw new InvalidOperationException(
                    "wake model output is " +
                    $"[{string.Join(", ", shape)}], " +
                    "expected a single probability");
            }
        }
    }


    // Прогоняет pipeline на детерминированном "шуме комнаты"
    // (как place() в train.py: 1e-3 * N(0, 1)) и запоминает
    // состояние. Reset = быстрое копирование этого снимка,
    // без инференса.
    private float BuildBaseline()
    {
        var random =
            new Random(20260919);

        float score = 0;

        Array.Clear(_rawWindow);
        Array.Clear(_melBuffer);
        Array.Clear(_features);

        for (int chunk = 0;
             chunk < BaselineChunks;
             chunk++)
        {
            for (int i = 0;
                 i < WakeContract.ChunkSamples;
                 i++)
            {
                _rawWindow[
                    WakeContract.MelContextSamples + i] =
                    ToInt16Scale(
                        NextGaussian(random) *
                        1e-3f);
            }

            score =
                ProcessChunk();
        }

        Array.Copy(
            _rawWindow,
            _baselineRaw,
            MelWindow);

        Array.Copy(
            _melBuffer,
            _baselineMel,
            MelBufferFloats);

        Array.Copy(
            _features,
            _baselineFeatures,
            _featureFloats);

        return score;
    }


    private void RestoreBaseline()
    {
        Array.Copy(
            _baselineRaw,
            _rawWindow,
            MelWindow);

        Array.Copy(
            _baselineMel,
            _melBuffer,
            MelBufferFloats);

        Array.Copy(
            _baselineFeatures,
            _features,
            _featureFloats);

        _pending = 0;
    }


    private static float NextGaussian(
        Random random)
    {
        double u1 =
            1.0 - random.NextDouble();

        double u2 =
            random.NextDouble();

        return (float)(
            Math.Sqrt(-2.0 * Math.Log(u1)) *
            Math.Cos(2.0 * Math.PI * u2));
    }


    // ---------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------

    private static OrtValue Tensor(
        float[] data,
        params long[] shape)
    {
        return OrtValue
            .CreateTensorValueFromMemory(
                data,
                shape);
    }


    private static bool ShapeEquals(
        long[] shape,
        params long[] expected)
    {
        return shape.AsSpan()
            .SequenceEqual(expected);
    }


    private void UpdateTiming(
        long started)
    {
        double ms =
            Stopwatch.GetElapsedTime(started)
                .TotalMilliseconds;

        _averageChunkMs =
            _averageChunkMs == 0
                ? ms
                : _averageChunkMs * 0.98 +
                  ms * 0.02;
    }


    private void DisposeTensors()
    {
        _melInput?.Dispose();
        _melOutput?.Dispose();
        _embeddingInput?.Dispose();
        _embeddingOutput?.Dispose();
        _classifierInput?.Dispose();
        _classifierOutput?.Dispose();
    }


    private static long DefaultClockMs()
    {
        return
            (long)(
                Stopwatch.GetTimestamp() *
                1000.0 /
                Stopwatch.Frequency);
    }
}
