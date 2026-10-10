using Edith.App;

namespace Edith.App.Tests;

public sealed class FirstRunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "edith-first-" + Guid.NewGuid().ToString("N"));

    public FirstRunTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private void Touch(string relative)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
    }

    [Fact]
    public void FreshFolder_ReportsModelsVoiceAndKey()
    {
        IReadOnlyList<string> problems = FirstRun.Problems(_root);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("Install-Models.ps1") && p.Contains("моделей"));
        Assert.Contains(problems, p => p.Contains("окружения голоса"));
        Assert.Contains(problems, p => p.Contains("GigaChat"));
    }

    [Fact]
    public void EverythingInPlace_ReportsNothing()
    {
        foreach (string file in new[]
        {
            @"models\tts\silero\v5_ru.pt",
            @"models\stt\sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16\model.int8.onnx",
            @"models\wake\vosk-model-small-ru-0.22\am\final.mdl",
            @"models\wake\custom-v3\edith-stage1.onnx",
            @"models\smart-turn\smart-turn-v3.2-cpu.onnx",
            @"models\embedding\model_quantized.onnx",
            @"data\tts-venv\Scripts\python.exe",
            @"secrets\gigachat-auth-key.txt",
        })
        {
            Touch(file);
        }

        Assert.Empty(FirstRun.Problems(_root));
    }
}
