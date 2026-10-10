using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Edith.Core.Runtime;

namespace Edith.App;

/// <summary>
/// Models in one place: what is installed in Ollama, which one the librarian uses (and how much of it sits on the video
/// card), and a short list of recommended ones with a download button. A download only starts when the owner clicks it.
/// </summary>
public sealed class ModelsWindow : Window
{
    private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(17, 20, 28));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(23, 27, 36));
    private static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(220, 228, 238));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(130, 142, 158));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(85, 223, 255));
    private static readonly Brush Good = new SolidColorBrush(Color.FromRgb(110, 210, 140));

    /// <summary>Name, size, what it is good for.</summary>
    private static readonly (string Name, string Size, string Note)[] Recommended =
    [
        ("qwen3.5:4b", "3,4 ГБ", "библиотекарь: подробные записи, умещается в 3 ГБ видеопамяти — рекомендуется"),
        ("qwen3.5:9b", "6,6 ГБ", "библиотекарь: чуть подробнее, но занимает почти всю видеокарту"),
        ("qwen3:8b", "5,2 ГБ", "запасной мозг EDITH, когда облако недоступно"),
        ("gemma4:e2b-it-qat", "4,3 ГБ", "лёгкая модель, быстрее, но поверхностнее"),
        ("ministral-3:3b", "3,0 ГБ", "самая маленькая из пригодных; для слабых ПК"),
    ];

    private readonly StackPanel _installed = new();
    private readonly StackPanel _recommended = new();
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private readonly ComboBox _provider = new();
    private readonly ComboBox _model = new() { IsEditable = true };
    private readonly Slider _layers = new() { Minimum = 0, Maximum = 40, TickFrequency = 2, IsSnapToTickEnabled = true, Width = 260 };
    private readonly TextBlock _layersText = new() { Foreground = Fg, FontSize = 13, Margin = new Thickness(10, 0, 0, 0) };

    public ModelsWindow()
    {
        Title = "E.D.I.T.H. — модели";
        Theme.Apply(this);
        Width = 560;
        Height = 700;
        Background = Bg;
        Foreground = Fg;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        root.Children.Add(Text("Модели", 20, Fg, FontWeights.SemiBold));
        root.Children.Add(Text("Что стоит на этом компьютере и какая модель ведёт архив. Скачивание начинается только по вашей кнопке.", 12, Muted));

        _provider.Items.Add("Локальная модель (ничего не уходит в облако)");
        _provider.Items.Add("GigaChat (облако, быстрее)");
        root.Children.Add(Card("Библиотекарь",
            Row("Где работает", _provider),
            Row("Локальная модель", _model),
            Row("Слоёв на видеокарте", new StackPanel { Orientation = Orientation.Horizontal, Children = { _layers, _layersText } }),
            Text("Меньше слоёв — меньше видеопамяти и нагрузки на видеокарту, но больше работает процессор и дольше ответ; ум модели от этого не меняется.", 11.5, Muted)));
        _layers.ValueChanged += (_, _) => _layersText.Text = _layers.Value >= 40 ? "все" : $"{_layers.Value:0} из ~38";

        var save = new Button { Content = "Сохранить выбор", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        save.Click += (_, _) => Save();
        root.Children.Add(save);

        root.Children.Add(Card("Установлены (Ollama)", _installed));
        root.Children.Add(Card("Рекомендуемые", _recommended));
        root.Children.Add(_status);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Loaded += async (_, _) => await InitAsync();
    }

    /// <summary>Fills the page (also when its content lives inside the main window instead of this one).</summary>
    public async Task InitAsync()
    {
        LoadConfig();
        await RefreshAsync();
    }

    private void LoadConfig()
    {
        try
        {
            string text = File.ReadAllText(LibrarianService.ConfigPath);
            _provider.SelectedIndex = (Edith.Core.ConfigText.Get(text, "provider") ?? "local") == "gigachat" ? 1 : 0;
            _model.Text = Edith.Core.ConfigText.Get(text, "localModel") ?? "qwen3.5:4b";
            _layers.Value = Edith.Core.ConfigText.GetNumber(text, "localGpuLayers") ?? 26;
        }
        catch
        {
            _provider.SelectedIndex = 0;
            _model.Text = "qwen3.5:4b";
            _layers.Value = 26;
        }

        _layersText.Text = _layers.Value >= 40 ? "все" : $"{_layers.Value:0} из ~38";
    }

    private void Save()
    {
        try
        {
            string text = File.ReadAllText(LibrarianService.ConfigPath);
            text = Edith.Core.ConfigText.Set(text, "provider", _provider.SelectedIndex == 1 ? "gigachat" : "local");
            text = Edith.Core.ConfigText.Set(text, "localModel", _model.Text.Trim());
            text = Edith.Core.ConfigText.Set(text, "localGpuLayers", (int)(_layers.Value >= 40 ? -1 : _layers.Value));
            File.WriteAllText(LibrarianService.ConfigPath, text);
            _status.Text = "Сохранено. Библиотекарь подхватит выбор при следующей записи.";
        }
        catch (Exception ex)
        {
            _status.Text = "Не удалось сохранить: " + ex.Message;
        }
    }

    private async Task RefreshAsync()
    {
        List<(string Name, string Size)> models = await Task.Run(ListInstalled);
        _installed.Children.Clear();
        if (models.Count == 0)
            _installed.Children.Add(Text("Ollama не отвечает или моделей нет.", 13, Muted));
        foreach ((string name, string size) in models)
        {
            string captured = name;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var use = new Button { Content = "Для библиотекаря", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(10, 0, 0, 0) };
            use.Click += (_, _) => { _model.Text = captured; _provider.SelectedIndex = 0; };
            DockPanel.SetDock(use, Dock.Right);
            row.Children.Add(use);
            row.Children.Add(Text($"{name}  ·  {size}", 13, Fg));
            _installed.Children.Add(row);
        }

        _recommended.Children.Clear();
        foreach ((string name, string size, string note) in Recommended)
        {
            bool have = models.Any(m => m.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            string captured = name;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var button = new Button { Content = have ? "Установлена" : "Скачать", IsEnabled = !have, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
            button.Click += async (_, _) => await PullAsync(captured, button);
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            var info = new StackPanel();
            info.Children.Add(Text($"{name}  ·  {size}", 13, have ? Good : Fg, FontWeights.SemiBold));
            info.Children.Add(Text(note, 12, Muted));
            row.Children.Add(info);
            _recommended.Children.Add(row);
        }
    }

    private static List<(string Name, string Size)> ListInstalled()
    {
        var result = new List<(string, string)>();
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("ollama", "list") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = System.Text.Encoding.UTF8 });
            string output = process?.StandardOutput.ReadToEnd() ?? "";
            process?.WaitForExit(8000);
            foreach (string line in output.Split('\n').Skip(1))
            {
                string[] parts = Regex.Split(line.Trim(), @"\s{2,}");
                if (parts.Length >= 3 && parts[0].Length > 0)
                    result.Add((parts[0], parts[2]));
            }
        }
        catch
        {
        }

        return result;
    }

    private async Task PullAsync(string model, Button button)
    {
        button.IsEnabled = false;
        button.Content = "Скачиваю…";
        _status.Text = $"Скачиваю {model}: это несколько гигабайт, может занять время. Окно можно закрыть, загрузка продолжится.";
        bool ok = await Task.Run(() =>
        {
            try
            {
                var start = new ProcessStartInfo("ollama", "pull " + model) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                using Process? process = Process.Start(start);
                if (process is null)
                    return false;
                string last = "";
                process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) last = e.Data; };
                process.BeginErrorReadLine();
                while (!process.WaitForExit(1500))
                {
                    string progress = Regex.Match(last, @"\d+%").Value;
                    Dispatcher.BeginInvoke(() => _status.Text = $"Скачиваю {model}: {progress}");
                }

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        });

        _status.Text = ok ? $"{model} скачана." : $"Не получилось скачать {model}: проверьте, что Ollama запущена и есть место на диске.";
        EdithEvents.Publish("models.pulled", model + (ok ? ": готово" : ": ошибка"));
        await RefreshAsync();
    }

    private static Border Card(string title, params UIElement[] content)
    {
        var inside = new StackPanel();
        inside.Children.Add(Text(title.ToUpperInvariant(), 11, Muted, FontWeights.SemiBold));
        foreach (UIElement element in content)
        {
            if (element is FrameworkElement f)
                f.Margin = new Thickness(0, 6, 0, 0);
            inside.Children.Add(element);
        }

        return new Border { Background = Panel, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 12, 16, 14), Margin = new Thickness(0, 12, 0, 0), Child = inside };
    }

    private static UIElement Row(string label, UIElement input)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(label, 12, Muted));
        panel.Children.Add(input);
        return panel;
    }

    private static TextBlock Text(string text, double size, Brush brush, FontWeight? weight = null) =>
        new() { Text = text, FontSize = size, Foreground = brush, FontWeight = weight ?? FontWeights.Normal, TextWrapping = TextWrapping.Wrap };
}
