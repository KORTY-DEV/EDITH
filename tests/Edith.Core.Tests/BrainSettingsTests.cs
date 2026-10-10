using Edith.Core.Brain;

namespace Edith.Core.Tests;

public class BrainSettingsTests
{
    [Fact]
    public void EmptyOrBrokenJson_GivesDefaults()
    {
        var warnings = new List<string>();

        Assert.Equal(new BrainSettings().MaxToolSteps, BrainSettingsLoader.Parse("", warnings).MaxToolSteps);
        Assert.Equal(new BrainSettings().MaxToolSteps, BrainSettingsLoader.Parse("{ not json", warnings).MaxToolSteps);
        Assert.Single(warnings);
    }

    [Fact]
    public void LegacyConfig_KeepsWorking()
    {
        // The shape shipped before the gaming profile existed (with a UTF-8 BOM, like the old file).
        var s = BrainSettingsLoader.Parse(
            "\uFEFF{\"enabled\":true,\"endpoint\":\"http://127.0.0.1:11434\",\"model\":\"qwen3:4b-instruct\"," +
            "\"autoModelMaxBytes\":4000000000,\"maxToolSteps\":5}");

        Assert.Equal("qwen3:4b-instruct", s.Model);
        Assert.Equal(5, s.MaxToolSteps);
        Assert.Equal(2048, s.Normal.NumCtx);
        Assert.True(s.Gaming.Enabled);
        Assert.Equal(TimeSpan.Zero, s.Gaming.Mode.KeepAlive);
    }

    [Fact]
    public void FullConfig_IsParsed()
    {
        var s = BrainSettingsLoader.Parse(
            """
            {
              "model": "big:9b",
              "normal": { "keepAlive": "2m", "numCtx": 4096, "answerNumPredict": 200 },
              "gaming": {
                "enabled": true,
                "processes": ["WorldOfTanks", "wot"],
                "detectFullscreen": false,
                "pollSeconds": 8,
                "preferLightModel": true,
                "model": "small:3b",
                "keepAlive": "0s",
                "stepTimeoutSeconds": 30
              }
            }
            """);

        Assert.Equal(TimeSpan.FromMinutes(2), s.Normal.KeepAlive);
        Assert.Equal(4096, s.Normal.NumCtx);
        Assert.Equal(200, s.Normal.AnswerNumPredict);
        Assert.Equal(new[] { "WorldOfTanks", "wot" }, s.Gaming.Processes);
        Assert.False(s.Gaming.DetectFullscreen);
        Assert.Equal(8, s.Gaming.PollSeconds);
        Assert.Equal("small:3b", s.Gaming.Mode.Model);
        Assert.Equal(TimeSpan.Zero, s.Gaming.Mode.KeepAlive);
        Assert.Equal(30, s.Gaming.Mode.StepTimeoutSeconds);
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        var s = BrainSettingsLoader.Parse(
            "{\"maxToolSteps\":99,\"normal\":{\"numCtx\":1000000,\"stepTimeoutSeconds\":0}}");

        Assert.Equal(5, s.MaxToolSteps);
        Assert.Equal(8192, s.Normal.NumCtx);
        Assert.Equal(3, s.Normal.StepTimeoutSeconds);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("0s", 0)]
    [InlineData("45s", 45)]
    [InlineData("2m", 120)]
    [InlineData("1h", 3600)]
    [InlineData("90", 90)]
    [InlineData("1500ms", 1.5)]
    public void Durations_AreParsed(string text, double seconds)
    {
        Assert.True(BrainSettingsLoader.TryParseDuration(text, out TimeSpan value));
        Assert.Equal(seconds, value.TotalSeconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("-5s")]
    public void BadDurations_AreRejected(string text)
    {
        Assert.False(BrainSettingsLoader.TryParseDuration(text, out _));
    }

    [Fact]
    public void KeepAliveForOllama_UsesIntegerZeroForUnload()
    {
        Assert.Equal(0, new BrainModeSettings { KeepAlive = TimeSpan.Zero }.KeepAliveForOllama);
        Assert.Equal("90s", new BrainModeSettings { KeepAlive = TimeSpan.FromSeconds(90) }.KeepAliveForOllama);
    }

    [Fact]
    public void NormalProfile_UsesTopLevelModel()
    {
        var s = new BrainSettings { Model = "big:9b", Gaming = new GamingSettings { Mode = new BrainModeSettings { Model = "small:3b" } } };
        var p = BrainProfiles.Resolve(s, gameActive: false);

        Assert.Equal("normal", p.Name);
        Assert.Equal("big:9b", p.Model);
        Assert.False(p.Gaming);
    }

    [Fact]
    public void GamingProfile_PrefersLightModelAndUnloads()
    {
        var s = new BrainSettings
        {
            Model = "big:9b",
            Gaming = new GamingSettings
            {
                PreferLightModel = true,
                Mode = new BrainModeSettings { Model = "small:3b", KeepAlive = TimeSpan.Zero }
            }
        };

        var p = BrainProfiles.Resolve(s, gameActive: true);

        Assert.Equal("gaming", p.Name);
        Assert.Equal("small:3b", p.Model);
        Assert.Equal(0, p.Mode.KeepAliveForOllama);
    }

    [Fact]
    public void GamingProfile_WithoutLightModel_FallsBackToNormalModel()
    {
        var s = new BrainSettings { Model = "big:9b", Gaming = new GamingSettings { PreferLightModel = false, Mode = new BrainModeSettings { Model = "small:3b" } } };
        Assert.Equal("big:9b", BrainProfiles.Resolve(s, true).Model);

        var s2 = new BrainSettings { Model = "big:9b" };
        Assert.Equal("big:9b", BrainProfiles.Resolve(s2, true).Model);
    }

    [Fact]
    public void GamingDisabled_NeverSwitchesProfile()
    {
        var s = new BrainSettings { Model = "big:9b", Gaming = new GamingSettings { Enabled = false } };
        Assert.Equal("normal", BrainProfiles.Resolve(s, gameActive: true).Name);
    }
}

public class GameModeTests
{
    private static readonly string[] Patterns = { "WorldOfTanks", "Tanki", "wot" };

    [Theory]
    [InlineData("WorldOfTanks.exe", true)]
    [InlineData("worldoftanks", true)]
    [InlineData("TankiOnline", true)]
    [InlineData("wot", true)]
    [InlineData("wot.exe", true)]
    [InlineData("wotan-notes", false)]
    [InlineData("notepad", false)]
    [InlineData("", false)]
    public void ProcessMatching(string process, bool expected)
    {
        Assert.Equal(expected, GameProcessMatcher.FindMatch(new[] { process }, Patterns) is not null);
    }

    [Fact]
    public void NoPatterns_NeverMatch()
    {
        Assert.Null(GameProcessMatcher.FindMatch(new[] { "WorldOfTanks" }, Array.Empty<string>()));
    }

    [Fact]
    public void Monitor_DetectsProcessAndCachesWithinPollInterval()
    {
        int scans = 0;
        var names = new List<string> { "chrome" };
        var now = DateTimeOffset.Parse("2026-09-19T12:00:00Z");

        var monitor = new GameModeMonitor(() => { scans++; return names; }, () => false, () => now);
        var settings = new GamingSettings { PollSeconds = 5 };

        Assert.False(monitor.Evaluate(settings).Active);
        Assert.Equal(1, scans);

        names.Add("WorldOfTanks.exe");
        now = now.AddSeconds(2);
        Assert.False(monitor.Evaluate(settings).Active); // cached
        Assert.Equal(1, scans);

        now = now.AddSeconds(4);
        GameModeState state = monitor.Evaluate(settings);
        Assert.True(state.Active);
        Assert.StartsWith("process:", state.Reason);
        Assert.Equal(2, scans);
    }

    [Fact]
    public void Monitor_FullscreenProbe_OnlyWhenEnabled()
    {
        var now = DateTimeOffset.UtcNow;
        var monitor = new GameModeMonitor(() => new[] { "chrome" }, () => true, () => now);

        Assert.True(monitor.Evaluate(new GamingSettings { DetectFullscreen = true }).Active);

        now = now.AddMinutes(1);
        Assert.False(monitor.Evaluate(new GamingSettings { DetectFullscreen = false }).Active);
    }

    [Fact]
    public void Monitor_DisabledOrThrowingProbe_MeansNoGame()
    {
        var monitor = new GameModeMonitor(() => throw new InvalidOperationException(), () => true);

        Assert.False(monitor.Evaluate(new GamingSettings { Enabled = false }).Active);
        Assert.False(monitor.Evaluate(new GamingSettings { Enabled = true, PollSeconds = 2 }).Active);
    }

    [Fact]
    public void Monitor_ForceOverridesEverything()
    {
        var monitor = new GameModeMonitor(() => new[] { "notepad" }, () => false);
        var settings = new GamingSettings();

        monitor.Force(true);
        Assert.True(monitor.Evaluate(settings).Active);

        monitor.Force(null);
        Assert.False(monitor.Evaluate(settings).Active);
    }
}

public class BrainDecisionParserTests
{
    [Fact]
    public void Answer_IsParsed()
    {
        var d = BrainDecisionParser.Parse("{\"type\":\"answer\",\"text\":\"Готово.\"}");
        Assert.Equal("answer", d.Type);
        Assert.Equal("Готово.", d.Text);
    }

    [Fact]
    public void Tool_IsParsedWithArguments()
    {
        var d = BrainDecisionParser.Parse("{\"type\":\"tool\",\"tool\":\"find_files\",\"arguments\":{\"root\":\"C:\\\\EDITH\",\"pattern\":\"*.cs\"}}");
        Assert.Equal("tool", d.Type);
        Assert.Equal("find_files", d.Tool);
        Assert.Equal("*.cs", d.Arguments.GetProperty("pattern").GetString());
    }

    [Fact]
    public void FencedJson_IsUnwrapped()
    {
        var d = BrainDecisionParser.Parse("```json\n{\"type\":\"tool\",\"tool\":\"processes\",\"arguments\":{}}\n```");
        Assert.Equal("processes", d.Tool);
    }

    [Fact]
    public void JsonInsideProse_IsExtracted()
    {
        var d = BrainDecisionParser.Parse("Сейчас проверю. {\"type\":\"tool\",\"tool\":\"gpu_status\",\"arguments\":{}} Готово.");
        Assert.Equal("gpu_status", d.Tool);
    }

    [Fact]
    public void BareAnswerEnvelope_IsAccepted()
    {
        var d = BrainDecisionParser.Parse("{\"answer\":\"Париж\"}");
        Assert.Equal("answer", d.Type);
        Assert.Equal("Париж", d.Text);
    }

    [Fact]
    public void ToolWithoutType_IsATool()
    {
        var d = BrainDecisionParser.Parse("{\"tool\":\"system_snapshot\",\"arguments\":{}}");
        Assert.Equal("tool", d.Type);
        Assert.Equal("system_snapshot", d.Tool);
    }

    [Fact]
    public void PlainText_BecomesAnAnswer()
    {
        var d = BrainDecisionParser.Parse("Просто текст без JSON.");
        Assert.Equal("answer", d.Type);
        Assert.Equal("Просто текст без JSON.", d.Text);
    }

    [Fact]
    public void BracesInsideStrings_DoNotBreakExtraction()
    {
        var d = BrainDecisionParser.Parse("ok {\"type\":\"answer\",\"text\":\"скобка } внутри\"} хвост");
        Assert.Equal("скобка } внутри", d.Text);
    }
}

public class VoiceTextTests
{
    [Fact]
    public void Markdown_IsRemoved()
    {
        Assert.Equal("Это важно и код.", VoiceText.Sanitize("**Это** важно и `код`."));
        Assert.Equal("Первый Второй", VoiceText.Sanitize("- Первый\n- Второй"));
        Assert.Equal("Заголовок текст", VoiceText.Sanitize("# Заголовок\nтекст"));
    }

    [Fact]
    public void LongText_IsCutAtSentence()
    {
        string text = string.Concat(Enumerable.Repeat("Это длинное предложение для проверки. ", 30));
        string result = VoiceText.Sanitize(text, 100);

        Assert.True(result.Length <= 100);
        Assert.EndsWith(".", result);
    }

    [Fact]
    public void Empty_StaysEmpty()
    {
        Assert.Equal("", VoiceText.Sanitize("   "));
    }

    [Fact]
    public void ForSpeech_RemovesUrlsAndJsonNoise_ButKeepsPrice()
    {
        string result = VoiceText.ForSpeech("Цена: 624990 ₽. https://example.test/x {\"source\":\"store\"}");
        Assert.Contains("шестьсот двадцать четыре тысячи девятьсот девяносто рублей", result);
        Assert.DoesNotContain("http", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForSpeech_KeepsCompleteCanonicalGpuAnswer()
    {
        string text = "Сейчас по доступным независимым тестам лидирует NVIDIA GeForce RTX 5090. " +
                      "Она обеспечивает максимальную производительность в актуальных играх и приложениях.";
        string result = VoiceText.ForSpeech(text);
        Assert.Contains("энвидиа джифорс эр тэ икс пятьдесят девяносто", result);
        Assert.EndsWith(".", result);
        Assert.DoesNotContain("…", result);
    }
}

public class BrainLatencyRecordTests
{
    [Fact]
    public void Format_ContainsEveryRequiredField()
    {
        var record = new BrainLatencyRecord
        {
            Profile = "gaming",
            Model = "qwen3.5:4b",
            State = "cold",
            Mode = "cpu",
            LoadMs = 4141.4,
            PromptEvalMs = 6118.6,
            GenerationMs = 2688,
            TotalMs = 12950,
            WallMs = 12965,
            PromptTokens = 489,
            GeneratedTokens = 27,
            ToolStep = "0:tool=system_snapshot",
            TimeoutReason = "none",
            Game = "process:WorldOfTanks"
        };

        string line = record.Format(DateTimeOffset.Parse("2026-09-19T12:00:00+04:00"));

        Assert.StartsWith("2026-09-19T12:00:00.000+04:00 ", line);
        Assert.Contains("profile=gaming", line);
        Assert.Contains("model=qwen3.5:4b", line);
        Assert.Contains("state=cold", line);
        Assert.Contains("mode=cpu", line);
        Assert.Contains("load_ms=4141", line);
        Assert.Contains("prompt_eval_ms=6119", line);
        Assert.Contains("generation_ms=2688", line);
        Assert.Contains("total_ms=12950", line);
        Assert.Contains("prompt_tokens=489", line);
        Assert.Contains("generated_tokens=27", line);
        Assert.Contains("tool_step=0:tool=system_snapshot", line);
        Assert.Contains("timeout=none", line);
        Assert.Contains("game=process:WorldOfTanks", line);
        Assert.DoesNotContain("\n", line);
    }

    [Theory]
    [InlineData(3_000_000_000, 0, "cpu")]
    [InlineData(3_000_000_000, 3_000_000_000, "gpu(100%)")]
    [InlineData(4_000_000_000, 1_000_000_000, "gpu/cpu(25%)")]
    [InlineData(0, 0, "unknown")]
    public void DescribeMode(long size, long vram, string expected)
    {
        Assert.Equal(expected, BrainLatencyRecord.DescribeMode(size, vram));
    }
}

public class BrainModeThreadTests
{
    [Fact]
    public void NumThreadAndCpuOnly_AreParsedAndGamingDefaultsToCpu()
    {
        var s = BrainSettingsLoader.Parse(
            "{\"gaming\":{\"numThread\":3,\"cpuOnly\":true},\"normal\":{\"numThread\":99}}");

        Assert.Equal(3, s.Gaming.Mode.NumThread);
        Assert.True(s.Gaming.Mode.CpuOnly);
        Assert.Equal(32, s.Normal.NumThread);
        Assert.False(s.Normal.CpuOnly);
        Assert.False(BrainSettingsLoader.Parse("{\"normal\":{\"fallbackToLightOnCpu\":false}}").Normal.FallbackToLightOnCpu);
        Assert.True(new BrainSettings().Gaming.Mode.CpuOnly);
    }
}

public class BrainAvailabilityPolicyTests
{
    [Fact]
    public void BusyPrimary_UsesAvailableCpuFallback()
    {
        Assert.Equal(BrainAvailabilityAction.UseCpuFallback,
            BrainAvailabilityPolicy.Decide(primaryIsBlocked: true, cpuFallbackIsAvailable: true));
    }

    [Fact]
    public void BusyPrimaryWithoutFallback_ReturnsGracefulUnavailable()
    {
        Assert.Equal(BrainAvailabilityAction.Unavailable,
            BrainAvailabilityPolicy.Decide(primaryIsBlocked: true, cpuFallbackIsAvailable: false));
        Assert.Equal("Не смогла получить ответ сейчас.",
            BrainAvailabilityPolicy.UnavailableReply);
    }
}

public class ShippedBrainConfigTests
{
    [Fact]
    public void ShippedBrainJson_HasNormalAndGamingProfiles()
    {
        string path = Path.Combine(FindRepoRoot(), "config", "brain.json");
        var warnings = new List<string>();
        var s = BrainSettingsLoader.Parse(File.ReadAllText(path), warnings);

        Assert.Empty(warnings);
        Assert.Equal("qwen3:8b", s.Model);
        Assert.Equal(TimeSpan.FromHours(3), s.Normal.KeepAlive);
        Assert.False(s.Normal.CpuOnly);
        Assert.True(s.Normal.FallbackToLightOnCpu);

        Assert.True(s.Gaming.Enabled);
        Assert.Contains("WorldOfTanks", s.Gaming.Processes);
        Assert.True(s.Gaming.DetectFullscreen);
        Assert.Equal("ministral-3:3b", s.Gaming.Mode.Model);
        Assert.True(s.Gaming.Mode.CpuOnly);
        Assert.InRange(s.Gaming.Mode.NumThread, 1, 6);

        var gaming = BrainProfiles.Resolve(s, gameActive: true);
        Assert.Equal("ministral-3:3b", gaming.Model);
        Assert.Equal(2048, gaming.Mode.NumCtx);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;

        while (dir is not null && !File.Exists(Path.Combine(dir, "EDITH.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        // Scratch build outputs live outside the repo.
        return dir ?? @"C:\EDITH";
    }
}
