using Edith.Speech;
using System.Diagnostics;

if (args.Length == 0 || !args[0].Equals("bench", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Usage: Edith.SpeechCheck bench");
    return 2;
}

TtsSettings settings = TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json");
settings.Provider = "local";
settings.LocalEngine = "silero";
if (!File.Exists(settings.LocalModelPath) || !File.Exists(settings.LocalPythonPath) || !File.Exists(settings.LocalWorkerPath))
{
    Console.Error.WriteLine("Silero local runtime is not configured.");
    Console.Error.WriteLine($"Model: {settings.LocalModelPath}");
    Console.Error.WriteLine($"Python: {settings.LocalPythonPath}");
    return 3;
}

const string phrase = "Сейчас по независимым тестам лидирует NVIDIA GeForce RTX 5090. Нашла актуальное предложение за 624 990 рублей в магазине Регард.";
IReadOnlyList<string> chunks = SileroTextChunker.Split(phrase);
Console.WriteLine($"chunk-count={chunks.Count} chars={phrase.Length}");
using var service = new SileroTtsService(settings);
foreach (string voice in new[] { "xenia" })
{
    Console.WriteLine($"=== {voice} ===");
    Stopwatch clock = Stopwatch.StartNew();
    await service.SpeakAsync(phrase, voiceOverride: voice);
    Console.WriteLine($"complete-ms={clock.Elapsed.TotalMilliseconds:F0}");
}

return 0;
