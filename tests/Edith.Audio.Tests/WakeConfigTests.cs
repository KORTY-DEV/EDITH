using Edith.Audio.Tests.Support;
using Edith.Audio.Wake;

namespace Edith.Audio.Tests;

public class WakeConfigTests
{
    private const string Root =
        @"C:\EDITH";

    private static WakeConfig Parse(
        string json,
        out List<string> warnings)
    {
        warnings = [];

        return WakeConfigLoader.Parse(
            json,
            Root,
            warnings);
    }


    [Fact]
    public void FullConfig_IsParsedAsSpecified()
    {
        var config =
            Parse(
                """
                {
                  "engine": "auto",
                  "customModel": "C:\\EDITH\\models\\wake\\custom\\edith.onnx",
                  "melModel": "C:\\EDITH\\models\\wake\\custom\\melspectrogram.onnx",
                  "embeddingModel": "C:\\EDITH\\models\\wake\\custom\\embedding_model.onnx",
                  "threshold": 0.7,
                  "minConsecutiveHits": 2,
                  "cooldownMs": 1500,
                  "fallbackToVosk": true
                }
                """,
                out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(WakeEngineMode.Auto, config.Engine);
        Assert.Equal(
            @"C:\EDITH\models\wake\custom\edith.onnx",
            config.CustomModel);
        Assert.Equal(
            @"C:\EDITH\models\wake\custom\melspectrogram.onnx",
            config.MelModel);
        Assert.Equal(
            @"C:\EDITH\models\wake\custom\embedding_model.onnx",
            config.EmbeddingModel);
        Assert.Equal(0.7f, config.Threshold);
        Assert.Equal(2, config.MinConsecutiveHits);
        Assert.Equal(1500, config.CooldownMs);
        Assert.True(config.FallbackToVosk);
    }


    [Theory]
    [InlineData("auto", WakeEngineMode.Auto)]
    [InlineData("CUSTOM", WakeEngineMode.Custom)]
    [InlineData("wake-v3", WakeEngineMode.CustomV3)]
    [InlineData("Vosk", WakeEngineMode.Vosk)]
    [InlineData(" vosk ", WakeEngineMode.Vosk)]
    public void Engine_IsCaseInsensitive(
        string text,
        WakeEngineMode expected)
    {
        var config =
            Parse(
                $$"""{ "engine": "{{text}}" }""",
                out var warnings);

        Assert.Equal(expected, config.Engine);
        Assert.Empty(warnings);
    }


    [Fact]
    public void V3PathsAndVerifierThreshold_AreParsed()
    {
        var config = Parse("""{ "engine":"custom-v3", "customV3Model":"models\\s1.onnx", "verifierModel":"models\\v.onnx", "verifierThreshold":0.8 }""", out var warnings);
        Assert.Empty(warnings);
        Assert.Equal(WakeEngineMode.CustomV3, config.Engine);
        Assert.Equal(@"C:\EDITH\models\s1.onnx", config.CustomV3Model);
        Assert.Equal(@"C:\EDITH\models\v.onnx", config.VerifierModel);
        Assert.Equal(0.8f, config.VerifierThreshold);
    }


    [Fact]
    public void UnknownEngine_FallsBackToAutoWithWarning()
    {
        var config =
            Parse(
                """{ "engine": "porcupine" }""",
                out var warnings);

        Assert.Equal(WakeEngineMode.Auto, config.Engine);
        Assert.Single(warnings);
    }


    [Fact]
    public void MissingFields_UseDefaults()
    {
        var config =
            Parse("{}", out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(new WakeConfig(), config);
        Assert.Equal(0.7f, config.Threshold);
        Assert.Equal(2, config.MinConsecutiveHits);
        Assert.Equal(1500, config.CooldownMs);
        Assert.True(config.FallbackToVosk);
    }


    [Theory]
    [InlineData("0")]
    [InlineData("-0.5")]
    [InlineData("1.5")]
    [InlineData("\"high\"")]
    public void InvalidThreshold_UsesDefaultWithWarning(
        string value)
    {
        var config =
            Parse(
                $$"""{ "threshold": {{value}} }""",
                out var warnings);

        Assert.Equal(0.7f, config.Threshold);
        Assert.Single(warnings);
    }


    [Theory]
    [InlineData("minConsecutiveHits", "0")]
    [InlineData("minConsecutiveHits", "100")]
    [InlineData("minConsecutiveHits", "1.5")]
    [InlineData("cooldownMs", "-1")]
    [InlineData("cooldownMs", "999999")]
    [InlineData("fallbackToVosk", "\"yes\"")]
    public void InvalidNumbersAndBooleans_UseDefaults(
        string name,
        string value)
    {
        var config =
            Parse(
                $$"""{ "{{name}}": {{value}} }""",
                out var warnings);

        Assert.Equal(new WakeConfig(), config);
        Assert.Single(warnings);
    }


    [Fact]
    public void PropertyNames_AreCaseInsensitive()
    {
        var config =
            Parse(
                """{ "Threshold": 0.9, "COOLDOWNMS": 10 }""",
                out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(0.9f, config.Threshold);
        Assert.Equal(10, config.CooldownMs);
    }


    [Fact]
    public void CommentsAndTrailingCommas_AreTolerated()
    {
        var config =
            Parse(
                """
                {
                  // порог
                  "threshold": 0.8,
                }
                """,
                out var warnings);

        Assert.Empty(warnings);
        Assert.Equal(0.8f, config.Threshold);
    }


    [Theory]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("42")]
    public void BrokenJson_NeverThrows_ReturnsDefaults(
        string json)
    {
        var config =
            Parse(json, out var warnings);

        Assert.Equal(new WakeConfig(), config);
        Assert.NotEmpty(warnings);
    }


    [Fact]
    public void RelativePaths_ResolveAgainstEdithRoot()
    {
        var config =
            Parse(
                """{ "customModel": "models\\wake\\x.onnx" }""",
                out _);

        Assert.Equal(
            @"C:\EDITH\models\wake\x.onnx",
            config.CustomModel);
    }


    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        using var dir = new TempDir();

        var result =
            WakeConfigLoader.Load(
                dir.File("wake.json"));

        Assert.False(result.FileFound);
        Assert.Equal(new WakeConfig(), result.Config);
    }


    [Fact]
    public void Load_ReadsFileAndResolvesRelativeToParentOfConfigDir()
    {
        using var dir = new TempDir();

        string configDir =
            Path.Combine(dir.Path, "config");

        Directory.CreateDirectory(configDir);

        string file =
            Path.Combine(configDir, "wake.json");

        File.WriteAllText(
            file,
            """{ "engine": "vosk", "customModel": "m\\e.onnx" }""");

        var result =
            WakeConfigLoader.Load(file);

        Assert.True(result.FileFound);
        Assert.Equal(WakeEngineMode.Vosk, result.Config.Engine);
        Assert.Equal(
            Path.Combine(dir.Path, "m", "e.onnx"),
            result.Config.CustomModel);
    }


    // Контракт поставляемого по умолчанию конфига. Живой C:\EDITH\config\wake.json
    // пользователь вправе менять (например, engine=vosk), поэтому тест его не читает:
    // проверяется то, что получает установка без файла / с пустым файлом.
    [Fact]
    public void ShippedDefaults_MatchSpecifiedContract()
    {
        var shipped =
            WakeConfigLoader.Load(
                Path.Combine(
                    Path.GetTempPath(),
                    "edith-no-such-dir-" + Guid.NewGuid().ToString("N"),
                    "wake.json"));

        var result =
            WakeConfigLoader.Parse(
                "{}",
                @"C:\EDITH",
                new List<string>());

        foreach (var config in new[] { shipped.Config, result })
        {
            Assert.Equal(WakeEngineMode.Auto, config.Engine);
            Assert.Equal(0.7f, config.Threshold);
            Assert.Equal(2, config.MinConsecutiveHits);
            Assert.Equal(1500, config.CooldownMs);
            Assert.True(config.FallbackToVosk);
            Assert.EndsWith("edith.onnx", config.CustomModel);
        }
    }

    [Fact]
    public void InjectedDefaults_ReplaceProductionPaths_ForMissingBrokenAndPartialConfig()
    {
        var isolated =
            new WakeConfig
            {
                CustomModel = @"X:\iso\edith.onnx",
                MelModel = @"X:\iso\mel.onnx",
                EmbeddingModel = @"X:\iso\emb.onnx"
            };

        using var dir = new TempDir();

        string broken = dir.File("broken.json");
        File.WriteAllText(broken, "{ not json");

        Assert.Equal(
            isolated.CustomModel,
            WakeConfigLoader.Load(dir.File("missing.json"), null, isolated).Config.CustomModel);

        Assert.Equal(
            isolated.CustomModel,
            WakeConfigLoader.Load(broken, null, isolated).Config.CustomModel);

        string partial = dir.File("partial.json");
        File.WriteAllText(partial, "{ \"engine\": \"vosk\" }");

        var loaded = WakeConfigLoader.Load(partial, null, isolated).Config;

        Assert.Equal(WakeEngineMode.Vosk, loaded.Engine);
        Assert.Equal(isolated.MelModel, loaded.MelModel);
    }
}
