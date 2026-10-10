using System.Text;

namespace Edith.Audio.Wake;

public enum WakeEngineChoice
{
    Custom,
    CustomV3,
    Hybrid,
    HybridVoskFallback,
    VoskFallback,
    ForcedVosk,

    // Custom не поднялся, а fallbackToVosk = false.
    None
}


public sealed record WakeEngineResolution(
    WakeEngineChoice Choice,
    WakeEngineMode Requested,
    string Reason,
    WakeModelSetReport? Report,
    OnnxWakeDetector? Detector,
    string? Error)
{
    // Значение для wake-engine.txt.
    public string Label =>
        Choice switch
        {
            WakeEngineChoice.Custom =>
                "custom",

            WakeEngineChoice.CustomV3 =>
                "custom-v3",

            WakeEngineChoice.Hybrid =>
                "hybrid",

            WakeEngineChoice.HybridVoskFallback =>
                "hybrid-vosk-fallback",

            WakeEngineChoice.VoskFallback =>
                "vosk-fallback",

            WakeEngineChoice.ForcedVosk =>
                "forced-vosk",

            _ =>
                "none"
        };

    public bool UsesVosk =>
        Choice is
            WakeEngineChoice.Hybrid or
            WakeEngineChoice.HybridVoskFallback or
            WakeEngineChoice.VoskFallback or
            WakeEngineChoice.ForcedVosk;
}


public static class WakeEngineSelector
{
    // Никогда не бросает.
    //
    //   vosk   -> forced-vosk (ONNX вообще не трогаем)
    //   auto   -> custom, если все три модели валидны и
    //             pipeline прошёл проверку на реальном инференсе;
    //             иначе vosk-fallback
    //   custom -> custom; при ошибке vosk-fallback (если
    //             fallbackToVosk), иначе none
    public static WakeEngineResolution Resolve(
        WakeConfig config,
        IWakeDiagnostics? diagnostics = null,
        Func<long>? clockMs = null)
    {
        if (config.Engine ==
            WakeEngineMode.Vosk)
        {
            return new WakeEngineResolution(
                WakeEngineChoice.ForcedVosk,
                config.Engine,
                "engine=vosk in wake.json",
                null,
                null,
                null);
        }

        WakeModelSetReport report =
            config.Engine == WakeEngineMode.CustomV3
                ? TryCreateCustomV3(config, diagnostics, clockMs, out OnnxWakeDetector? detector, out string? error)
                : TryCreateCustom(config, diagnostics, clockMs, out detector, out error);

        if (detector is not null)
        {
            return new WakeEngineResolution(
                config.Engine == WakeEngineMode.Hybrid
                    ? WakeEngineChoice.Hybrid
                    : config.Engine == WakeEngineMode.CustomV3
                        ? WakeEngineChoice.CustomV3
                    : WakeEngineChoice.Custom,
                config.Engine,
                "custom models valid and pipeline " +
                "verified (baseline score " +
                detector.BaselineScore.ToString("F4", System.Globalization.CultureInfo.InvariantCulture) +
                ")",
                report,
                detector,
                null);
        }

        // Для auto "моделей ещё нет" — штатное состояние,
        // а не ошибка. Ошибка — когда файлы есть, но не годятся,
        // или пользователь явно потребовал custom.
        bool nothingInstalled =
            !report.Classifier.Exists &&
            (config.Engine == WakeEngineMode.Auto ||
             config.Engine == WakeEngineMode.Hybrid);

        string reason =
            nothingInstalled
                ? "custom wake model not installed: " +
                  config.CustomModel
                : error ??
                  "custom wake models unavailable";

        string? reportedError =
            nothingInstalled
                ? null
                : reason;

        bool fallback =
            config.Engine ==
            WakeEngineMode.Auto ||
            config.Engine == WakeEngineMode.Hybrid ||
            config.FallbackToVosk;

        return new WakeEngineResolution(
            fallback
                ? config.Engine == WakeEngineMode.Hybrid
                    ? WakeEngineChoice.HybridVoskFallback
                    : WakeEngineChoice.VoskFallback
                : WakeEngineChoice.None,
            config.Engine,
            reason,
            report,
            null,
            reportedError);
    }


    // Загружает и проверяет custom pipeline.
    // detector != null только при полном успехе.
    public static WakeModelSetReport TryCreateCustom(
        WakeConfig config,
        IWakeDiagnostics? diagnostics,
        Func<long>? clockMs,
        out OnnxWakeDetector? detector,
        out string? error)
    {
        detector = null;
        error = null;

        WakeModelSetReport report;

        LoadedWakeModels? models;

        try
        {
            report =
                WakeModelLoader.TryLoad(
                    WakeModelPaths.From(config),
                    out models);
        }
        catch (Exception ex)
        {
            error =
                "model loader failed: " +
                ex.GetType().Name +
                ": " +
                ex.Message;

            var failed =
                new WakeModelFileReport(
                    "loader",
                    "",
                    false,
                    0,
                    false,
                    [],
                    [],
                    error,
                    0);

            return new WakeModelSetReport(
                failed,
                failed,
                failed,
                "unknown");
        }

        if (models is null)
        {
            error =
                report.FirstError;

            return report;
        }

        try
        {
            detector =
                new OnnxWakeDetector(
                    models,
                    new WakeDetectionPolicy(
                        config.Threshold,
                        config.MinConsecutiveHits,
                        config.CooldownMs),
                    diagnostics,
                    clockMs);

            return report;
        }
        catch (Exception ex)
        {
            // Сессии принадлежат нам, пока конструктор
            // детектора не завершился успешно.
            models.Dispose();

            detector = null;

            error =
                "wake pipeline rejected: " +
                ex.Message;

            return report;
        }
    }


    public static WakeModelSetReport TryCreateCustomV3(
        WakeConfig config,
        IWakeDiagnostics? diagnostics,
        Func<long>? clockMs,
        out OnnxWakeDetector? detector,
        out string? error)
    {
        detector = null;
        error = null;
        WakeConfig stage1 = config with { CustomModel = config.CustomV3Model };
        WakeModelSetReport report = WakeModelLoader.TryLoad(WakeModelPaths.From(stage1), out LoadedWakeModels? models);
        if (models is null)
        {
            error = report.FirstError;
            return report;
        }

        OnnxWakeVerifier? verifier = null;
        try
        {
            verifier = new OnnxWakeVerifier(config.VerifierModel, models.ClassifierFrames, config.VerifierThreshold);
            detector = new OnnxWakeDetector(
                models,
                new WakeDetectionPolicy(config.Threshold, config.MinConsecutiveHits, config.CooldownMs),
                diagnostics,
                clockMs,
                verifier);
            return report;
        }
        catch (Exception ex)
        {
            verifier?.Dispose();
            models.Dispose();
            error = "wake-v3 pipeline rejected: " + ex.Message;
            return report;
        }
    }


    public static string DescribeModelStatus(
        WakeEngineResolution resolution)
    {
        var text =
            new StringBuilder();

        text.AppendLine(
            "engine=" +
            resolution.Label);

        if (resolution.Report is null)
        {
            text.AppendLine(
                "models=not evaluated " +
                "(custom wake disabled by config)");

            return text.ToString();
        }

        if (resolution.Detector is not null)
        {
            text.AppendLine(
                "baseline-score=" +
                resolution.Detector.BaselineScore
                    .ToString("F4", System.Globalization.CultureInfo.InvariantCulture));
        }

        text.Append(
            resolution.Report.Describe());

        return text.ToString();
    }
}
