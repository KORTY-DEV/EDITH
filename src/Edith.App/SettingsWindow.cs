using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Edith.Core;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>
/// Settings from the tray: a few everyday knobs over EDITH's config files. Values are written
/// in place, so comments in the files survive. Some apply at once, others after a restart.
/// </summary>
public sealed class SettingsWindow : Window
{
    private const string ConfigDir = @"C:\EDITH\config";
    private static readonly string PersonaPath = Path.Combine(ConfigDir, "persona.json");
    private static readonly string TtsPath = Path.Combine(ConfigDir, "tts.json");
    private static readonly string WakePath = Path.Combine(ConfigDir, "wake.json");
    private static readonly string ConversationPath = Path.Combine(ConfigDir, "conversation.json");
    private static readonly string BrainPath = Path.Combine(ConfigDir, "brain.json");

    private static readonly Brush Background0 = Frozen(Color.FromRgb(17, 20, 26));
    private static readonly Brush Panel = Frozen(Color.FromRgb(28, 33, 42));
    private static readonly Brush Foreground0 = Frozen(Color.FromRgb(220, 228, 238));
    private static readonly Brush Muted = Frozen(Color.FromRgb(130, 142, 158));
    private static readonly Brush Accent = Frozen(Color.FromRgb(85, 223, 255));

    private static readonly (string Label, string Value)[] Voices =
    [
        ("Ксения (xenia) — основной", "xenia"),
        ("Ксения 2 (kseniya)", "kseniya"),
        ("Бая (baya)", "baya"),
        ("Айдар (aidar) — мужской", "aidar"),
        ("Евгений (eugene) — мужской", "eugene"),
    ];

    private static readonly (string Label, double Threshold)[] WakeLevels =
    [
        ("Чуткая — слышит и тихое «Эдит», чаще ошибается", 0.5),
        ("Обычная", 0.6),
        ("Строгая — реже ложные, говорить чётче", 0.7),
    ];

    private static readonly (string Label, string Value)[] KeepAliveOptions =
    [
        ("30 минут", "30m"), ("1 час", "1h"), ("3 часа", "3h"), ("8 часов", "8h"), ("Всегда", "-1"),
    ];

    private readonly Action _restart;
    private readonly TextBox _address = new();
    private readonly ComboBox _voice = new();
    private readonly ComboBox _wake = new();
    private readonly ComboBox _rate = new();
    private static readonly int[] Rates = [80, 85, 90, 95, 100, 110];
    private readonly Slider _silence = new() { Minimum = 350, Maximum = 1500, TickFrequency = 50, IsSnapToTickEnabled = true };
    private readonly CheckBox _smartTurn = new() { Content = "Умно понимать, что я договорил (Smart Turn)", Foreground = Foreground0 };
    private bool _smartTurnLoaded;
    private readonly TextBlock _silenceValue = Label("", Muted);
    private readonly ComboBox _keepAlive = new();
    private readonly CheckBox _autostart = new() { Content = "Запускать EDITH вместе с Windows", Foreground = Foreground0 };
    private readonly CheckBox _answerWindow = new() { Content = "Показывать ответы в окне на экране", Foreground = Foreground0 };
    private readonly CheckBox _quiet = new() { Content = "Тихий режим: не говорить вслух, только писать", Foreground = Foreground0 };
    private readonly CheckBox _librarian = new() { Content = "Библиотекарь: вести архив в Obsidian (когда я не занята и нет игры)", Foreground = Foreground0 };
    private readonly CheckBox _gaming = new() { Content = "Уступать видеокарту играм (игровой режим)", Foreground = Foreground0 };
    private readonly TextBlock _status = Label("", Accent);

    private int _loadedRate = 100;
    private string _loadedVoice = "", _loadedWake = "", _loadedSilence = "";

    public SettingsWindow(Action restart)
    {
        _restart = restart;

        Title = "E.D.I.T.H. — настройки";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Background0;
        Foreground = Foreground0;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "edith.ico")))
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "edith.ico")));

        foreach ((string label, _) in Voices) _voice.Items.Add(label);
        foreach ((string label, _) in WakeLevels) _wake.Items.Add(label);
        foreach (int rate in Rates) _rate.Items.Add(rate == 100 ? "Обычная (100%)" : $"{rate}%");
        foreach ((string label, _) in KeepAliveOptions) _keepAlive.Items.Add(label);
        _silence.ValueChanged += (_, _) => _silenceValue.Text = $"{_silence.Value:0} мс";

        var root = new StackPanel { Margin = new Thickness(18, 14, 18, 16) };
        root.Children.Add(Label("Настройки", Foreground0, 20, FontWeights.SemiBold));

        root.Children.Add(Section("Запуск"));
        root.Children.Add(Pad(_autostart));
        root.Children.Add(Hint("Ярлык в папке автозагрузки; убрать галочку — и он удалится."));

        root.Children.Add(Section("Окно ответов и архив"));
        root.Children.Add(Pad(_answerWindow));
        root.Children.Add(Pad(_quiet));
        root.Children.Add(Pad(_librarian));
        var models = new Button { Content = "Модели: выбрать и скачать…", Margin = new Thickness(0, 6, 0, 2), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        models.Click += (_, _) => new ModelsWindow().Show();
        root.Children.Add(models);
        root.Children.Add(Hint("Применяется сразу. Окно можно двигать и растягивать за уголок; голосом: «убери окно», «покажи окно», «тихий режим»."));

        root.Children.Add(Section("Общение"));
        root.Children.Add(Field("Как ко мне обращаться", _address));
        root.Children.Add(Hint("Применяется сразу."));

        root.Children.Add(Section("Голос и слух"));
        root.Children.Add(Field("Голос", _voice));
        var listen = new Button { Content = "▶ Прослушать (целая фраза)", Margin = new Thickness(0, 4, 0, 4), Padding = new Thickness(10, 4, 10, 4), HorizontalAlignment = HorizontalAlignment.Left };
        var player = new Edith.Speech.WavPlayer();
        listen.Click += async (_, _) =>
        {
            string voice = Voices[Math.Max(0, _voice.SelectedIndex)].Value;
            string? file = Directory.Exists(@"C:\EDITH\data\voice-samples")
                ? Directory.GetFiles(@"C:\EDITH\data\voice-samples", voice + "-*.wav").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                : null;
            if (file is null)
            {
                listen.Content = "Образец ещё записывается, попробуйте через минуту";
                return;
            }

            listen.IsEnabled = false;
            try { await player.PlayAsync(file, CancellationToken.None); } catch { }
            listen.IsEnabled = true;
            listen.Content = "▶ Прослушать (целая фраза)";
        };
        root.Children.Add(listen);
        root.Children.Add(Field("Скорость речи", _rate));
        root.Children.Add(Field("Чувствительность к «Эдит»", _wake));
        var silenceRow = new DockPanel();
        DockPanel.SetDock(_silenceValue, Dock.Right);
        _silenceValue.Width = 60;
        _silenceValue.TextAlignment = TextAlignment.Right;
        silenceRow.Children.Add(_silenceValue);
        silenceRow.Children.Add(_silence);
        root.Children.Add(Pad(_smartTurn));
        root.Children.Add(Field("Пауза, после которой EDITH решает, что ты договорил", silenceRow));
        root.Children.Add(Hint("Меньше — отвечает быстрее, но может перебить на паузе. Голос, скорость и слух меняются после перезапуска."));

        root.Children.Add(Section("Модель"));
        root.Children.Add(Field("Держать модель в памяти видеокарты", _keepAlive));
        root.Children.Add(Pad(_gaming));
        root.Children.Add(Hint("Применяется со следующего вопроса."));

        root.Children.Add(Section("Файлы"));
        var files = new WrapPanel();
        files.Children.Add(LinkButton("Словарь ударений", () => OpenFile(Path.Combine(ConfigDir, "stress.json"))));
        files.Children.Add(LinkButton("Характер", () => OpenFile(PersonaPath)));
        files.Children.Add(LinkButton("Приложения", () => OpenFile(Path.Combine(ConfigDir, File.Exists(Path.Combine(ConfigDir, "apps.json")) ? "apps.json" : "apps.example.json"))));
        files.Children.Add(LinkButton("Папка EDITH", () => OpenFile(@"C:\EDITH")));
        root.Children.Add(files);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(Button("Сохранить", () => Save(restartAfter: false)));
        buttons.Children.Add(Button("Сохранить и перезапустить", () => Save(restartAfter: true), primary: true));
        root.Children.Add(buttons);
        root.Children.Add(_status);

        Content = root;
        Load();
    }

    private void Load()
    {
        string persona = Read(PersonaPath), tts = Read(TtsPath), wake = Read(WakePath),
            conversation = Read(ConversationPath), brain = Read(BrainPath);

        _address.Text = ConfigText.Get(persona, "defaultAddress") ?? "сэр";

        _loadedVoice = ConfigText.Get(tts, "localVoice") ?? "xenia";
        _voice.SelectedIndex = Math.Max(0, Array.FindIndex(Voices, v => v.Value == _loadedVoice));
        _loadedRate = (int)(ConfigText.GetNumber(tts, "localRatePercent") ?? 100);
        _rate.SelectedIndex = Array.IndexOf(Rates, _loadedRate) is var ri and >= 0 ? ri : Array.IndexOf(Rates, 100);

        double threshold = ConfigText.GetNumber(wake, "threshold") ?? 0.6;
        _wake.SelectedIndex = Array.FindIndex(WakeLevels, w => Math.Abs(w.Threshold - threshold) < 0.001) is var wi and >= 0 ? wi : 1;
        _loadedWake = _wake.SelectedIndex.ToString();

        _smartTurnLoaded = ConfigText.Get(conversation, "smartTurn") != "false";
        _smartTurn.IsChecked = _smartTurnLoaded;
        _silence.Value = ConfigText.GetNumber(conversation, SilenceKey(_smartTurnLoaded)) ?? (_smartTurnLoaded ? 550 : 850);
        _silenceValue.Text = $"{_silence.Value:0} мс";
        _loadedSilence = _silence.Value.ToString("0");

        string keepAlive = ConfigText.Get(brain, "normal.keepAlive") ?? "3h";
        _keepAlive.SelectedIndex = Array.FindIndex(KeepAliveOptions, k => k.Value == keepAlive) is var ki and >= 0 ? ki : 2;

        _gaming.IsChecked = ConfigText.Get(brain, "gaming.enabled") != "false";
        _autostart.IsChecked = Autostart.IsEnabled;
        _answerWindow.IsChecked = UiModes.WindowOn;
        _quiet.IsChecked = UiModes.Quiet;
        _librarian.IsChecked = LibrarianSwitch.On;
    }

    private void Save(bool restartAfter)
    {
        try
        {
            Update(PersonaPath, t => ConfigText.Set(t, "defaultAddress", string.IsNullOrWhiteSpace(_address.Text) ? "сэр" : _address.Text.Trim()));
            Update(TtsPath, t => ConfigText.Set(t, "localVoice", Voices[Math.Max(0, _voice.SelectedIndex)].Value));
            int rate = Rates[Math.Max(0, _rate.SelectedIndex)];
            Update(TtsPath, t => ConfigText.Get(t, "localRatePercent") is null
                ? t.Replace("\"localVoice\"", "\"localRatePercent\": " + rate + ",\n    \"localVoice\"")
                : ConfigText.Set(t, "localRatePercent", rate));

            double threshold = WakeLevels[Math.Max(0, _wake.SelectedIndex)].Threshold;
            Update(WakePath, t => ConfigText.Set(ConfigText.Set(t, "threshold", threshold), "verifierThreshold", threshold));
            bool smart = _smartTurn.IsChecked == true;
            Update(ConversationPath, t => ConfigText.Set(ConfigText.Set(t, "smartTurn", smart), SilenceKey(smart), (int)_silence.Value));
            Update(BrainPath, t => ConfigText.Set(
                ConfigText.Set(t, "normal.keepAlive", KeepAliveOptions[Math.Max(0, _keepAlive.SelectedIndex)].Value),
                "gaming.enabled", _gaming.IsChecked == true));

            UiModes.Set(_answerWindow.IsChecked == true, _quiet.IsChecked == true);
            if ((_librarian.IsChecked == true) != LibrarianSwitch.On)
                LibrarianSwitch.Set(_librarian.IsChecked == true);

            if ((_autostart.IsChecked == true) != Autostart.IsEnabled)
                Autostart.Set(_autostart.IsChecked == true);

            bool needsRestart = Voices[Math.Max(0, _voice.SelectedIndex)].Value != _loadedVoice ||
                                rate != _loadedRate ||
                                _wake.SelectedIndex.ToString() != _loadedWake ||
                                _silence.Value.ToString("0") != _loadedSilence ||
                                (_smartTurn.IsChecked == true) != _smartTurnLoaded;

            EdithEvents.Publish(EventKinds.SettingsChanged, "Настройки сохранены");
            if (restartAfter)
            {
                _restart();
                return;
            }

            _status.Text = needsRestart ? "Сохранено. Голос и слух изменятся после перезапуска." : "Сохранено.";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось сохранить: " + ex.Message;
        }
    }

    /// <summary>With Smart Turn the pause only triggers the question "is he done?", so it is shorter.</summary>
    private static string SilenceKey(bool smartTurn) =>
        smartTurn ? "turnEndSilenceMilliseconds" : "speechEndSilenceMilliseconds";

    private static string Read(string path) =>
        File.Exists(path) ? File.ReadAllText(path) : "";

    private static void Update(string path, Func<string, string> change)
    {
        if (!File.Exists(path))
            return;

        string before = File.ReadAllText(path);
        string after = change(before);
        if (after != before)
            File.WriteAllText(path, after, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static UIElement Section(string text) =>
        Label(text, Accent, 13, FontWeights.SemiBold, new Thickness(0, 14, 0, 4));

    private static UIElement Field(string label, UIElement input)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 2) };
        panel.Children.Add(Label(label, Foreground0, 13));
        if (input is Control control)
        {
            control.Margin = new Thickness(0, 3, 0, 0);
            control.FontSize = 13;
        }
        else if (input is FrameworkElement element)
        {
            element.Margin = new Thickness(0, 3, 0, 0);
        }

        panel.Children.Add(input);
        return panel;
    }

    private static UIElement Hint(string text) =>
        Label(text, Muted, 12, margin: new Thickness(0, 2, 0, 0));

    private static UIElement Pad(UIElement element) =>
        new Border { Margin = new Thickness(0, 8, 0, 0), Child = element };

    private static Button Button(string text, Action click, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Background = primary ? Accent : Panel,
            Foreground = primary ? Background0 : Foreground0,
            BorderThickness = new Thickness(0),
        };
        button.Click += (_, _) => click();
        return button;
    }

    private static Button LinkButton(string text, Action click)
    {
        Button button = Button(text, click);
        button.Margin = new Thickness(0, 4, 8, 0);
        button.Padding = new Thickness(10, 4, 10, 4);
        return button;
    }

    private static TextBlock Label(string text, Brush brush, double size = 13, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        Foreground = brush,
        FontSize = size,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? new Thickness(0),
    };

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
