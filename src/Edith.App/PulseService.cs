using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Edith.Core.Activity;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>config/pulse.json: what the background pulse may look at and where it may speak.</summary>
public sealed record PulseSettings
{
    public bool Enabled { get; init; } = true;

    /// <summary>Repositories read (read-only git) for the project picture.</summary>
    public string[] Repos { get; init; } = [];

    /// <summary>A silent tray balloon when new cards appear (never voice, never during a game).</summary>
    public bool Notify { get; init; } = true;

    /// <summary>The short dossier line goes into the brain's context (and so to the cloud with each question). Off until the owner agrees.</summary>
    public bool DossierToPrompt { get; init; }

    public const string DefaultPath = @"C:\EDITH\config\pulse.json";

    public static PulseSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path))
                return new PulseSettings();

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            JsonElement root = document.RootElement;
            return new PulseSettings
            {
                Enabled = !root.TryGetProperty("enabled", out JsonElement e) || e.ValueKind != JsonValueKind.False,
                Notify = !root.TryGetProperty("notify", out JsonElement n) || n.ValueKind != JsonValueKind.False,
                DossierToPrompt = root.TryGetProperty("dossierToPrompt", out JsonElement d) && d.ValueKind == JsonValueKind.True,
                Repos = root.TryGetProperty("repos", out JsonElement r) && r.ValueKind == JsonValueKind.Array
                    ? r.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToArray()
                    : [],
            };
        }
        catch
        {
            return new PulseSettings();
        }
    }
}

/// <summary>
/// The background "pulse": counts which program the owner is working in (names only, every 30 s while
/// they are at the keyboard) and, every half hour, reads the state of their repositories, then lets
/// <see cref="PulseBuilder"/> decide what is worth a card. Everything stays on this PC. Cards wait in the
/// status window and are told when asked ("что нового?"); EDITH never starts talking about them.
/// </summary>
public sealed class PulseService : IDisposable
{
    public static PulseService? Instance { get; private set; }

    private static readonly TimeSpan SampleEvery = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(2);

    private readonly ActivityStore _store;
    private readonly PulseSettings _settings;
    private readonly IEventBus _bus;
    private readonly Action<string, string>? _notify;
    private readonly System.Threading.Timer _sampler;
    private readonly System.Threading.Timer _pulse;
    private int _pulsing;
    private volatile string _dossier = "";
    private volatile IReadOnlyList<RepoState> _repos = [];

    public PulseService(ActivityStore store, PulseSettings settings, IEventBus bus, Action<string, string>? notify = null)
    {
        _store = store;
        _settings = settings;
        _bus = bus;
        _notify = notify;
        Instance = this;

        _sampler = new System.Threading.Timer(_ => Sample(), null, SampleEvery, SampleEvery);
        _pulse = new System.Threading.Timer(_ => _ = Task.Run(RunOnce), null, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(30));
    }

    public ActivityStore Store => _store;

    /// <summary>The dossier line for the brain, or empty when the owner has not agreed to send it.</summary>
    public string DossierForPrompt => _settings.DossierToPrompt ? _dossier : "";

    private void Sample()
    {
        try
        {
            if (IdleFor() > IdleLimit)
                return;

            string? process = ForegroundProcessName();
            if (process is null || PulseBuilder.FriendlyApp(process) is null)
                return;

            _store.AddUsage(DateTimeOffset.Now, process, (int)SampleEvery.TotalSeconds);
        }
        catch
        {
            // Counting time must never disturb anything.
        }
    }

    /// <summary>One pass: read the repositories, build the picture, save the cards that are new.</summary>
    public int RunOnce()
    {
        if (Interlocked.Exchange(ref _pulsing, 1) == 1)
            return 0;

        try
        {
            DateTimeOffset now = DateTimeOffset.Now;
            var repos = new List<RepoState>();
            foreach (string path in _settings.Repos)
                if (GitActivity.Read(path) is { } state)
                    repos.Add(state);

            IReadOnlyList<UsageRow> usage = _store.Usage(now.AddDays(-PulseBuilder.ActiveWithinDays), now);
            PulseResult result = PulseBuilder.Build(repos, usage, now);
            _dossier = result.Dossier;
            _repos = repos;

            var added = new List<PulseCard>();
            foreach (PulseCard card in result.Cards)
                if (_store.AddCard(card))
                    added.Add(card);

            foreach (PulseCard card in added)
                _bus.Publish(EdithEvent.Create("pulse.card", card.Title, data: new Dictionary<string, string> { ["body"] = card.Body }));

            if (added.Count > 0 && _settings.Notify && _notify is not null)
                _notify("E.D.I.T.H.", added.Count == 1 ? added[0].Title : $"Новых заметок: {added.Count}");

            return added.Count;
        }
        catch (Exception ex)
        {
            _bus.Publish(EdithEvent.Create(EventKinds.Error, "Фоновая сводка не удалась: " + ex.Message));
            return 0;
        }
        finally
        {
            Volatile.Write(ref _pulsing, 0);
        }
    }

    /// <summary>The first greeting of the day gets the brief; later ones get null.</summary>
    public string? TakeBrief(bool force = false)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string today = now.ToString("yyyy-MM-dd");
        if (!force && _store.GetMeta("brief-date") == today)
            return null;

        _store.SetMeta("brief-date", today);
        int fresh = _store.Cards().Count(c => c.State == ActivityStore.CardNew);
        string brief = PulseBuilder.Brief(_repos, _store.Usage(now.AddDays(-2), now), now, fresh);
        return brief.Length == 0 ? null : brief;
    }

    /// <summary>Everything new, as one spoken answer; the cards become "seen". Null when there is nothing.</summary>
    public string? Telling(int max = 3)
    {
        IReadOnlyList<PulseCard> fresh = _store.Cards().Where(c => c.State == ActivityStore.CardNew).Take(max).ToList();
        if (fresh.Count == 0)
            return null;

        foreach (PulseCard card in fresh)
            _store.SetCardState(card.Id, ActivityStore.CardSeen);

        return string.Join(" ", fresh.Select(c => c.Body));
    }

    public static bool IsNewsQuery(string command)
    {
        string text = command.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('?', '.', '!');
        return text is "что нового" or "есть новости" or "что новенького" or "что ты заметила" or "что ты заметила нового"
            or "какие новости" or "сводка" or "дай сводку" or "утренняя сводка" or "что у меня по проектам" or "что по проектам" or "над чем я работал" or "что у меня нового";
    }

    public void Dispose()
    {
        _sampler.Dispose();
        _pulse.Dispose();
        if (ReferenceEquals(Instance, this))
            Instance = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    private static TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        return GetLastInputInfo(ref info)
            ? TimeSpan.FromMilliseconds((uint)Environment.TickCount - info.Time)
            : TimeSpan.Zero;
    }

    private static string? ForegroundProcessName()
    {
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero || GetWindowThreadProcessId(window, out uint pid) == 0)
            return null;

        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return null;
        }
    }
}
