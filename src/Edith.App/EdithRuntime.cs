using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Edith.Core.Apps;
using Edith.Core.Browser;
using Edith.Core.Documents;
using Edith.Core.Power;
using Edith.Core.Reminders;
using Edith.Core.Resources;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>
/// The "one system" core of the running app: event bus, action journal, resource and health
/// monitors, deferred-task queue. Created once in App.OnStartup; the voice pipeline, tools and
/// permissions publish into <see cref="Bus"/>, the status panel and the journal listen to it.
/// </summary>
public sealed class EdithRuntime : IDisposable
{
    public const string JournalPath = @"C:\EDITH\data\journal\edith-journal.db";
    public const string DeferredTasksPath = @"C:\EDITH\data\journal\deferred-tasks.json";
    public const string ShutdownStatePath = @"C:\EDITH\data\journal\shutdown.json";
    public const string SignalsDirectory = @"C:\EDITH\data\signals";
    public const string AppsConfigPath = @"C:\EDITH\config\apps.json";
    public const string RemindersPath = @"C:\EDITH\data\journal\reminders.json";
    public const string FileOpsPath = @"C:\EDITH\data\journal\file-ops.json";

    private static readonly TimeSpan ShortcutRescanInterval = TimeSpan.FromMinutes(10);
    private readonly object _shortcutGate = new();
    private ShortcutIndex? _shortcuts;
    private DateTimeOffset _shortcutsScannedAt;

    private static readonly TimeSpan PowerPollInterval = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan ResourcePollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromSeconds(20);

    private readonly Timer _resourceTimer;
    private readonly Timer _healthTimer;
    private readonly Timer _powerTimer;
    private int _healthRunning;
    private bool _disposed;

    private EdithRuntime(EventBus bus, ActionJournal? journal, IEnumerable<HealthCheck> healthChecks, Func<bool> gameActive)
    {
        Bus = bus;
        Journal = journal;
        Deferred = new DeferredTaskQueue(DeferredTasksPath, bus);
        Resources = new ResourceMonitor(
            gameActive,
            SystemProbes.QueryGpu,
            bus,
            edithVramMb: SystemProbes.EdithVramMb);
        Health = new HealthMonitor(healthChecks, bus);

        // Game or someone else on the GPU: give the card back right away (the model otherwise
        // stays loaded for hours to avoid 15-18 s cold loads).
        Resources.Changed += plan =>
        {
            if (plan.Brain == BrainPlacement.Cpu)
                _ = BrainService.Shared.YieldGpuAsync(plan.Reason);
        };
        Reminders = new ReminderScheduler(RemindersPath, bus);
        Files = new Edith.Core.Files.FileOps(
            Edith.Core.Files.FileSafety.ForCurrentUser(), new WindowsRecycleBin(), FileOpsPath, bus);
        Sorter = Edith.Core.Files.DownloadsSorter.ForCurrentUser();
        BrainToolRouter.Files = Files;
        Screen = new WindowsScreen();
        BrainToolRouter.Screen = Screen;
        Documents = new DocumentAssistant(DocumentFinder.DefaultDirectories, bus);
        Browser = new BrowserAssistant(_playwright, bus);
        Apps = new AppActions(AppCatalog.Load(AppsConfigPath), Shortcuts, new WindowsAppControl(), bus);
        Shutdown = new ShutdownScheduler(
            new WindowsPowerControl(),
            new AgentMarkerFiles(SignalsDirectory),
            bus: bus,
            statePath: ShutdownStatePath);

        _resourceTimer = new Timer(_ => PollResources(), null, TimeSpan.FromSeconds(2), ResourcePollInterval);
        _healthTimer = new Timer(_ => _ = RunHealthAsync(), null, TimeSpan.FromSeconds(8), HealthPollInterval);
        _powerTimer = new Timer(_ => TickShutdown(), null, PowerPollInterval, PowerPollInterval);
    }

    public EventBus Bus { get; }

    /// <summary>Null only when the journal database could not be opened; EDITH still runs.</summary>
    public ActionJournal? Journal { get; }

    public ResourceMonitor Resources { get; }

    public HealthMonitor Health { get; }

    public DeferredTaskQueue Deferred { get; }

    public ShutdownScheduler Shutdown { get; }

    public AppActions Apps { get; }

    public ReminderScheduler Reminders { get; }

    public Edith.Core.Files.FileOps Files { get; }

    public Edith.Core.Files.DownloadsSorter Sorter { get; }

    public Edith.Core.Screen.IScreen Screen { get; }

    public DocumentAssistant Documents { get; }

    public BrowserAssistant Browser { get; }

    /// <summary>EDITH's own browser, also driven step by step by the brain (browser_* tools).</summary>
    public PlaywrightBrowser Playwright => _playwright;

    private readonly PlaywrightBrowser _playwright = new();

    /// <summary>Start-menu/desktop shortcuts, rescanned at most every 10 minutes (new installs appear).</summary>
    public ShortcutIndex Shortcuts()
    {
        lock (_shortcutGate)
        {
            if (_shortcuts is null || DateTimeOffset.Now - _shortcutsScannedAt > ShortcutRescanInterval)
            {
                _shortcuts = ShortcutIndex.Scan(ShortcutIndex.DefaultDirectories());
                _shortcutsScannedAt = DateTimeOffset.Now;
            }

            return _shortcuts;
        }
    }

    public static EdithRuntime Start(IEnumerable<HealthCheck> healthChecks, Func<bool> gameActive)
    {
        var bus = new EventBus(recentCapacity: 1000);
        ActionJournal? journal = null;

        try
        {
            journal = new ActionJournal(JournalPath);
            _ = Task.Run(() => { try { journal.Prune(TimeSpan.FromDays(30)); } catch { } });
            journal.Attach(bus);
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("journal-error.txt", ex.ToString());
        }

        bus.SubscriberFailed += (evt, ex) =>
            VoiceDiagnostics.WriteSnapshot("event-subscriber-error.txt", $"kind={evt.Kind}", ex.ToString());

        EdithEvents.Install(bus);
        var runtime = new EdithRuntime(bus, journal, healthChecks, gameActive);

        bus.Publish(EdithEvent.Create(
            EventKinds.AppStarted,
            "EDITH запущена",
            data: new Dictionary<string, string>
            {
                ["journal"] = journal is null ? "unavailable" : JournalPath,
                ["deferred"] = runtime.Deferred.Pending.Count.ToString(),
            }));

        return runtime;
    }

    /// <summary>Recent events for "что ты делала?": today's journal, or the in-memory bus as a fallback.</summary>
    public IReadOnlyList<EdithEvent> RecentActivity()
    {
        if (Journal is not null)
        {
            try
            {
                return Journal.Since(DateTimeOffset.Now.Date, 1000);
            }
            catch
            {
            }
        }

        return Bus.Recent(1000);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _resourceTimer.Dispose();
        _healthTimer.Dispose();
        _powerTimer.Dispose();
        Bus.Publish(EdithEvent.Create(EventKinds.AppStopping, "EDITH завершает работу"));

        try
        {
            Journal?.FlushAsync().Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        Journal?.Dispose();

        try
        {
            _playwright.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
        }
    }

    private void TickShutdown()
    {
        try
        {
            Shutdown.Tick();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("shutdown-scheduler-error.txt", ex.ToString());
        }
    }

    private void PollResources()
    {
        try
        {
            Resources.Poll();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("resource-monitor-error.txt", ex.ToString());
        }
    }

    private async Task RunHealthAsync()
    {
        if (Interlocked.Exchange(ref _healthRunning, 1) == 1)
            return;

        try
        {
            await Health.RunOnceAsync();
        }
        catch (Exception ex)
        {
            VoiceDiagnostics.WriteRaw("health-monitor-error.txt", ex.ToString());
        }
        finally
        {
            Volatile.Write(ref _healthRunning, 0);
        }
    }
}

/// <summary>Read-only system probes. None of them load a model or touch the GPU workload.</summary>
public static class SystemProbes
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>One nvidia-smi query; null when there is no NVIDIA driver or it does not answer.</summary>
    public static GpuSnapshot? QueryGpu()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = NvidiaSmiParser.QueryArguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null)
                return null;

            Task<string> output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            return NvidiaSmiParser.Parse(output.Result);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GET /api/version: answers whether the Ollama server is up. It never loads a model,
    /// unlike /api/generate or /api/chat.
    /// </summary>
    public static async Task<HealthResult> ProbeOllamaAsync(string endpoint, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(
                endpoint.TrimEnd('/') + "/api/version",
                cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthResult.Ok()
                : HealthResult.Failed($"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthResult.Failed("Ollama не отвечает");
        }
    }

    /// <summary>
    /// VRAM held by EDITH's own models (brain.json), from Ollama /api/ps. Other models loaded
    /// in Ollama are not EDITH's and stay counted as foreign load. 0 when Ollama is silent.
    /// </summary>
    public static int EdithVramMb()
    {
        try
        {
            string path = Environment.GetEnvironmentVariable("EDITH_BRAIN_CONFIG") is { Length: > 0 } custom
                ? custom
                : @"C:\EDITH\config\brain.json";
            Edith.Core.Brain.BrainSettings settings =
                Edith.Core.Brain.BrainSettingsLoader.Parse(File.Exists(path) ? File.ReadAllText(path) : null);
            if (!settings.Enabled)
                return 0;

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            string json = Http.GetStringAsync(settings.Endpoint.TrimEnd('/') + "/api/ps", timeout.Token)
                .GetAwaiter().GetResult();

            return OllamaPs.EdithVramMb(
                OllamaPs.Parse(json),
                [settings.Model, settings.Normal.Model, settings.Gaming.Mode.Model]);
        }
        catch
        {
            return 0;
        }
    }

    public static HealthResult ProbeDisk(string path, long minFreeBytes = 5L * 1024 * 1024 * 1024)
    {
        var drive = new DriveInfo(Path.GetPathRoot(path) ?? "C:\\");
        long free = drive.AvailableFreeSpace;
        string detail = $"{free / 1024 / 1024 / 1024} ГБ свободно";

        return free >= minFreeBytes ? HealthResult.Ok(detail) : HealthResult.Degraded(detail);
    }

    public static HealthResult ProbeFiles(params string[] paths)
    {
        string[] missing = paths.Where(path => !File.Exists(path) && !Directory.Exists(path)).ToArray();

        return missing.Length == 0
            ? HealthResult.Ok()
            : HealthResult.Failed("нет: " + string.Join(", ", missing.Select(Path.GetFileName)));
    }
}
