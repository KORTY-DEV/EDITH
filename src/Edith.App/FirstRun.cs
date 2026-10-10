using System.IO;

namespace Edith.App;

/// <summary>What a fresh install still lacks, in words a person can act on. Empty when EDITH is ready to talk.</summary>
public static class FirstRun
{
    private static readonly (string File, string What)[] Models =
    [
        (@"models\tts\silero\v5_ru.pt", "голос Silero"),
        (@"models\stt\sherpa-onnx-nemo-ctc-giga-am-v3-russian-2025-12-16\model.int8.onnx", "распознавание речи GigaAM"),
        (@"models\wake\vosk-model-small-ru-0.22\am\final.mdl", "модель Vosk"),
        (@"models\wake\custom-v3\edith-stage1.onnx", "слово «Эдит» (должно быть в архиве EDITH)"),
        (@"models\smart-turn\smart-turn-v3.2-cpu.onnx", "определение конца фразы"),
        (@"models\embedding\model_quantized.onnx", "смысловой поиск"),
    ];

    public static IReadOnlyList<string> Problems(string root = @"C:\EDITH")
    {
        var problems = new List<string>();

        string[] missing = Models.Where(m => !File.Exists(Path.Combine(root, m.File))).Select(m => m.What).ToArray();
        if (missing.Length > 0)
            problems.Add("Не хватает моделей (" + string.Join(", ", missing) + "). Запустите scripts\\Install-Models.ps1 — он скачает недостающее.");

        if (!File.Exists(Path.Combine(root, @"data\tts-venv\Scripts\python.exe")))
            problems.Add("Нет окружения голоса (Python). Его ставит scripts\\Install-Models.ps1; без него EDITH говорит голосом Windows.");

        bool key = Edith.Speech.LocalSecrets.Exists(Path.Combine(root, @"secrets\gigachat-auth-key.txt"));
        if (!key)
            problems.Add("Нет ключа GigaChat: облако не работает. Вставьте ключ во вкладке «Настройки» (инструкция — docs\\GIGACHAT.md) или поставьте Ollama для локальной модели.");

        return problems;
    }
}
