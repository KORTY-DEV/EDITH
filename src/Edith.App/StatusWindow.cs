using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Edith.Core.Resources;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>
/// Status panel (Ctrl+Alt+J): what EDITH is doing, where the brain runs, what is broken and
/// the live event feed. Pure projection of the event bus; closing only hides it.
/// </summary>
public sealed class StatusWindow : Window
{
    private const int MaxFeedItems = 300;

    private static readonly Brush Background0 = Frozen(Color.FromRgb(17, 20, 26));
    private static readonly Brush Foreground0 = Frozen(Color.FromRgb(220, 228, 238));
    private static readonly Brush Muted = Frozen(Color.FromRgb(130, 142, 158));
    private static readonly Brush Accent = Frozen(Color.FromRgb(85, 223, 255));
    private static readonly Brush Good = Frozen(Color.FromRgb(110, 210, 140));
    private static readonly Brush Warn = Frozen(Color.FromRgb(255, 193, 92));
    private static readonly Brush Bad = Frozen(Color.FromRgb(255, 95, 105));

    private static readonly Brush Panel = Frozen(Color.FromRgb(23, 27, 36));
    private static readonly Brush PanelBorder = Frozen(Color.FromRgb(38, 45, 58));
    private static readonly Brush TabOn = Frozen(Color.FromRgb(36, 48, 66));

    private readonly EdithRuntime _runtime;
    private readonly Edith.Core.Memory.SqliteMemoryStore? _memory;
    private readonly IDisposable _subscription;
    private readonly TextBlock _state = Text("…", 14, Accent, FontWeights.SemiBold);
    private readonly Ellipse _dot = new() { Width = 12, Height = 12, Fill = Accent, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock _resources = Text("…", 12, Muted);
    private readonly TextBlock _deferred = Text("", 12, Muted);
    private readonly StackPanel _health = new();
    private readonly StackPanel _cards = new();
    private readonly StackPanel _services = new();
    private readonly StackPanel _todo = new();
    private Border? _todoCard;
    private readonly System.Windows.Threading.DispatcherTimer _servicesTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly StackPanel _facts = new();
    private readonly StackPanel _talks = new();
    private readonly ListBox _feed = new();
    private readonly List<(Border Tab, UIElement Page)> _tabs = [];
    private bool _closing;

    /// <summary>Lets other windows ask the main window to open a tab ("Модели", "Настройки"...).</summary>
    public static Action<string>? OpenTabRequested { get; set; }

    private readonly Dictionary<Border, Action<Border>> _firstShow = [];
    private readonly Dictionary<Border, string> _names = [];

    public StatusWindow(EdithRuntime runtime, Edith.Core.Memory.SqliteMemoryStore? memory = null, Action? restart = null)
    {
        OpenTabRequested = Open;
        _runtime = runtime;
        _memory = memory;

        Title = "E.D.I.T.H.";
        Theme.Apply(this);
        Width = 640;
        Height = 760;
        MinWidth = 420;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Background0;
        Foreground = Foreground0;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowInTaskbar = true;

        _feed.Background = Brushes.Transparent;
        _feed.Foreground = Foreground0;
        _feed.BorderThickness = new Thickness(0);
        _feed.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _feed.FontSize = 12;
        ScrollViewer.SetHorizontalScrollBarVisibility(_feed, ScrollBarVisibility.Disabled);

        // Header: a small orb in the colour of the state, the name, what she is doing now.
        var header = new DockPanel { Margin = new Thickness(22, 18, 22, 6), LastChildFill = true };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(_dot);
        title.Children.Add(Text("E.D.I.T.H.", 20, Foreground0, FontWeights.SemiBold));
        var stateBox = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        stateBox.Children.Add(_state);
        DockPanel.SetDock(stateBox, Dock.Right);
        header.Children.Add(stateBox);
        header.Children.Add(title);

        var sub = new StackPanel { Margin = new Thickness(22, 0, 22, 10) };
        sub.Children.Add(_resources);
        sub.Children.Add(_deferred);

        // Pages.
        _todoCard = Card("Что нужно настроить", _todo);
        var now = Page(_todoCard, Card("Службы", _services), Card("Системы", _health));
        var notes = Page(Card("Что я заметила", _cards));
        var memoryPage = Page(Card("Что я о вас знаю", _facts), Card("Недавние разговоры", _talks));
        var events = new Border { Padding = new Thickness(14, 4, 14, 14), Child = _feed };

        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(22, 0, 22, 10) };
        var body = new Grid();
        void AddTab(string name, UIElement page)
        {
            var label = Text(name, 13, Muted, FontWeights.SemiBold);
            label.TextWrapping = TextWrapping.NoWrap;
            var tab = new Border
            {
                Child = label,
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(16),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
            };
            _tabs.Add((tab, page));
            page.Visibility = _tabs.Count == 1 ? Visibility.Visible : Visibility.Collapsed;
            body.Children.Add(page);
            tab.MouseLeftButtonUp += (_, _) => SelectTab(tab);
            tabBar.Children.Add(tab);
        }

        AddTab("Сейчас", now);
        AddTab("Заметки", notes);
        AddTab("Память", memoryPage);
        AddTab("События", events);

        // One interface: models and settings live here too, built the first time their tab is opened.
        void AddLazyTab(string name, Action<Border> build)
        {
            var host = new Border();
            AddTab(name, host);
            _firstShow[_tabs[^1].Tab] = build;
        }

        AddLazyTab("Модели", host =>
        {
            var models = new ModelsWindow();
            host.Child = (UIElement)models.Content;
            models.Content = null;
            _ = models.InitAsync();
        });
        AddLazyTab("Настройки", host =>
        {
            var settings = new SettingsWindow(restart ?? (() => { }));
            UIElement content = (UIElement)settings.Content;
            settings.Content = null;
            host.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Margin = new Thickness(4, 0, 4, 12), Child = content } };
        });
        foreach ((Border tab, _) in _tabs)
            _names[tab] = ((TextBlock)tab.Child).Text;
        SelectTab(_tabs[0].Tab);

        var root = new DockPanel
        {
            Background = new LinearGradientBrush(Color.FromRgb(17, 20, 28), Color.FromRgb(12, 14, 19), 90),
        };
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(sub);
        top.Children.Add(tabBar);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        root.Children.Add(body);
        Content = root;

        foreach (EdithEvent evt in runtime.Bus.Recent(MaxFeedItems))
            Apply(evt, refreshPanels: false);

        RefreshPanels();
        _subscription = runtime.Bus.Subscribe(evt => Dispatcher.BeginInvoke(() => Apply(evt, refreshPanels: true)));
        _servicesTimer.Tick += (_, _) => RefreshServices();
        LibraryStatus.Changed += () => Dispatcher.BeginInvoke(RefreshServices);
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                RefreshPanels();
                _servicesTimer.Start();
            }
            else
            {
                _servicesTimer.Stop();
            }
        };
    }

    /// <summary>Shows the window on the named tab.</summary>
    public void Open(string name)
    {
        Show();
        Activate();
        if (_names.FirstOrDefault(p => p.Value == name).Key is { } tab)
            SelectTab(tab);
    }

    private void SelectTab(Border selected)
    {
        if (_firstShow.Remove(selected, out Action<Border>? build))
            build((Border)_tabs.First(t => ReferenceEquals(t.Tab, selected)).Page);

        foreach ((Border tab, UIElement page) in _tabs)
        {
            bool on = ReferenceEquals(tab, selected);
            tab.Background = on ? TabOn : Brushes.Transparent;
            ((TextBlock)tab.Child).Foreground = on ? Foreground0 : Muted;
            page.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static ScrollViewer Page(params UIElement[] cards)
    {
        var stack = new StackPanel { Margin = new Thickness(22, 0, 22, 18) };
        foreach (UIElement card in cards)
            stack.Children.Add(card);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = stack };
    }

    private static Border Card(string title, UIElement content)
    {
        var inside = new StackPanel();
        TextBlock heading = Text(title.ToUpperInvariant(), 11, Muted, FontWeights.SemiBold);
        heading.Margin = new Thickness(0, 0, 0, 8);
        inside.Children.Add(heading);
        inside.Children.Add(content);
        return new Border
        {
            Background = Panel,
            BorderBrush = PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Margin = new Thickness(0, 0, 0, 12),
            Child = inside,
        };
    }

    /// <summary>Really closes the window (app exit); the user's close button only hides it.</summary>
    public void Shutdown()
    {
        _closing = true;
        _subscription.Dispose();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void Apply(EdithEvent evt, bool refreshPanels)
    {
        if (evt.Kind == EventKinds.StateChanged)
        {
            _state.Text = DescribeState(evt.Summary);
            _dot.Fill = evt.Summary switch
            {
                "Thinking" => Warn,
                "Warning" => Bad,
                "Sleeping" => Muted,
                _ => Accent,
            };
        }

        _feed.Items.Insert(0, FeedLine(evt));
        while (_feed.Items.Count > MaxFeedItems)
            _feed.Items.RemoveAt(_feed.Items.Count - 1);

        if (refreshPanels &&
            evt.Kind is EventKinds.HealthChanged or EventKinds.ResourcesChanged or EventKinds.TaskDeferred or EventKinds.TaskResumed
                or "pulse.card" or "memory.learned" or "memory.episode")
        {
            RefreshPanels();
        }
    }

    private void RefreshPanels()
    {
        ResourcePlan? plan = _runtime.Resources.Current;
        _resources.Text = plan is null
            ? "ресурсы ещё не измерены"
            : ResourceMonitor.Describe(plan) + (plan.Gpu is { } gpu
                ? $" · VRAM {gpu.MemoryUsedMb}/{gpu.MemoryTotalMb} МБ · GPU {gpu.UtilizationPercent}%"
                : "");

        int deferred = _runtime.Deferred.Pending.Count;
        _deferred.Text = deferred == 0 ? "" : $"Отложено задач: {deferred}";

        RefreshCards();
        RefreshMemory();
        RefreshServices();

        _health.Children.Clear();
        IReadOnlyDictionary<string, HealthResult> current = _runtime.Health.Current;
        if (current.Count == 0)
        {
            _health.Children.Add(Text("проверка ещё не выполнялась", 13, Muted));
            return;
        }

        foreach ((string name, HealthResult result) in current.OrderBy(pair => pair.Key))
        {
            (string mark, Brush brush) = result.Status switch
            {
                HealthStatus.Ok => ("●", Good),
                HealthStatus.Degraded => ("●", Warn),
                HealthStatus.Failed => ("●", Bad),
                _ => ("○", Muted),
            };

            var line = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            line.Inlines.Add(new System.Windows.Documents.Run(mark + " ") { Foreground = brush });
            line.Inlines.Add(new System.Windows.Documents.Run(name) { Foreground = Foreground0 });
            if (result.Detail.Length > 0)
                line.Inlines.Add(new System.Windows.Documents.Run(" — " + result.Detail) { Foreground = Muted });
            _health.Children.Add(line);
        }
    }

    private static StackPanel ServiceRow(string name, string value, Brush valueBrush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        row.Children.Add(new TextBlock { Text = name + "  ", Foreground = Muted, FontSize = 13, MinWidth = 150 });
        row.Children.Add(new TextBlock { Text = value, Foreground = valueBrush, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        return row;
    }

    /// <summary>Librarian, archive, pulse, watchdog and the answer window: what runs and what it has done.</summary>
    private void RefreshServices()
    {
        // A fresh install: what is still missing, in plain words (hidden when everything is in place).
        _todo.Children.Clear();
        IReadOnlyList<string> problems = FirstRun.Problems();
        foreach (string problem in problems)
            _todo.Children.Add(new TextBlock { Text = "• " + problem, Foreground = Warn, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) });
        if (_todoCard is not null)
            _todoCard.Visibility = problems.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        _services.Children.Clear();

        bool libraryOn = LibrarianSwitch.On;
        Brush stateBrush = !libraryOn ? Muted : LibraryStatus.State.StartsWith("работает", StringComparison.Ordinal) ? Good : LibraryStatus.State.StartsWith("ждёт", StringComparison.Ordinal) ? Warn : Foreground0;
        _services.Children.Add(ServiceRow("Библиотекарь", LibraryStatus.State, stateBrush));
        if (LibraryStatus.Current.Length > 0)
            _services.Children.Add(ServiceRow("  сейчас", LibraryStatus.Current, Foreground0));

        (int notes, int left) = ArchiveCounts();
        _services.Children.Add(ServiceRow("Архив", $"заметок записано: {notes}" + (left > 0 ? $" · ещё не разобрано сессий: {left}" : ""), Foreground0));
        if (LibraryStatus.LastNote.Length > 0)
            _services.Children.Add(ServiceRow("  последняя", $"{LibraryStatus.LastNote} ({LibraryStatus.LastNoteAt:HH:mm})", Muted));

        int fresh = PulseService.Instance?.Store.Cards().Count(c => c.State == Edith.Core.Activity.ActivityStore.CardNew) ?? 0;
        _services.Children.Add(ServiceRow("Пульс", PulseService.Instance is null ? "выключен" : fresh == 0 ? "новых заметок нет" : $"новых заметок: {fresh}", PulseService.Instance is null ? Muted : Foreground0));

        string crashes = "0";
        try { crashes = File.Exists(@"C:\EDITH\data\crashes.log") ? File.ReadAllLines(@"C:\EDITH\data\crashes.log").Length.ToString() : "0"; } catch { }
        _services.Children.Add(ServiceRow("Сторож", crashes == "0" ? "падений не было" : $"перезапусков после падений: {crashes}", crashes == "0" ? Good : Warn));

        _services.Children.Add(ServiceRow("Окно ответов", UiModes.Quiet ? "тихий режим: только пишу" : UiModes.WindowOn ? "показываю ответы" : "выключено", Foreground0));
    }

    private static (int Notes, int Left) ArchiveCounts()
    {
        try
        {
            string vault = @"C:\Obsidian\KORTY";
            int notes = Directory.Exists(vault)
                ? Directory.GetDirectories(vault).Sum(d => Directory.Exists(System.IO.Path.Combine(d, "Сессии AI")) ? Directory.GetFiles(System.IO.Path.Combine(d, "Сессии AI"), "*.md").Length : 0)
                : 0;
            int done = 0;
            string ledger = @"C:\EDITH\data\library\ledger.json";
            if (File.Exists(ledger))
                done = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ledger)).RootElement.EnumerateObject().Count(p => p.Name.Contains(':') && !p.Name.StartsWith("find:", StringComparison.Ordinal) && !p.Name.StartsWith("picture:", StringComparison.Ordinal));
            return (notes, Math.Max(0, Edith.Core.Library.SessionSources.CountFiles(LibrarySettings.Load().Sources) - done));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>What the background pulse noticed; the cross hides a note for good.</summary>
    private void RefreshCards()
    {
        _cards.Children.Clear();
        Edith.Core.Activity.ActivityStore? store = PulseService.Instance?.Store;
        IReadOnlyList<Edith.Core.Activity.PulseCard> cards = store?.Cards(max: 12) ?? [];
        if (store is null || cards.Count == 0)
        {
            _cards.Children.Add(Text("Пока ничего. Я слежу за вашими проектами и вечерами и скажу, как только будет что заметить.", 13, Muted));
            return;
        }

        foreach (Edith.Core.Activity.PulseCard card in cards)
        {
            bool fresh = card.State == Edith.Core.Activity.ActivityStore.CardNew;
            long id = card.Id;
            var text = new StackPanel();
            text.Children.Add(Text((fresh ? "● " : "") + card.Title, 13, fresh ? Accent : Foreground0, FontWeights.SemiBold));
            text.Children.Add(Text(card.Body, 12, Muted));
            _cards.Children.Add(Removable(text, "Убрать", () =>
            {
                store.SetCardState(id, Edith.Core.Activity.ActivityStore.CardDismissed);
                RefreshCards();
            }));
        }
    }

    /// <summary>What she knows about the owner and the last conversations; a cross makes her forget a fact.</summary>
    private void RefreshMemory()
    {
        _facts.Children.Clear();
        _talks.Children.Clear();
        if (_memory is null)
        {
            _facts.Children.Add(Text("память недоступна", 13, Muted));
            return;
        }

        IReadOnlyList<Edith.Core.Memory.MemoryItem> items = _memory.ActiveItems();
        if (items.Count == 0)
            _facts.Children.Add(Text("Пока ничего не знаю. Скажите «запомни, что…» или просто поговорите со мной.", 13, Muted));

        foreach (Edith.Core.Memory.MemoryItem item in items.OrderBy(i => i.Type).ThenBy(i => i.Key))
        {
            string key = item.Key;
            var line = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            line.Inlines.Add(new System.Windows.Documents.Run(key + " — ") { Foreground = Muted });
            line.Inlines.Add(new System.Windows.Documents.Run(item.Content) { Foreground = Foreground0 });
            _facts.Children.Add(Removable(line, "Забыть", () =>
            {
                _memory.Forget(key);
                RefreshMemory();
            }));
        }

        DateTimeOffset now = DateTimeOffset.Now;
        IReadOnlyList<Edith.Core.Memory.Episode> episodes = _memory.Episodes(now.AddDays(-14), now.AddMinutes(1), 8);
        if (episodes.Count == 0)
            _talks.Children.Add(Text("Итоги разговоров появятся, когда мы поговорим и вы на время замолчите.", 13, Muted));

        foreach (Edith.Core.Memory.Episode episode in episodes.OrderByDescending(e => e.End))
        {
            var text = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            text.Children.Add(Text(Edith.Core.Memory.EpisodeRecall.When(episode.End, now), 11, Muted));
            text.Children.Add(Text(episode.Summary, 13, Foreground0));
            _talks.Children.Add(text);
        }
    }

    private static UIElement Removable(UIElement content, string tip, Action onRemove)
    {
        var row = new DockPanel { Margin = new Thickness(0, 3, 0, 5), LastChildFill = true };
        var cross = new Button
        {
            Content = "✕",
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(10, 0, 0, 0),
            Background = Brushes.Transparent,
            Foreground = Muted,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = tip,
        };
        cross.Click += (_, _) => onRemove();
        DockPanel.SetDock(cross, Dock.Right);
        row.Children.Add(cross);
        row.Children.Add(content);
        return row;
    }

    private static TextBlock FeedLine(EdithEvent evt)
    {
        Brush brush =
            evt.Kind is EventKinds.Error or EventKinds.ToolFailed or EventKinds.PermissionFailed ? Bad :
            evt.Kind == EventKinds.HealthChanged && evt.Get("status") is "Failed" ? Bad :
            evt.Kind.StartsWith("permission.", StringComparison.Ordinal) ? Warn :
            evt.Kind.StartsWith("tool.", StringComparison.Ordinal) ? Accent :
            evt.Kind == EventKinds.StateChanged ? Muted :
            Foreground0;

        string summary = OneLine(evt.Summary, 1500) + Details(evt);

        return new TextBlock
        {
            Text = $"{evt.Timestamp:HH:mm:ss}  {Label(evt.Kind),-12} {summary}",
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap,
            ToolTip = evt.Data is { Count: > 0 } data
                ? string.Join(Environment.NewLine, data.Select(p => $"{p.Key}: {OneLine(p.Value, 600)}"))
                : null,
        };
    }

    private static string OneLine(string? text, int max)
    {
        string line = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>What happened, in words: tool arguments and result, route, timings, what was heard.</summary>
    private static string Details(EdithEvent evt)
    {
        string? Get(string key) => evt.Get(key) is { Length: > 0 } v ? v : null;
        var parts = new List<string>();
        switch (evt.Kind)
        {
            case EventKinds.ToolStarted:
                if (Get("arguments") is { } args && args != "{}") parts.Add(OneLine(args, 300));
                break;
            case EventKinds.ToolFinished:
            case EventKinds.ToolFailed:
                if (Get("arguments") is { } a && a != "{}") parts.Add(OneLine(a, 200));
                if (Get("result") is { } r) parts.Add("→ " + OneLine(r, 700));
                break;
            case EventKinds.ResponseSpoken:
                if (Get("route") is { } route) parts.Add("[" + route + "]");
                break;
            case "brain.latency":
                parts.Add($"{Get("wall-ms") ?? "?"} мс, шаг {Get("step") ?? "?"}, токены {Get("prompt-tokens") ?? "?"}/{Get("gen-tokens") ?? "?"}");
                if (Get("timeout") is { } t && t != "none") parts.Add("таймаут: " + t);
                break;
            case EventKinds.WakeAccepted:
            case "voice.wake-rejected":
            case "voice.wake-check":
                if (Get("heard") is { } heard) parts.Add("услышала: «" + OneLine(heard, 200) + "»");
                if (Get("source") is { } source) parts.Add(source);
                break;
            case EventKinds.BargeIn:
                if (Get("heard") is { } h) parts.Add("услышала: «" + OneLine(h, 200) + "»");
                break;
        }

        return parts.Count == 0 ? "" : "  " + string.Join("  ", parts);
    }

    private static string Label(string kind) => kind switch
    {
        EventKinds.CommandHeard => "вы:",
        EventKinds.ResponseSpoken => "EDITH:",
        EventKinds.ToolStarted => "инструмент",
        EventKinds.ToolFinished => "результат",
        EventKinds.ToolFailed => "ошибка инстр.",
        EventKinds.WakeAccepted => "«Эдит»",
        "voice.wake-rejected" => "не «Эдит»",
        EventKinds.BargeIn => "перебила",
        "voice.turn" => "конец фразы",
        "voice.scan" => "слышу при ответе",
        "voice.mic-restart" => "микрофон",
        "voice.cancel" => "стоп",
        "voice.session-end" => "сессия конец",
        "brain.latency" => "мозг",
        EventKinds.Error => "ОШИБКА",
        EventKinds.StateChanged => "состояние",
        _ => kind,
    };

    private static string DescribeState(string state) => state switch
    {
        "Sleeping" => "жду «Эдит»",
        "Listening" => "слушаю",
        "Thinking" => "думаю",
        "Speaking" => "говорю",
        "Warning" => "проблема",
        _ => state,
    };

    private static StackPanel Row(string label, TextBlock value)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        row.Children.Add(Text(label + ":  ", 13, Muted));
        row.Children.Add(value);
        value.TextWrapping = TextWrapping.Wrap;
        return row;
    }

    private static TextBlock Section(string title)
    {
        TextBlock block = Text(title.ToUpperInvariant(), 11, Muted, FontWeights.SemiBold);
        block.Margin = new Thickness(0, 14, 0, 4);
        return block;
    }

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null) =>
        new()
        {
            Text = text,
            FontSize = size,
            Foreground = brush,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
