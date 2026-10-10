using System.IO;
using System.Text.Json;
using Edith.Core.Library;
using Edith.Core.Resources;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>
/// Runs the librarian all the time, politely: every minute and a half it checks that EDITH is idle, no game runs and
/// (for a local model) the video card and the machine are free, and then works on one session. Before every model call
/// it asks again, so a game or a question to EDITH stops it at once; progress is kept and it carries on later.
/// Off until the owner flips the switch (<see cref="LibrarianSwitch"/>).
/// </summary>
public sealed class LibrarianService : IDisposable
{
    public const string ConfigPath = @"C:\EDITH\config\library.json";

    private readonly System.Threading.Timer _timer;
    private readonly Func<bool> _voiceIdle;
    private readonly Func<ResourcePlan?> _resources;
    private readonly IEventBus _bus;
    private int _running;
    private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

    public LibrarianService(IEventBus bus, Func<bool> voiceIdle, Func<ResourcePlan?> resources)
    {
        _bus = bus;
        _voiceIdle = voiceIdle;
        _resources = resources;
        _timer = new System.Threading.Timer(_ => _ = Task.Run(RunOnceAsync), null, TimeSpan.FromMinutes(2), TimeSpan.FromSeconds(90));
    }

    private async Task RunOnceAsync()
    {
        if (!LibrarianSwitch.On || Interlocked.Exchange(ref _running, 1) == 1)
            return;

        try
        {
            LibrarySettings settings = LibrarySettings.Load();
            string vault = settings.Vault, provider = settings.Provider, model = settings.LocalModel;
            double idle = settings.IdleHours;
            int gpuLayers = settings.GpuLayers;
            var projects = settings.Projects;
            string[] scanFolders = settings.ScanFolders.ToArray();

            bool local = provider.Equals("local", StringComparison.OrdinalIgnoreCase);
            bool MayWork()
            {
                if (!LibrarianSwitch.On)
                {
                    LibraryStatus.Waiting("");
                    return false;
                }

                if (!_voiceIdle())
                {
                    LibraryStatus.Waiting("EDITH занята разговором");
                    return false;
                }

                ResourcePlan? plan = _resources();
                if (plan is null)
                    return false;

                // "A game" is also a launcher sitting in memory: what counts is whether the machine is really loaded.
                int util = plan.Gpu?.UtilizationPercent ?? 0;
                if (plan.GameActive && util > 20)
                {
                    LibraryStatus.Waiting("идёт игра");
                    return false;
                }

                // A local model needs the card: enough free video memory and no heavy foreign load.
                if (local && plan.Gpu is { } gpu && !(util < 55 && gpu.MemoryTotalMb - gpu.MemoryUsedMb >= 3500))
                {
                    LibraryStatus.Waiting("видеокарта занята");
                    return false;
                }

                return true;
            }

            if (!MayWork())
                return;

            var librarian = new Librarian(vault, projects, @"C:\EDITH\data\library", LibraryModel.For(provider, model, gpuLayers));
            librarian.Progress = LibraryStatus.Progress;
            IReadOnlyList<string> written = await librarian.RunAsync(SessionSources.Find(settings.Sources, TimeSpan.FromHours(idle), 12, librarian.IsDone), 1, CancellationToken.None, mayWork: MayWork);
            foreach (string note in written)
                _bus.Publish(EdithEvent.Create("library.entry", "Записано в архив: " + note));

            if (written.Count == 0)
                LibraryStatus.Idle();

            // When the history is caught up (or every few hours), the quiet round: pictures with fresh repository snapshots, PC finds.
            if (written.Count == 0 && DateTimeOffset.Now - _lastRefresh > TimeSpan.FromHours(6) && MayWork())
            {
                _lastRefresh = DateTimeOffset.Now;
                await librarian.RefreshAsync(scanFolders, CancellationToken.None, MayWork);
                _bus.Publish(EdithEvent.Create("library.entry", "Картины проектов и находки на ПК обновлены"));
            }
        }
        catch (Exception ex)
        {
            _bus.Publish(EdithEvent.Create(EventKinds.Error, "Библиотекарь: " + ex.Message));
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
