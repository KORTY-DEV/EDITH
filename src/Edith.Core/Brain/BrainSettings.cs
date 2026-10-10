using System.Text.Json;

namespace Edith.Core.Brain;

/// <summary>
/// Resource / latency envelope of one brain profile (normal or gaming).
/// </summary>
public sealed record BrainModeSettings
{
    /// <summary>Empty = inherit the top-level <c>model</c>.</summary>
    public string Model { get; init; } = "";

    /// <summary>How long Ollama keeps the model resident after a reply. Zero = unload right away.</summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromSeconds(90);

    public int NumCtx { get; init; } = 2048;

    /// <summary>CPU threads for generation (0 = Ollama default). Gaming caps this to leave cores to the game.</summary>
    public int NumThread { get; init; } = 0;

    /// <summary>Force CPU-only inference (num_gpu = 0) so the model never touches VRAM.</summary>
    public bool CpuOnly { get; init; } = false;

    /// <summary>
    /// Normal profile only: if Ollama put the model on the CPU (no GPU offload) switch to the
    /// gaming/light model, which is the one that stays fast on CPU.
    /// </summary>
    public bool FallbackToLightOnCpu { get; init; } = true;

    /// <summary>Generation budget for a final voice answer.</summary>
    public int AnswerNumPredict { get; init; } = 128;

    /// <summary>Generation budget for a tool decision ({"type":"tool",...}).</summary>
    public int ToolNumPredict { get; init; } = 96;

    /// <summary>Budget for one warm chat call (prompt + generation).</summary>
    public int StepTimeoutSeconds { get; init; } = 20;

    /// <summary>Extra budget added to a step when the model has to be loaded first.</summary>
    public int ColdLoadTimeoutSeconds { get; init; } = 40;

    /// <summary>Budget for the whole request including tool steps.</summary>
    public int TotalTimeoutSeconds { get; init; } = 60;

    /// <summary>Value for the Ollama <c>keep_alive</c> field: integer 0 or a duration string.</summary>
    public object KeepAliveForOllama =>
        KeepAlive <= TimeSpan.Zero
            ? 0
            : $"{(int)Math.Ceiling(KeepAlive.TotalSeconds)}s";
}

public sealed record GamingSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Process names (without .exe), case-insensitive. Names shorter than 4 chars must match exactly.</summary>
    public IReadOnlyList<string> Processes { get; init; } =
        new[] { "WorldOfTanks", "Tanki", "wot" };

    /// <summary>Also treat a foreground full-screen / presentation app as a game.</summary>
    public bool DetectFullscreen { get; init; } = true;

    public int PollSeconds { get; init; } = 5;

    /// <summary>Use <see cref="Mode"/>.Model (when set) instead of the normal model while gaming.</summary>
    public bool PreferLightModel { get; init; } = true;

    public BrainModeSettings Mode { get; init; } =
        new()
        {
            KeepAlive = TimeSpan.Zero,
            CpuOnly = true,
            StepTimeoutSeconds = 25,
            ColdLoadTimeoutSeconds = 40,
            TotalTimeoutSeconds = 75
        };
}

public sealed record BrainSettings
{
    public bool Enabled { get; init; } = true;

    public string Endpoint { get; init; } = "http://127.0.0.1:11434";

    /// <summary>Model of the normal profile (and the fallback for the gaming profile).</summary>
    public string Model { get; init; } = "";

    public long AutoModelMaxBytes { get; init; } = 4_000_000_000;

    public int MaxToolSteps { get; init; } = 3;

    public BrainModeSettings Normal { get; init; } = new();

    public GamingSettings Gaming { get; init; } = new();

    /// <summary>
    /// When set, EDITH runs her own Ollama server at <see cref="Endpoint"/> with models from this
    /// folder (a fast NVMe copy of just her models); the shared server keeps all the others.
    /// </summary>
    public string OwnServerModelsPath { get; init; } = "";
}

public static class BrainSettingsLoader
{
    private static readonly JsonDocumentOptions Options =
        new()
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        };

    public static BrainSettings Parse(string? json, ICollection<string>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new BrainSettings();
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json.TrimStart('﻿'), Options);
            JsonElement root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                warnings?.Add("brain.json root is not an object; defaults used.");
                return new BrainSettings();
            }

            var defaults = new BrainSettings();

            BrainModeSettings normal =
                ReadMode(GetObject(root, "normal"), defaults.Normal, warnings, "normal");

            JsonElement? gamingElement = GetObject(root, "gaming");
            GamingSettings gaming =
                ReadGaming(gamingElement, defaults.Gaming, warnings);

            return new BrainSettings
            {
                Enabled = GetBool(root, "enabled", defaults.Enabled),
                Endpoint = GetString(root, "endpoint", defaults.Endpoint),
                Model = GetString(root, "model", defaults.Model),
                AutoModelMaxBytes = GetLong(root, "autoModelMaxBytes", defaults.AutoModelMaxBytes),
                MaxToolSteps = Math.Clamp(GetInt(root, "maxToolSteps", defaults.MaxToolSteps), 1, 5),
                Normal = normal,
                Gaming = gaming,
                OwnServerModelsPath = GetString(root, "ownServerModelsPath", defaults.OwnServerModelsPath)
            };
        }
        catch (JsonException ex)
        {
            warnings?.Add("brain.json is not valid JSON (" + ex.Message + "); defaults used.");
            return new BrainSettings();
        }
    }

    /// <summary>Accepts "0", "0s", "45s", "2m", "1h" or a bare number of seconds.</summary>
    public static bool TryParseDuration(string? text, out TimeSpan value)
    {
        value = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim().ToLowerInvariant();

        double multiplier = 1;

        if (text.EndsWith("ms")) { multiplier = 0.001; text = text[..^2]; }
        else if (text.EndsWith('s')) { text = text[..^1]; }
        else if (text.EndsWith('m')) { multiplier = 60; text = text[..^1]; }
        else if (text.EndsWith('h')) { multiplier = 3600; text = text[..^1]; }

        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double number) ||
            number < 0 ||
            double.IsNaN(number) ||
            double.IsInfinity(number))
        {
            return false;
        }

        value = TimeSpan.FromSeconds(Math.Min(number * multiplier, 24 * 3600));
        return true;
    }

    private static GamingSettings ReadGaming(
        JsonElement? element,
        GamingSettings defaults,
        ICollection<string>? warnings)
    {
        if (element is null)
        {
            return defaults;
        }

        JsonElement e = element.Value;

        IReadOnlyList<string> processes = defaults.Processes;

        if (e.TryGetProperty("processes", out JsonElement list) &&
            list.ValueKind == JsonValueKind.Array)
        {
            processes =
                list.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => (x.GetString() ?? "").Trim())
                    .Where(x => x.Length > 0)
                    .ToArray();
        }

        return defaults with
        {
            Enabled = GetBool(e, "enabled", defaults.Enabled),
            Processes = processes,
            DetectFullscreen = GetBool(e, "detectFullscreen", defaults.DetectFullscreen),
            PollSeconds = Math.Clamp(GetInt(e, "pollSeconds", defaults.PollSeconds), 2, 60),
            PreferLightModel = GetBool(e, "preferLightModel", defaults.PreferLightModel),
            Mode = ReadMode(e, defaults.Mode, warnings, "gaming")
        };
    }

    private static BrainModeSettings ReadMode(
        JsonElement? element,
        BrainModeSettings defaults,
        ICollection<string>? warnings,
        string section)
    {
        if (element is null)
        {
            return defaults;
        }

        JsonElement e = element.Value;

        TimeSpan keepAlive = defaults.KeepAlive;

        if (e.TryGetProperty("keepAlive", out JsonElement ka))
        {
            string? text =
                ka.ValueKind switch
                {
                    JsonValueKind.String => ka.GetString(),
                    JsonValueKind.Number => ka.GetRawText(),
                    _ => null
                };

            if (TryParseDuration(text, out TimeSpan parsed))
            {
                keepAlive = parsed;
            }
            else
            {
                warnings?.Add($"{section}.keepAlive is not a duration; default used.");
            }
        }

        return defaults with
        {
            Model = GetString(e, "model", defaults.Model),
            KeepAlive = keepAlive,
            NumCtx = Math.Clamp(GetInt(e, "numCtx", defaults.NumCtx), 512, 8192),
            NumThread = Math.Clamp(GetInt(e, "numThread", defaults.NumThread), 0, 32),
            CpuOnly = GetBool(e, "cpuOnly", defaults.CpuOnly),
            FallbackToLightOnCpu = GetBool(e, "fallbackToLightOnCpu", defaults.FallbackToLightOnCpu),
            AnswerNumPredict = Math.Clamp(GetInt(e, "answerNumPredict", defaults.AnswerNumPredict), 16, 512),
            ToolNumPredict = Math.Clamp(GetInt(e, "toolNumPredict", defaults.ToolNumPredict), 16, 512),
            StepTimeoutSeconds = Math.Clamp(GetInt(e, "stepTimeoutSeconds", defaults.StepTimeoutSeconds), 3, 300),
            ColdLoadTimeoutSeconds = Math.Clamp(GetInt(e, "coldLoadTimeoutSeconds", defaults.ColdLoadTimeoutSeconds), 0, 300),
            TotalTimeoutSeconds = Math.Clamp(GetInt(e, "totalTimeoutSeconds", defaults.TotalTimeoutSeconds), 5, 600)
        };
    }

    private static JsonElement? GetObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.Object
            ? e
            : null;

    private static string GetString(JsonElement e, string name, string fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? fallback).Trim()
            : fallback;

    private static bool GetBool(JsonElement e, string name, bool fallback) =>
        e.TryGetProperty(name, out JsonElement v) &&
        (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : fallback;

    private static int GetInt(JsonElement e, string name, int fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n)
            ? n
            : fallback;

    private static long GetLong(JsonElement e, string name, long fallback) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long n)
            ? n
            : fallback;
}

/// <summary>The settings actually used for one request.</summary>
public sealed record BrainProfile(
    string Name,
    bool Gaming,
    string Model,
    BrainModeSettings Mode);

public static class BrainProfiles
{
    /// <summary>
    /// Like <see cref="Resolve(BrainSettings, bool)"/>, but also honours the resource monitor:
    /// when something else holds the GPU (another project's runs), EDITH goes straight to the
    /// light CPU profile instead of queueing behind it or evicting it.
    /// </summary>
    public static BrainProfile Resolve(BrainSettings settings, bool gameActive, Edith.Core.Resources.ResourcePlan? plan)
    {
        if (!gameActive &&
            plan is { Brain: Edith.Core.Resources.BrainPlacement.Cpu, GameActive: false } &&
            settings.Gaming.Enabled &&
            !string.IsNullOrWhiteSpace(settings.Gaming.Mode.Model))
        {
            return new BrainProfile("gpu-busy", true, settings.Gaming.Mode.Model, settings.Gaming.Mode);
        }

        return Resolve(settings, gameActive);
    }

    public static BrainProfile Resolve(BrainSettings settings, bool gameActive)
    {
        if (gameActive && settings.Gaming.Enabled)
        {
            string model =
                settings.Gaming.PreferLightModel &&
                !string.IsNullOrWhiteSpace(settings.Gaming.Mode.Model)
                    ? settings.Gaming.Mode.Model
                    : FirstNonEmpty(settings.Normal.Model, settings.Model);

            return new BrainProfile("gaming", true, model, settings.Gaming.Mode);
        }

        return new BrainProfile(
            "normal",
            false,
            FirstNonEmpty(settings.Normal.Model, settings.Model),
            settings.Normal);
    }

    private static string FirstNonEmpty(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a : b;
}
