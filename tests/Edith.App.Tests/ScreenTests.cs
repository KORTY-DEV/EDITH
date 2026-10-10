using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Edith.App;
using Edith.Core.Persona;
using Edith.Core.Screen;

namespace Edith.App.Tests;

public sealed class ScreenRouteTests
{
    private sealed class FakeScreen : IScreen
    {
        public List<string> Clicked { get; } = [];
        public string Typed = "";

        public Task<ScreenContent?> ReadForegroundAsync(CancellationToken cancellationToken) =>
            Task.FromResult<ScreenContent?>(new ScreenContent("notepad", "Ошибка", "Не удалось сохранить файл: диск переполнен.", "uia"));

        public IReadOnlyList<UiElementInfo> Clickables() => [new("Сохранить", "Button"), new("Удалить", "Button")];

        public bool Click(UiElementInfo element) { Clicked.Add(element.Name); return true; }

        public void Type(string text) => Typed = text;
    }

    private static (ResponseEngine Engine, FakeScreen Screen, Func<Func<Task<string>>?> Pending) Create()
    {
        var screen = new FakeScreen();
        Func<Task<string>>? pending = null;
        var engine = new ResponseEngine(
            Path.Combine(Path.GetTempPath(), $"edith-memory-{Guid.NewGuid():N}.json"),
            () => [],
            () => new PersonaProfile(),
            requestPermission: (summary, _, execute) => { pending = execute; return $"Нужно подтверждение: {summary}."; },
            screen: screen);
        return (engine, screen, () => pending);
    }

    [Fact]
    public async Task Read_WithBrainOff_ReadsTheWindowText()
    {
        (ResponseEngine engine, _, _) = Create();

        string answer = await engine.HandleAsync("что за ошибка");

        Assert.Equal("Screen", engine.LastRoute);
        Assert.Equal("В окне «Ошибка»: Не удалось сохранить файл: диск переполнен.", answer);
    }

    [Fact]
    public async Task SafeButton_IsPressed_RiskyOneAsksFirst()
    {
        (ResponseEngine engine, FakeScreen screen, var pending) = Create();

        Assert.Equal("Нажала «Сохранить».", await engine.HandleAsync("нажми сохранить"));
        Assert.Equal("Нужно подтверждение: нажать «Удалить».", await engine.HandleAsync("нажми удалить"));
        Assert.Equal(["Сохранить"], screen.Clicked);

        Assert.Equal("Нажала «Удалить».", await pending()!());
        Assert.Equal(["Сохранить", "Удалить"], screen.Clicked);
        Assert.Equal("Не нашла «печать» в активном окне.", await engine.HandleAsync("нажми печать"));
    }

    [Fact]
    public async Task Type_TypesOriginalText()
    {
        (ResponseEngine engine, FakeScreen screen, _) = Create();

        Assert.Equal("Напечатала.", await engine.HandleAsync("напечатай Привет, мир"));
        Assert.Equal("Привет, мир", screen.Typed);
    }
}

/// <summary>Real Windows OCR and UI Automation, CPU only, nothing shown on the user's screen.</summary>
public sealed class WindowsScreenSmokeTests
{
    private static T OnSta<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            throw new Xunit.Sdk.XunitException(error.ToString());
        return result;
    }

    [Fact]
    public async Task Ocr_ReadsRussianTextFromAnImage()
    {
        byte[] png = OnSta(() =>
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 900, 200));
                dc.DrawText(
                    new FormattedText("Ошибка: файл не найден", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 48, Brushes.Black, 1.0),
                    new Point(20, 60));
            }

            var bitmap = new RenderTargetBitmap(900, 200, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        });

        string text = await WindowsScreen.OcrPngAsync(png, CancellationToken.None);

        Assert.Contains("файл не найден", text.ToLowerInvariant());
    }

    [Fact]
    public void Uia_ReadsAWindowsOwnText()
    {
        string text = OnSta(() =>
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Проверка связи EDITH" });
            panel.Children.Add(new Button { Content = "Сохранить изменения" });
            var window = new Window
            {
                Content = panel,
                Left = -10000,
                Top = -10000,
                Width = 300,
                Height = 200,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            window.Show();
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                return WindowsScreen.ReadUia(handle);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Contains("Проверка связи EDITH", text);
        Assert.Contains("Сохранить изменения", text);
    }
}
