using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Edith.App;

namespace Edith.BrainCheck;

// Real end-to-end check of the EDITH brain without the microphone and without the app window:
// the phrases go through the same ResponseEngine (Reflex first, then BrainService) that EDITH uses.
//
//   Edith.BrainCheck [--game 0|1] [--config C:\path\brain.json] [--phrase "text"]...
//
// --game 1   forces the Gaming profile (like a running game), --game 0 forces Normal.
//            Without it the real GameMode detector decides.
// --config   alternative brain.json (sets EDITH_BRAIN_CONFIG) - e.g. another Ollama endpoint.
//
// For every phrase it prints the answer, wall time, whether the LLM was involved (new lines in
// brain-latency.txt) and, after LLM answers, what Ollama still keeps loaded and the VRAM/RAM deltas.
internal static class Program
{
    private const string Latency =
        @"C:\EDITH\data\diagnostics\brain-latency.txt";

    private static readonly (string Phrase, string Kind)[] Scenarios =
    {
        ("который час", "reflex"),
        ("сколько оперативки", "reflex"),
        ("сколько свободной оперативной памяти", "reflex"),
        ("объясни коротко, что такое DNS", "llm-knowledge"),
        ("какая сейчас загрузка видеокарты", "llm-tool"),
        ("оцени, хватает ли компьютеру ресурсов: память и диски", "llm-tool"),
        ("придумай имя для рыжего кота", "llm-free"),
    };

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --with-browser: the brain's browser_* tools get a headless Chromium (as in the app, visible there).
        PlaywrightBrowser? sharedBrowser = null;
        if (args.Contains("--with-browser"))
        {
            sharedBrowser = new PlaywrightBrowser(headless: true, profilePath: Path.Combine(Path.GetTempPath(), "edith-web-" + Guid.NewGuid().ToString("N")));
            BrainToolRouter.BrowserProvider = () => sharedBrowser;
        }

        // --episodes: summarise finished conversations in the real memory database right now
        // (what the background timer does when EDITH is idle), and print what it wrote.
        if (args.Contains("--episodes"))
            return await RunEpisodesAsync();

        // --browser-smoke: the browser_* hands on a local page with real Chromium (headless).
        if (args.Contains("--browser-smoke"))
            return await RunBrowserSmokeAsync();

        // --pulse: the repositories and program usage as the background pulse sees them (dossier + cards, nothing saved).
        if (args.Contains("--pulse"))
        {
            PulseSettings settings = PulseSettings.Load();
            var repos = settings.Repos.Select(path => Edith.Core.Activity.GitActivity.Read(path)).OfType<Edith.Core.Activity.RepoState>().ToList();
            foreach (var repo in repos)
                Console.WriteLine($"{repo.Name} [{repo.Branch}] last={repo.LastCommit:yyyy-MM-dd} week={repo.Commits7d} dirty={repo.DirtyFiles} — {repo.LastSubject}");
            var store = new Edith.Core.Activity.ActivityStore(@"C:\EDITH\data\memory\activity.db");
            var result = Edith.Core.Activity.PulseBuilder.Build(repos, store.Usage(DateTimeOffset.Now.AddDays(-14), DateTimeOffset.Now), DateTimeOffset.Now);
            Console.WriteLine("dossier: " + result.Dossier);
            foreach (var card in result.Cards)
                Console.WriteLine($"card [{card.Key}] {card.Title}: {card.Body}");
            return 0;
        }

        // --say "text": speaks the text with the real voice (to hear pauses and intonation).
        if (args.Contains("--say"))
        {
            var voice = new Edith.Speech.HybridTtsService(Edith.Speech.TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json"));
            await voice.WarmUpAsync();
            await voice.SpeakAsync(args.SkipWhile(a => a != "--say").Skip(1).First());
            return 0;
        }

        // --tts-check [FILE]: each phrase goes through the real preparation and the real voice, then GigaAM listens to the result;
        // "heard" next to "meant" shows where the voice stumbles (digits, names, abbreviations) without anyone listening.
        static int EditDistance(string a, string b)
        {
            var previous = Enumerable.Range(0, b.Length + 1).ToArray();
            for (int i = 1; i <= a.Length; i++)
            {
                var current = new int[b.Length + 1];
                current[0] = i;
                for (int j = 1; j <= b.Length; j++)
                    current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                previous = current;
            }

            return previous[b.Length];
        }

        if (args.Contains("--tts-check"))
        {
            string[] rest = args.SkipWhile(a => a != "--tts-check").Skip(1).ToArray();
            string[] checkPhrases = rest.Length > 0 && File.Exists(rest[0])
                ? File.ReadAllLines(rest[0]).Where(l => l.Trim().Length > 0).ToArray()
                :
                [
                    "Файл лежит в C:/EDITH/config.", "Он живёт в г. Самара на ул. Мира, д. 5.", "Температура +16°C, ветер 3 м/с, влажность 60%.",
                    "Версия 1.0.7 вышла 10.10.2026.", "Видеокарта RTX 5090 стоит около 300 тыс. руб.", "Напомню через 25 минут, в 19:30 и завтра в полночь.",
                    "Памяти осталось 8 192 МБ, это 3/4 от всей.", "Откройте Discord, Steam и Chrome.", "Это было в 2026 г. В 1990-х было иначе.",
                    "Три плюс пять равно восемь.", "Сейчас в Самаре около 16 градусов, пасмурно.", "Ничего страшного, сэр, всё под контролем.",
                ];
            Edith.Core.Brain.VoiceText.NumberWords = Edith.Speech.SpokenNumbers.Apply;
            var voice = new Edith.Speech.HybridTtsService(Edith.Speech.TtsSettingsLoader.Load(@"C:\EDITH\config	ts.json"));
            await voice.WarmUpAsync();
            using var giga = new Edith.Audio.GigaAmCommandRecognizer(Edith.Audio.SttSettings.Load(@"C:\EDITH\config\stt.json"));
            int weak = 0;
            foreach (string phrase in checkPhrases)
            {
                string meant = Edith.Core.Brain.VoiceText.ForSpeech(phrase);
                string wav = Path.Combine(Path.GetTempPath(), "edith-ttscheck-" + Guid.NewGuid().ToString("N") + ".wav");
                await voice.RenderToFileAsync(meant, wav);
                using var reader = new NAudio.Wave.AudioFileReader(wav);
                NAudio.Wave.ISampleProvider source = reader;
                if (reader.WaveFormat.Channels == 2)
                    source = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(source);
                var mono = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(source, 16000);
                var samples = new List<float>();
                var buffer = new float[16000];
                int n;
                while ((n = mono.Read(buffer.AsSpan())) > 0)
                    samples.AddRange(buffer.Take(n));
                string heard = giga.Decode(samples.ToArray()).NormalizedText;
                string Plain(string t) => System.Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant().Replace('ё', 'е'), @"[^\p{L}\d ]", " ").Replace("  ", " ").Trim();
                string a = Plain(meant), b = Plain(heard);
                int distance = EditDistance(a, b);
                double similarity = 1.0 - (double)distance / Math.Max(1, Math.Max(a.Length, b.Length));
                bool bad = similarity < 0.75;
                if (bad) weak++;
                Console.WriteLine($"{(bad ? "WEAK " : "ok   ")}{similarity:0.00}  {phrase}" + Environment.NewLine + $"         meant: {meant}" + Environment.NewLine + $"         heard: {heard}");
                try { File.Delete(wav); } catch { }
            }

            Console.WriteLine($"{checkPhrases.Length - weak}/{checkPhrases.Length} close to what was meant");
            return weak == 0 ? 0 : 3;
        }

        // --screen-check DIR [describe]: the monitors as EDITH sees them (saved as pictures); with "describe" the cloud tells what is on the one under the mouse.
        if (args.Contains("--screen-check"))
        {
            string[] rest = args.SkipWhile(a => a != "--screen-check").Skip(1).ToArray();
            string dir = rest[0];
            Directory.CreateDirectory(dir);
            foreach (var m in Edith.App.WindowsScreen.Monitors())
                Console.WriteLine($"monitor {m.Name}: {m.Width}x{m.Height} at {m.Left},{m.Top}{(m.Primary ? " (primary)" : "")}");
            Console.WriteLine("under the mouse: " + Edith.App.WindowsScreen.CursorMonitor()?.Name);
            var shots = Edith.App.WindowsScreen.CaptureMonitors(Edith.App.WindowsScreen.ScreenTarget.All);
            foreach (var shot in shots)
            {
                string file = Path.Combine(dir, shot.Name.Replace(' ', '-') + ".jpg");
                File.WriteAllBytes(file, shot.Jpeg);
                Console.WriteLine($"saved {file} ({shot.Jpeg.Length} bytes)");
            }

            if (rest.Contains("describe"))
            {
                string region = rest.SkipWhile(a => a != "describe").Skip(1).FirstOrDefault() ?? "";
                var mine = Edith.App.WindowsScreen.CaptureMonitors(Edith.App.WindowsScreen.ScreenTarget.Cursor, region).First();
                File.WriteAllBytes(Path.Combine(dir, "described.jpg"), mine.Jpeg);
                var onScreen = Edith.App.WindowsScreen.WindowsOn(Edith.App.WindowsScreen.CursorMonitor()!);
                Console.WriteLine("windows: " + string.Join(" | ", onScreen));
                string seen = await Edith.App.WindowsScreen.OcrPngAsync(mine.Png, CancellationToken.None);
                Console.WriteLine("ocr chars: " + seen.Length);
                Console.WriteLine(await GigaChatClient.Shared.DescribeImageAsync(CloudSettings.Load(), mine.Jpeg,
                    "Опиши, что показано на экране: какая программа или сайт, что главное. Текст с экрана (возможны ошибки): " + (seen.Length > 1500 ? seen[..1500] : seen), CancellationToken.None));
            }

            return 0;
        }

        if (args.Contains("--voice-samples"))
        {
            var samples = new Edith.Speech.HybridTtsService(Edith.Speech.TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json"));
            await samples.WarmUpAsync();
            Console.WriteLine($"rendered {await samples.RenderVoiceSamplesAsync()} voice samples");
            return 0;
        }

        // --session-dump DIR: the model prompt of one session per source, as files (for model comparisons).
        if (args.Contains("--session-dump"))
        {
            string dir = args.SkipWhile(a => a != "--session-dump").Skip(1).First();
            Directory.CreateDirectory(dir);
            var settings0 = LibrarySettings.Load();
            var found = Edith.Core.Library.SessionSources.Find(settings0.Sources, TimeSpan.FromHours(1), 60).ToList();
            foreach (string source in settings0.Sources.Select(x => x.Id))
            {
                var pick = found.Where(t => t.Source == source && t.Turns.Count >= 20).OrderByDescending(t => t.Turns.Count).FirstOrDefault();
                if (pick is null) continue;
                File.WriteAllText(Path.Combine(dir, source + ".txt"), Edith.Core.Library.Librarian.Chunks(pick)[0]);
                Console.WriteLine($"{source}: {pick.Project} {pick.Turns.Count} turns -> {source}.txt");
            }

            return 0;
        }

        // --draw "prompt" FILE: the cloud painter, saved to a file.
        if (args.Contains("--draw"))
        {
            string[] rest = args.SkipWhile(a => a != "--draw").Skip(1).ToArray();
            (byte[]? image, string why) = await GigaChatClient.Shared.GenerateImageAsync(CloudSettings.Load(), rest[0], CancellationToken.None);
            if (image is null) { Console.WriteLine("failed: " + why); return 1; }
            File.WriteAllBytes(rest[1], image);
            Console.WriteLine($"saved {image.Length} bytes");
            return 0;
        }

        // --orb-bench: CPU cost of drawing one frame of the orb (the part EDITH pays every frame while it is visible).
        if (args.Contains("--orb-bench"))
        {
            double ms = 0;
            var thread = new Thread(() =>
            {
                var orb = new Edith.App.OrbControl();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var state in new[] { Edith.Core.AssistantState.Listening, Edith.Core.AssistantState.Thinking, Edith.Core.AssistantState.Speaking })
                {
                    for (int i = 0; i < 600; i++)
                    {
                        var visual = new System.Windows.Media.DrawingVisual();
                        using var dc = visual.RenderOpen();
                        orb.RenderAt(dc, i / 30.0, state, 0.1, i < 20 ? i / 30.0 : double.PositiveInfinity);
                    }
                }

                ms = sw.Elapsed.TotalMilliseconds / 1800;
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Console.WriteLine($"orb: {ms:F3} ms per frame (CPU)");
            return 0;
        }

        // --ui-shot DIR: the settings and models pages rendered offscreen to pictures (for a visual audit).
        if (args.Contains("--ui-shot"))
        {
            string dir = args.SkipWhile(a => a != "--ui-shot").Skip(1).First();
            Directory.CreateDirectory(dir);
            var thread = new Thread(() =>
            {
                void Shoot(string name, System.Windows.UIElement content, double width)
                {
                    var host = new System.Windows.Controls.Border { Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(17, 20, 28)), Child = content, Width = width };
                    Edith.App.Theme.Apply(host);
                    host.Measure(new System.Windows.Size(width, double.PositiveInfinity));
                    host.Arrange(new System.Windows.Rect(0, 0, width, host.DesiredSize.Height));
                    host.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)width, (int)Math.Ceiling(host.DesiredSize.Height), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(host);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using FileStream stream = File.Create(Path.Combine(dir, name));
                    encoder.Save(stream);
                    Console.WriteLine("saved " + name);
                }

                var settings = new Edith.App.SettingsWindow(() => { });
                var settingsContent = (System.Windows.UIElement)settings.Content;
                settings.Content = null;
                Shoot("settings.png", settingsContent, 620);
                var models = new Edith.App.ModelsWindow();
                var modelsContent = (System.Windows.UIElement)models.Content;
                models.Content = null;
                Shoot("models.png", new System.Windows.Controls.ScrollViewer { Content = modelsContent, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled }, 620);
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            return 0;
        }

        // --answer-shot DIR [query]: the real answer window with a really found picture, rendered to a PNG; prints the picture's rendered size.
        if (args.Contains("--answer-shot"))
        {
            string[] tail = args.SkipWhile(a => a != "--answer-shot").Skip(1).ToArray();
            string dir = tail[0];
            string query = tail.Length > 1 ? tail[1] : "Mount Everest";
            Directory.CreateDirectory(dir);
            (byte[]? bytes, string caption, string reason) = Edith.App.AnswerImages.FindAsync(query, CancellationToken.None).GetAwaiter().GetResult();
            if (bytes is null)
                (bytes, caption, reason) = Edith.App.AnswerImages.FindOnWebAsync(query, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(bytes is null ? "NO IMAGE: " + reason : $"found {bytes.Length} bytes, caption: {caption}");
            if (bytes is null)
                return 1;

            int code = 1;
            var thread = new Thread(() =>
            {
                var window = new Edith.App.AnswerWindow { Left = -10000, Top = -10000 };
                window.ShowAnswer("Покажи " + query, "Вот, сэр.");
                window.ShowImage(bytes, caption);
                window.Show();
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);

                var root = (System.Windows.FrameworkElement)window.Content;
                int w = (int)Math.Ceiling(root.ActualWidth), h = (int)Math.Ceiling(root.ActualHeight);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(root);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                string file = Path.Combine(dir, "answer-image.png");
                using (FileStream stream = File.Create(file))
                    encoder.Save(stream);

                // Count the pixels that are not the card's own dark background: a picture makes the card tall and colourful.
                var pixels = new byte[w * h * 4];
                bitmap.CopyPixels(pixels, w * 4, 0);
                int colourful = 0;
                for (int i = 0; i < pixels.Length; i += 4)
                    if (pixels[i + 3] > 200 && Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 40)
                        colourful++;
                Console.WriteLine($"card {w}x{h}, colourful pixels {colourful} ({colourful * 100.0 / (w * h):0.0}%), saved {file}");
                code = h > 200 && colourful > w * h / 20 ? 0 : 2;
                window.Close();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            Console.WriteLine(code == 0 ? "PASS: the picture is drawn in the answer window" : "FAIL: no picture in the rendered card");
            return code;
        }

        // --digest PATH / --pcscan: what the librarian reads on the machine (read-only), printed.
        if (args.Contains("--digest"))
        {
            Console.WriteLine(Edith.Core.Library.RepoDigest.Build(args.SkipWhile(a => a != "--digest").Skip(1).First()));
            return 0;
        }

        if (args.Contains("--pcscan"))
        {
            var folders = LibrarySettings.Load().ScanFolders;
            Console.WriteLine(Edith.Core.Library.PcScout.Render(Edith.Core.Library.PcScout.Scan(folders, DateTime.Now.AddDays(-30), 15)));
            return 0;
        }

        // --librarian [--dry] [--max N]: sessions of AI agents into the project chronicles of the vault.
        if (args.Contains("--librarian"))
        {
            bool dry = args.Contains("--dry");
            int max = args.SkipWhile(a => a != "--max").Skip(1).FirstOrDefault() is { } m && int.TryParse(m, out int n) ? n : 2;
            LibrarySettings cfg = LibrarySettings.Load();
            string provider = args.SkipWhile(a => a != "--provider").Skip(1).FirstOrDefault() ?? "gigachat";
            string localModel = args.SkipWhile(a => a != "--model").Skip(1).FirstOrDefault() ?? "qwen3:8b";
            var librarian = new Edith.Core.Library.Librarian(cfg.Vault, cfg.Projects, "C:/EDITH/data/library",
                LibraryModel.For(provider, localModel), commit: !dry);
            var sessions = Edith.Core.Library.SessionSources.Find(LibrarySettings.Load().Sources, TimeSpan.FromHours(3));
            foreach (string line in await librarian.RunAsync(sessions, max, CancellationToken.None, dry))
                Console.WriteLine(line);
            return 0;
        }

        // --build-phrases: records the fixed short phrases with the current voice (played from disk afterwards).
        if (args.Contains("--build-phrases"))
        {
            var tts = new Edith.Speech.HybridTtsService(Edith.Speech.TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json"));
            await tts.WarmUpAsync();
            int made = await tts.PrerenderPhrasesAsync();
            Console.WriteLine($"recorded {made} new phrases in {Edith.Speech.PhraseCache.Folder}");
            return 0;
        }

        // --orb-frames FILE.png: the orb in each state and at several moments of its activation, as one picture.
        if (args.Contains("--orb-frames"))
        {
            string file = args.SkipWhile(a => a != "--orb-frames").Skip(1).First();
            return RunOrbFrames(file);
        }

        // --speak-stream "phrase": the answer is spoken sentence by sentence with the real voice.
        if (args.Contains("--speak-stream"))
            return await RunSpeakStreamAsync(args.SkipWhile(a => a != "--speak-stream").Skip(1).First());

        // --browser-look URL: what the brain sees on a real page.
        if (args.Contains("--browser-look"))
        {
            string address = args.SkipWhile(a => a != "--browser-look").Skip(1).First();
            await using var look = new PlaywrightBrowser(headless: true, profilePath: Path.Combine(Path.GetTempPath(), "edith-look-" + Guid.NewGuid().ToString("N")));
            await look.OpenAsync(address, CancellationToken.None);
            Console.WriteLine(await look.LookAsync(CancellationToken.None));
            return 0;
        }

        // --notes "query": words-only versus words+meaning on the real vault.
        if (args.Contains("--notes"))
            return RunNotesCheck(args);

        var phrases = new List<(string, string)>();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--game" when i + 1 < args.Length:
                    Environment.SetEnvironmentVariable("EDITH_GAME_MODE", args[++i]);
                    break;

                case "--config" when i + 1 < args.Length:
                    Environment.SetEnvironmentVariable("EDITH_BRAIN_CONFIG", args[++i]);
                    break;

                case "--phrase" when i + 1 < args.Length:
                    phrases.Add((args[++i], "custom"));
                    break;
            }
        }

        if (phrases.Count == 0)
        {
            phrases.AddRange(Scenarios);
        }

        string endpoint = ReadEndpoint();
        Console.WriteLine($"game mode override: {Environment.GetEnvironmentVariable("EDITH_GAME_MODE") ?? "auto"}");
        Console.WriteLine($"endpoint: {endpoint}");
        Console.WriteLine();

        var engine = new ResponseEngine();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        int failures = 0;

        foreach ((string phrase, string kind) in phrases)
        {
            int linesBefore = CountLatencyLines();
            (long vramBefore, long ramBefore) = Snapshot();

            var sw = Stopwatch.StartNew();
            string answer = await engine.HandleAsync(phrase);
            sw.Stop();

            int newLines = CountLatencyLines() - linesBefore;
            bool usedLlm = newLines > 0;

            bool expectedLlm = kind.StartsWith("llm");
            bool routingOk = kind == "custom" || usedLlm == expectedLlm;

            if (!routingOk)
            {
                failures++;
            }

            Console.WriteLine($"[{kind}] \"{phrase}\"");
            Console.WriteLine($"  answer : {answer}");
            Console.WriteLine($"  time   : {sw.ElapsedMilliseconds} ms | llm steps: {newLines}" +
                              (routingOk ? "" : "  <-- UNEXPECTED ROUTING"));

            if (usedLlm)
            {
                foreach (string line in ReadLatencyTail(newLines))
                {
                    Console.WriteLine("  latency: " + line);
                }

                // Let a background unload (keep_alive=0) finish, then look at what is still resident.
                await Task.Delay(3500);
                (long vramAfter, long ramAfter) = Snapshot();
                string loaded = await LoadedModelsAsync(http, endpoint);
                Console.WriteLine($"  after 3.5s: ollama loaded=[{loaded}] " +
                                  $"VRAM {vramBefore}->{vramAfter} MiB, RAM used {ramBefore}->{ramAfter} MiB");
            }

            Console.WriteLine();
        }

        Console.WriteLine(failures == 0 ? "ROUTING OK" : $"ROUTING PROBLEMS: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static string ReadEndpoint()
    {
        try
        {
            string path =
                Environment.GetEnvironmentVariable("EDITH_BRAIN_CONFIG") is { Length: > 0 } p
                    ? p
                    : @"C:\EDITH\config\brain.json";

            return Edith.Core.Brain.BrainSettingsLoader.Parse(File.ReadAllText(path)).Endpoint;
        }
        catch
        {
            return "http://127.0.0.1:11434";
        }
    }

    private static int CountLatencyLines() =>
        File.Exists(Latency) ? File.ReadLines(Latency).Count() : 0;

    private static IEnumerable<string> ReadLatencyTail(int count) =>
        File.ReadLines(Latency).TakeLast(count);

    private static async Task<string> LoadedModelsAsync(HttpClient http, string endpoint)
    {
        try
        {
            using JsonDocument doc =
                JsonDocument.Parse(await http.GetStringAsync(endpoint.TrimEnd('/') + "/api/ps"));

            return string.Join(
                ", ",
                doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString()));
        }
        catch (Exception ex)
        {
            return "ps failed: " + ex.Message;
        }
    }

    private static (long VramMiB, long RamUsedMiB) Snapshot()
    {
        long vram = 0;

        try
        {
            using Process? p = Process.Start(
                new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                });

            if (p is not null)
            {
                long.TryParse(p.StandardOutput.ReadToEnd().Trim(), out vram);
                p.WaitForExit(3000);
            }
        }
        catch
        {
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        GlobalMemoryStatusEx(ref memory);

        return (vram, (long)((memory.TotalPhys - memory.AvailPhys) / 1024 / 1024));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

    private static async Task<int> RunEpisodesAsync()
    {
        var store = new Edith.Core.Memory.SqliteMemoryStore(ResponseEngine.DefaultMemoryPath);
        var summarizer = new Edith.Core.Memory.EpisodeSummarizer(
            store, BrainService.Shared.CompleteJsonAsync, clock: () => DateTimeOffset.Now.AddHours(3));
        int saved = await summarizer.RunOnceAsync(CancellationToken.None);
        Console.WriteLine($"episodes saved: {saved}");
        foreach (Edith.Core.Memory.Episode e in store.Episodes(DateTimeOffset.Now.AddDays(-7), DateTimeOffset.Now.AddDays(1), 50))
            Console.WriteLine($"{e.End:yyyy-MM-dd HH:mm} ({e.Turns}) {e.Summary}  [{e.Topics}]");
        return 0;
    }

    private static async Task<int> RunBrowserSmokeAsync()
    {
        const string page = "<html><head><title>Тест</title></head><body><h1>Магазин</h1>" +
            "<input placeholder='Поиск' id='q'><button onclick=\"document.getElementById('out').innerText='Найдено: шахматы'\">Найти</button>" +
            "<button onclick=\"document.getElementById('out').innerText='КУПЛЕНО'\">Купить сейчас</button>" +
            "<input id='s' placeholder='Найти статью'><button class='search-btn' onclick=\"document.getElementById('out').innerText='РЕЗУЛЬТАТЫ ПОИСКА'\"><svg width='10' height='10'></svg></button>" +
            "<input type='password' placeholder='Пароль'><a href='#x'>О магазине</a><p id='out'></p></body></html>";
        string url = "data:text/html;charset=utf-8," + Uri.EscapeDataString(page);
        string profile = Path.Combine(Path.GetTempPath(), "edith-browser-smoke-" + Guid.NewGuid().ToString("N"));
        await using var browser = new PlaywrightBrowser(headless: true, profilePath: profile);
        var ct = CancellationToken.None;

        await browser.OpenAsync(url, ct);
        Console.WriteLine(await browser.LookAsync(ct));
        Console.WriteLine("--- type: " + (await browser.TypeAsync("Поиск", "шахматы", false, ct)).Split('\n')[0]);
        Console.WriteLine("--- password: " + (await browser.TypeAsync("Пароль", "secret", false, ct)).Split('\n')[0]);
        string clicked = await browser.ClickAsync("Найти", ct);
        Console.WriteLine("--- click: " + clicked.Split('\n')[0] + " | " + (clicked.Contains("Найдено: шахматы") ? "RESULT SHOWN" : "NO RESULT"));
        string searched = await browser.TypeAsync("Найти статью", "плейрайт", true, ct);
        Console.WriteLine("--- search (Enter ignored by the page): " + (searched.Contains("РЕЗУЛЬТАТЫ ПОИСКА") ? "SEARCH BUTTON PRESSED" : "NOT PRESSED"));
        Console.WriteLine(string.Join(" | ", searched.Split(Environment.NewLine).Where(l => l.StartsWith("[")).Take(8)));
        Console.WriteLine("--- label of 'Купить': " + await browser.LabelOfAsync("Купить", ct));
        Console.WriteLine("--- by number: " + (await browser.ClickAsync("4", ct)).Split('\n')[0]);
        return 0;
    }

    private static int RunNotesCheck(string[] args)
    {
        var settings = VaultNotes.Settings();
        string db = Path.Combine(Path.GetTempPath(), "edith-notes-check.db");
        var index = new Edith.Core.Memory.NotesIndex(db);
        Console.WriteLine($"indexed notes: {index.Refresh(settings.Path, settings.Folders, settings.ExtraFiles)}");

        var clock = System.Diagnostics.Stopwatch.StartNew();
        using Edith.Audio.Embedding.E5Embedder? embedder = Edith.Audio.Embedding.E5Embedder.TryCreate(@"C:\EDITH\models\embedding");
        Console.WriteLine($"model loaded: {embedder is not null} ({clock.ElapsedMilliseconds} ms)");
        if (embedder is null)
            return 1;

        clock.Restart();
        int left;
        do { left = index.EmbedPending(embedder, 60); } while (left > 0);
        Console.WriteLine($"vectors computed in {clock.ElapsedMilliseconds} ms");

        foreach (string query in args.SkipWhile(a => a != "--notes").Skip(1))
        {
            Edith.Core.Memory.EmbeddingService.Provider = () => null;
            var words = index.Search(query, 3);
            Edith.Core.Memory.EmbeddingService.Provider = () => embedder;
            clock.Restart();
            var both = index.Search(query, 3);
            long ms = clock.ElapsedMilliseconds;
            Console.WriteLine($"\n=== {query}");
            Console.WriteLine("words  : " + string.Join(" | ", words.Select(c => c.Title + " > " + c.Heading)));
            Console.WriteLine($"meaning: ({ms} ms) " + string.Join(" | ", both.Select(c => c.Title + " > " + c.Heading)));
        }

        return 0;
    }

    private static int RunOrbFrames(string file)
    {
        int exit = 1;
        var thread = new Thread(() =>
        {
            var cells = new (string Label, Edith.Core.AssistantState State, double Level, double BurstAgo, double T)[]
            {
                ("wake 0.06", Edith.Core.AssistantState.Listening, 0.05, 0.06, 1.0),
                ("wake 0.18", Edith.Core.AssistantState.Listening, 0.05, 0.18, 1.1),
                ("wake 0.35", Edith.Core.AssistantState.Listening, 0.05, 0.35, 1.2),
                ("wake 0.55", Edith.Core.AssistantState.Listening, 0.05, 0.55, 1.4),
                ("listening quiet", Edith.Core.AssistantState.Listening, 0.0, double.PositiveInfinity, 3.0),
                ("listening loud", Edith.Core.AssistantState.Listening, 0.25, double.PositiveInfinity, 3.3),
                ("thinking", Edith.Core.AssistantState.Thinking, 0.0, double.PositiveInfinity, 4.0),
                ("speaking", Edith.Core.AssistantState.Speaking, 0.0, double.PositiveInfinity, 5.2),
            };

            const int columns = 4;
            int rows = (cells.Length + columns - 1) / columns;
            double cell = Edith.App.OrbControl.Size;
            var visual = new System.Windows.Media.DrawingVisual();
            using (System.Windows.Media.DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(24, 27, 34)), null,
                    new System.Windows.Rect(0, 0, columns * cell, rows * cell));
                for (int i = 0; i < cells.Length; i++)
                {
                    var orb = new Edith.App.OrbControl();
                    dc.PushTransform(new System.Windows.Media.TranslateTransform(i % columns * cell, i / columns * cell));
                    // Warm the colour easing: a few frames before the one that is kept.
                    for (double t = cells[i].T - 0.6; t < cells[i].T; t += 0.05)
                        orb.RenderAt(new System.Windows.Media.DrawingVisual().RenderOpen(), t, cells[i].State, cells[i].Level, cells[i].BurstAgo);
                    orb.RenderAt(dc, cells[i].T, cells[i].State, cells[i].Level, cells[i].BurstAgo);
                    dc.Pop();
                }
            }

            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)(columns * cell), (int)(rows * cell), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using FileStream stream = File.Create(file);
            encoder.Save(stream);
            Console.WriteLine("saved " + file);
            exit = 0;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return exit;
    }

    private static async Task<int> RunSpeakStreamAsync(string phrase)
    {
        var tts = new Edith.Speech.HybridTtsService(Edith.Speech.TtsSettingsLoader.Load(@"C:\EDITH\config\tts.json"));
        await tts.WarmUpAsync();
        var engine = new ResponseEngine();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long firstSentence = -1, firstAudio = -1;
        tts.FirstAudioStarted += () => firstAudio = clock.ElapsedMilliseconds;
        var speech = new StreamingSpeech(
            tts, CancellationToken.None, 1,
            onStart: text => { firstSentence = clock.ElapsedMilliseconds; Console.WriteLine($"first sentence at {firstSentence} ms: {text}"); },
            onSentence: _ => { });
        engine.StreamTarget = speech;

        string answer = await engine.HandleAsync(phrase, CancellationToken.None);
        long answered = clock.ElapsedMilliseconds;
        await speech.FinishAsync();
        Console.WriteLine($"started={speech.Started} first audio at {firstAudio} ms, full answer ready at {answered} ms, all spoken at {clock.ElapsedMilliseconds} ms");
        Console.WriteLine("answer: " + answer);
        Console.WriteLine("spoken: " + speech.Spoken);
        return 0;
    }
}
