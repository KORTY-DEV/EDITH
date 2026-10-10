using System.Text.Json;

namespace Edith.Audio.Wake;

public enum WakeEngineMode
{
    Auto,
    Custom,
    CustomV3,
    Vosk,
    Hybrid
}


public static class WakePaths
{
    public const string Root =
        @"C:\EDITH";

    public const string ConfigFile =
        @"C:\EDITH\config\wake.json";

    public const string DiagnosticsDirectory =
        @"C:\EDITH\data\diagnostics";

    public const string CustomModelDirectory =
        @"C:\EDITH\models\wake\custom";

    public const string CustomV3ModelDirectory =
        @"C:\EDITH\models\wake\custom-v3";

    // Переопределение только для тестов / smoke-проверок.
    public const string ConfigOverrideVariable =
        "EDITH_WAKE_CONFIG";


    public static string ResolveConfigFile()
    {
        string? overridden =
            Environment.GetEnvironmentVariable(
                ConfigOverrideVariable);

        return string.IsNullOrWhiteSpace(overridden)
            ? ConfigFile
            : overridden;
    }
}


public sealed record WakeConfig
{
    public const float DefaultThreshold = 0.7f;
    public const int DefaultMinConsecutiveHits = 2;
    public const int DefaultCooldownMs = 1500;

    public WakeEngineMode Engine { get; init; } =
        WakeEngineMode.Auto;

    public string CustomModel { get; init; } =
        Path.Combine(
            WakePaths.CustomModelDirectory,
            "edith.onnx");

    public string MelModel { get; init; } =
        Path.Combine(
            WakePaths.CustomModelDirectory,
            "melspectrogram.onnx");

    public string EmbeddingModel { get; init; } =
        Path.Combine(
            WakePaths.CustomModelDirectory,
            "embedding_model.onnx");

    // V3 deliberately has separate files so installing training output cannot
    // alter the proven V1/V2 custom fallback.
    public string CustomV3Model { get; init; } =
        Path.Combine(WakePaths.CustomV3ModelDirectory, "edith-stage1.onnx");

    public string VerifierModel { get; init; } =
        Path.Combine(WakePaths.CustomV3ModelDirectory, "edith-verifier.onnx");

    public float VerifierThreshold { get; init; } = 0.75f;

    public float Threshold { get; init; } =
        DefaultThreshold;

    public int MinConsecutiveHits { get; init; } =
        DefaultMinConsecutiveHits;

    public int CooldownMs { get; init; } =
        DefaultCooldownMs;

    public bool FallbackToVosk { get; init; } =
        true;
}


public sealed record WakeConfigLoadResult(
    WakeConfig Config,
    IReadOnlyList<string> Warnings,
    bool FileFound);


public static class WakeConfigLoader
{
    private static readonly JsonDocumentOptions ParseOptions =
        new()
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };


    // Никогда не бросает: испорченный конфиг = дефолты + warning.
    // Wake-runtime не имеет права уронить запуск EDITH.
    //
    // defaults: что использовать при отсутствующем/сломанном файле и для
    // пропущенных ключей (null = боевые пути C:\EDITH; нужен тестам,
    // чтобы не зависеть от установленных на машине моделей).
    public static WakeConfigLoadResult Load(
        string path,
        string? baseDirectory = null,
        WakeConfig? defaults = null)
    {
        defaults ??= new WakeConfig();

        var warnings =
            new List<string>();

        string text;

        try
        {
            if (!File.Exists(path))
            {
                return new WakeConfigLoadResult(
                    defaults,
                    warnings,
                    FileFound: false);
            }

            text =
                File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            warnings.Add(
                $"config unreadable: {ex.Message}");

            return new WakeConfigLoadResult(
                defaults,
                warnings,
                FileFound: false);
        }

        WakeConfig config =
            Parse(
                text,
                baseDirectory ??
                Path.GetDirectoryName(
                    Path.GetDirectoryName(
                        Path.GetFullPath(path))) ??
                WakePaths.Root,
                warnings,
                defaults);

        return new WakeConfigLoadResult(
            config,
            warnings,
            FileFound: true);
    }


    public static WakeConfig Parse(
        string json,
        string baseDirectory,
        List<string> warnings,
        WakeConfig? fallback = null)
    {
        WakeConfig defaults =
            fallback ?? new WakeConfig();

        JsonDocument document;

        try
        {
            document =
                JsonDocument.Parse(
                    json,
                    ParseOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add(
                $"config is not valid JSON: {ex.Message}");

            return defaults;
        }

        using (document)
        {
            JsonElement root =
                document.RootElement;

            if (root.ValueKind !=
                JsonValueKind.Object)
            {
                warnings.Add(
                    "config root is not a JSON object");

                return defaults;
            }

            return new WakeConfig
            {
                Engine =
                    ReadEngine(
                        root,
                        warnings),

                CustomModel =
                    ReadPath(
                        root,
                        "customModel",
                        defaults.CustomModel,
                        baseDirectory,
                        warnings),

                MelModel =
                    ReadPath(
                        root,
                        "melModel",
                        defaults.MelModel,
                        baseDirectory,
                        warnings),

                EmbeddingModel =
                    ReadPath(
                        root,
                        "embeddingModel",
                        defaults.EmbeddingModel,
                        baseDirectory,
                        warnings),

                CustomV3Model =
                    ReadPath(root, "customV3Model", defaults.CustomV3Model, baseDirectory, warnings),

                VerifierModel =
                    ReadPath(root, "verifierModel", defaults.VerifierModel, baseDirectory, warnings),

                VerifierThreshold =
                    ReadFloat(root, "verifierThreshold", defaults.VerifierThreshold, warnings),

                Threshold =
                    ReadThreshold(
                        root,
                        warnings),

                MinConsecutiveHits =
                    ReadInt(
                        root,
                        "minConsecutiveHits",
                        WakeConfig.DefaultMinConsecutiveHits,
                        min: 1,
                        max: 32,
                        warnings),

                CooldownMs =
                    ReadInt(
                        root,
                        "cooldownMs",
                        WakeConfig.DefaultCooldownMs,
                        min: 0,
                        max: 60_000,
                        warnings),

                FallbackToVosk =
                    ReadBool(
                        root,
                        "fallbackToVosk",
                        defaults.FallbackToVosk,
                        warnings)
            };
        }
    }


    private static WakeEngineMode ReadEngine(
        JsonElement root,
        List<string> warnings)
    {
        if (!TryGet(
                root,
                "engine",
                out JsonElement element))
        {
            return WakeEngineMode.Auto;
        }

        if (element.ValueKind ==
            JsonValueKind.String)
        {
            string? value =
                element
                    .GetString()?
                    .Trim();

            if (string.Equals(
                    value,
                    "auto",
                    StringComparison.OrdinalIgnoreCase))
            {
                return WakeEngineMode.Auto;
            }

            if (string.Equals(
                    value,
                    "custom",
                    StringComparison.OrdinalIgnoreCase))
            {
                return WakeEngineMode.Custom;
            }

            if (string.Equals(value, "custom-v3", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(value, "wake-v3", StringComparison.OrdinalIgnoreCase))
            {
                return WakeEngineMode.CustomV3;
            }

            if (string.Equals(
                    value,
                    "vosk",
                    StringComparison.OrdinalIgnoreCase))
            {
                return WakeEngineMode.Vosk;
            }

            if (string.Equals(
                    value,
                    "hybrid",
                    StringComparison.OrdinalIgnoreCase))
            {
                return WakeEngineMode.Hybrid;
            }
        }

        warnings.Add(
            $"engine '{element}' is not one of " +
            "auto|custom|vosk|hybrid; using auto");

        return WakeEngineMode.Auto;
    }


    private static string ReadPath(
        JsonElement root,
        string name,
        string fallback,
        string baseDirectory,
        List<string> warnings)
    {
        if (!TryGet(
                root,
                name,
                out JsonElement element))
        {
            return fallback;
        }

        string? value =
            element.ValueKind ==
            JsonValueKind.String
                ? element.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(value))
        {
            warnings.Add(
                $"{name} must be a non-empty string; " +
                "using default");

            return fallback;
        }

        return ResolvePath(
            value,
            baseDirectory);
    }


    public static string ResolvePath(
        string value,
        string baseDirectory)
    {
        string expanded =
            Environment.ExpandEnvironmentVariables(
                value.Trim());

        if (!Path.IsPathRooted(expanded))
        {
            expanded =
                Path.Combine(
                    baseDirectory,
                    expanded);
        }

        return Path.GetFullPath(expanded);
    }


    private static float ReadThreshold(
        JsonElement root,
        List<string> warnings)
    {
        if (!TryGet(
                root,
                "threshold",
                out JsonElement element))
        {
            return WakeConfig.DefaultThreshold;
        }

        if (element.ValueKind ==
                JsonValueKind.Number &&
            element.TryGetDouble(
                out double value) &&
            double.IsFinite(value) &&
            value > 0.0 &&
            value <= 1.0)
        {
            return (float)value;
        }

        warnings.Add(
            $"threshold '{element}' must be a number in " +
            "(0, 1]; using " +
            WakeConfig.DefaultThreshold.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

        return WakeConfig.DefaultThreshold;
    }


    private static float ReadFloat(
        JsonElement root,
        string name,
        float fallback,
        List<string> warnings)
    {
        if (!TryGet(root, name, out JsonElement element))
            return fallback;

        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out double value) &&
            double.IsFinite(value) && value > 0.0 && value <= 1.0)
        {
            return (float)value;
        }

        warnings.Add($"{name} '{element}' must be a number in (0, 1]; using {fallback}");
        return fallback;
    }


    private static int ReadInt(
        JsonElement root,
        string name,
        int fallback,
        int min,
        int max,
        List<string> warnings)
    {
        if (!TryGet(
                root,
                name,
                out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind ==
                JsonValueKind.Number &&
            element.TryGetInt32(
                out int value) &&
            value >= min &&
            value <= max)
        {
            return value;
        }

        warnings.Add(
            $"{name} '{element}' must be an integer in " +
            $"[{min}, {max}]; using {fallback}");

        return fallback;
    }


    private static bool ReadBool(
        JsonElement root,
        string name,
        bool fallback,
        List<string> warnings)
    {
        if (!TryGet(
                root,
                name,
                out JsonElement element))
        {
            return fallback;
        }

        if (element.ValueKind ==
            JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind ==
            JsonValueKind.False)
        {
            return false;
        }

        warnings.Add(
            $"{name} '{element}' must be true or false; " +
            $"using {fallback}");

        return fallback;
    }


    // Имена свойств без учёта регистра.
    private static bool TryGet(
        JsonElement root,
        string name,
        out JsonElement value)
    {
        foreach (JsonProperty property in
                 root.EnumerateObject())
        {
            if (string.Equals(
                    property.Name,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                value =
                    property.Value;

                return true;
            }
        }

        value = default;

        return false;
    }
}
