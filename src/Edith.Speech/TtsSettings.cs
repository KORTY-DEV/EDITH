using System.Text.Json;

namespace Edith.Speech;

public sealed class TtsSettings
{
    public string Provider { get; set; } = "auto";
    public string ApiKeyPath { get; set; } = @"C:\EDITH\secrets\yandex-speechkit-api-key.txt";
    public string? ApiKey { get; set; }
    public string Voice { get; set; } = "marina";
    public string Role { get; set; } = "neutral";
    public string? Style { get; set; }
    public double Speed { get; set; } = 1.05;
    public int SampleRate { get; set; } = 48000;
    public string Fallback { get; set; } = "windows";
    public int TimeoutSeconds { get; set; } = 5;
    public string LocalEngine { get; set; } = "silero";
    public string LocalVoice { get; set; } = "xenia";
    public string LocalModelPath { get; set; } = @"C:\EDITH\models\tts\silero\v5_ru.pt";
    public string LocalPythonPath { get; set; } = @"C:\EDITH\data\tts-venv\Scripts\python.exe";
    public string LocalWorkerPath { get; set; } = @"C:\EDITH\scripts\tts\silero_worker.py";
    public int LocalNumThreads { get; set; } = 4;

    /// <summary>Silero speaking rate in percent (SSML prosody): slower without lowering the voice, unlike Speed.</summary>
    public int LocalRatePercent { get; set; } = 100;

    /// <summary>Breathing pauses after commas, dashes, colons and openers ("Хм, ...") for a livelier voice.</summary>
    public bool LocalPauses { get; set; } = true;

    /// <summary>RUAccent models (neural stress for every word); empty or missing folder = Silero's own stress plus config/stress.json.</summary>
    public string LocalAccentDir { get; set; } = @"C:\EDITH\models\ruaccent";

    public TtsSettings Normalize()
    {
        Provider = string.IsNullOrWhiteSpace(Provider) ? "auto" : Provider.Trim().ToLowerInvariant();
        Voice = string.IsNullOrWhiteSpace(Voice) ? "marina" : Voice.Trim();
        Role = string.IsNullOrWhiteSpace(Role) ? "neutral" : Role.Trim();
        Speed = Math.Clamp(Speed, 0.5, 2.0);
        SampleRate = SampleRate is 8000 or 16000 or 22050 or 24000 or 44100 or 48000 ? SampleRate : 48000;
        TimeoutSeconds = Math.Clamp(TimeoutSeconds, 2, 10);
        LocalEngine = string.IsNullOrWhiteSpace(LocalEngine) ? "silero" : LocalEngine.Trim().ToLowerInvariant();
        LocalVoice = string.IsNullOrWhiteSpace(LocalVoice) ? "xenia" : LocalVoice.Trim().ToLowerInvariant();
        if (LocalVoice is not ("xenia" or "kseniya" or "baya")) LocalVoice = "xenia";
        LocalRatePercent = LocalRatePercent <= 0 ? 100 : Math.Clamp(LocalRatePercent, 60, 140);
        LocalNumThreads = Math.Clamp(LocalNumThreads, 1, Math.Max(1, Environment.ProcessorCount));
        return this;
    }
}

public static class TtsSettingsLoader
{
    public static TtsSettings Load(string path)
    {
        TtsSettings settings = new();
        try
        {
            if (File.Exists(path))
            {
                settings = JsonSerializer.Deserialize<TtsSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new TtsSettings();
            }
        }
        catch
        {
            settings = new TtsSettings();
        }

        settings.Provider = Override(settings.Provider, "EDITH_TTS_PROVIDER");
        settings.ApiKeyPath = Override(settings.ApiKeyPath, "EDITH_TTS_API_KEY_PATH");
        settings.ApiKey = OverrideNullable(settings.ApiKey, "EDITH_TTS_API_KEY");
        settings.Voice = Override(settings.Voice, "EDITH_TTS_VOICE");
        settings.Role = Override(settings.Role, "EDITH_TTS_ROLE");
        settings.Style = OverrideNullable(settings.Style, "EDITH_TTS_STYLE");
        settings.Speed = OverrideDouble(settings.Speed, "EDITH_TTS_SPEED");
        settings.SampleRate = OverrideInt(settings.SampleRate, "EDITH_TTS_SAMPLE_RATE");
        settings.TimeoutSeconds = OverrideInt(settings.TimeoutSeconds, "EDITH_TTS_TIMEOUT_SECONDS");
        settings.Fallback = Override(settings.Fallback, "EDITH_TTS_FALLBACK");
        settings.LocalEngine = Override(settings.LocalEngine, "EDITH_TTS_LOCAL_ENGINE");
        settings.LocalVoice = Override(settings.LocalVoice, "EDITH_TTS_LOCAL_VOICE");
        settings.LocalModelPath = Override(settings.LocalModelPath, "EDITH_TTS_LOCAL_MODEL_PATH");
        settings.LocalPythonPath = Override(settings.LocalPythonPath, "EDITH_TTS_LOCAL_PYTHON_PATH");
        settings.LocalWorkerPath = Override(settings.LocalWorkerPath, "EDITH_TTS_LOCAL_WORKER_PATH");
        settings.LocalNumThreads = OverrideInt(settings.LocalNumThreads, "EDITH_TTS_LOCAL_THREADS");
        return settings.Normalize();
    }

    private static string Override(string value, string name)
    {
        string? env = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(env) ? value : env.Trim();
    }

    private static string? OverrideNullable(string? value, string name)
    {
        string? env = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(env) ? value : env.Trim();
    }

    private static double OverrideDouble(double value, string name)
    {
        string? env = Environment.GetEnvironmentVariable(name);
        return double.TryParse(env, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed) ? parsed : value;
    }

    private static int OverrideInt(int value, string name)
    {
        string? env = Environment.GetEnvironmentVariable(name);
        return int.TryParse(env, out int parsed) ? parsed : value;
    }
}
