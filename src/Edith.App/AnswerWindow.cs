using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Edith.App;

/// <summary>
/// What EDITH answers, written on the desktop: a nearly transparent glass card that appears with the answer, types it
/// out, hangs for a few seconds and fades. It can show a picture under the text. It never takes focus and nothing is
/// typed into it: the owner asks aloud. Hold and drag to move it, pull the corner to resize it (place and size are
/// remembered); hovering keeps it open.
/// </summary>
public sealed class AnswerWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private static readonly string PositionFile = @"C:\EDITH\data\answer-window.pos";

    private readonly TextBlock _question = new();
    private readonly TextBlock _answer = new();
    private readonly TextBlock _caption = new();
    private readonly Image _image = new();
    private readonly Border _imageFrame = new();
    private readonly DispatcherTimer _typewriter = new() { Interval = TimeSpan.FromMilliseconds(12) };
    private readonly DispatcherTimer _hold = new();
    private string _full = "";
    private int _shown;
    private string _lastQuestion = "", _lastAnswer = "";
    private DateTime _imageStamp = DateTime.MinValue;
    private bool _hasImage;

    /// <summary>How long the finished answer stays (seconds); quiet mode needs longer: it is the only reply.</summary>
    public Func<double> HoldSeconds { get; set; } = () => 5;

    /// <summary>While she is still speaking the answer stays, however long the writing took.</summary>
    public Func<bool> StillSpeaking { get; set; } = () => false;

    public AnswerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Theme.Apply(this);
        Width = 416;
        SizeToContent = SizeToContent.Height;
        MinWidth = 240;
        MinHeight = 90;
        MaxHeight = 520;
        Opacity = 0;

        var text = new SolidColorBrush(Color.FromRgb(232, 238, 247));
        var muted = new SolidColorBrush(Color.FromArgb(150, 170, 182, 200));

        _question.Foreground = muted;
        _question.FontSize = 11.5;
        _question.TextTrimming = TextTrimming.CharacterEllipsis;
        _question.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _answer.Foreground = text;
        _answer.FontSize = 14;
        _answer.LineHeight = 21;
        _answer.TextWrapping = TextWrapping.Wrap;
        _answer.Margin = new Thickness(0, 6, 0, 0);
        _answer.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _image.Stretch = Stretch.Uniform;
        _image.StretchDirection = StretchDirection.DownOnly;
        _image.MaxHeight = 520;
        _caption.Foreground = muted;
        _caption.FontSize = 11.5;
        _caption.TextWrapping = TextWrapping.Wrap;
        _caption.Margin = new Thickness(0, 4, 0, 0);
        _imageFrame.CornerRadius = new CornerRadius(8);
        _imageFrame.Margin = new Thickness(0, 10, 0, 0);
        _imageFrame.Visibility = Visibility.Collapsed;
        _imageFrame.ClipToBounds = true;
        var imageStack = new StackPanel();
        imageStack.Children.Add(_image);
        imageStack.Children.Add(_caption);
        _imageFrame.Child = imageStack;

        var body = new StackPanel();
        body.Children.Add(_answer);
        body.Children.Add(_imageFrame);

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(_question, 0);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = body, Margin = new Thickness(0, 0, 0, 6) };
        ScrollViewer scroll = _scroll;
        ApplyMaxHeight(520);
        Grid.SetRow(scroll, 1);
        grid.Children.Add(_question);
        grid.Children.Add(scroll);

        // The corner to pull: the window is resized by hand (it has no frame to grab).
        var grip = new Thumb
        {
            Width = 16,
            Height = 16,
            Cursor = Cursors.SizeNWSE,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -10, -8),
            Background = Brushes.Transparent,
            Template = GripTemplate(),
            Opacity = 0.55,
        };
        grip.DragDelta += (_, e) =>
        {
            Width = Math.Max(MinWidth, Width + e.HorizontalChange);
            ApplyMaxHeight(Math.Clamp(Math.Min(ActualHeight, MaxHeight) + e.VerticalChange, MinHeight, 1000));
        };
        grip.DragCompleted += (_, _) => SavePosition();
        var root = new Grid();
        root.Children.Add(grid);
        root.Children.Add(grip);

        // Glass: dark, mostly see-through, a soft cyan edge.
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(138, 10, 13, 20)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(64, 85, 223, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 11, 18, 13),
            Child = root,
        };

        _typewriter.Tick += (_, _) =>
        {
            _shown = Math.Min(_full.Length, _shown + 2);
            _answer.Text = _full[.._shown];
            if (_shown >= _full.Length)
            {
                _typewriter.Stop();
                StartHold();
            }
        };
        _hold.Tick += (_, _) =>
        {
            if (StillSpeaking())
            {
                _hold.Interval = TimeSpan.FromSeconds(2.5); // she is still talking: hang on, then a few seconds more
                return;
            }

            FadeOut();
        };

        MouseEnter += (_, _) => _hold.Stop();
        MouseLeave += (_, _) => { if (!_typewriter.IsEnabled && IsVisible) StartHold(); };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || e.OriginalSource is Thumb)
                return;
            try { DragMove(); } catch { }
            SavePosition();
        };

        SourceInitialized += (_, _) =>
        {
            IntPtr handle = new WindowInteropHelper(this).Handle;
            SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
    }

    private static ControlTemplate GripTemplate()
    {
        var factory = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        factory.SetValue(System.Windows.Shapes.Path.DataProperty, Geometry.Parse("M 14,4 L 4,14 M 14,9 L 9,14"));
        factory.SetValue(System.Windows.Shapes.Shape.StrokeProperty, new SolidColorBrush(Color.FromRgb(170, 190, 215)));
        factory.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.5);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = factory };
    }

    public bool HasAnswer => _lastAnswer.Length > 0 || _hasImage;

    /// <summary>Shows the answer and types it out; the window fades by itself a few seconds after the last letter.</summary>
    public void ShowAnswer(string question, string answer)
    {
        _lastQuestion = question;
        _lastAnswer = answer;
        _question.Text = question;
        _full = answer;
        _shown = 0;
        _answer.Text = "";

        // A picture that came with this question stays; an old one goes.
        if (DateTime.Now - _imageStamp > TimeSpan.FromSeconds(45))
            ClearImage();

        Reveal();
        _typewriter.Start();
    }

    /// <summary>A picture under the text (shown at once, also when no answer text follows).</summary>
    public void ShowImage(byte[] bytes, string caption)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
            }

            bitmap.Freeze();
            _image.Source = bitmap;
            _caption.Text = caption;
            _caption.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _imageFrame.Visibility = Visibility.Visible;
            _hasImage = true;
            _imageStamp = DateTime.Now;

            // A picture needs room: allow a taller card (never smaller than the owner set).
            ApplyMaxHeight(Math.Max(MaxHeight, 520));
            if (!IsVisible || _full.Length == 0)
            {
                _question.Text = _lastQuestion;
                Reveal();
                StartHold();
            }
        }
        catch
        {
            // Not an image after all: the answer is still there.
        }
    }

    private void ClearImage()
    {
        _image.Source = null;
        _imageFrame.Visibility = Visibility.Collapsed;
        _hasImage = false;
    }

    private void Reveal()
    {
        _hold.Stop();
        Place();
        BeginAnimation(OpacityProperty, null);
        if (!IsVisible)
            Show();
        BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(180)));
    }

    /// <summary>"Покажи окно": the last answer again, whole.</summary>
    public bool ShowLast()
    {
        if (!HasAnswer)
            return false;

        _imageStamp = DateTime.Now; // keep the picture of that answer
        ShowAnswer(_lastQuestion, _lastAnswer);
        return true;
    }

    public void HideNow()
    {
        _typewriter.Stop();
        _hold.Stop();
        FadeOut();
    }

    private void StartHold()
    {
        _hold.Stop();
        double seconds = Math.Max(2, HoldSeconds()) + (_hasImage ? 8 : 0);
        _hold.Interval = TimeSpan.FromSeconds(seconds);
        _hold.Start();
    }

    private void FadeOut()
    {
        _hold.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
                Hide();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    private void Place()
    {
        Rect area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.16;
        if (LoadPlace() is { } saved && saved.X >= area.Left - 100 && saved.X < area.Right - 100 && saved.Y >= area.Top - 20 && saved.Y < area.Bottom - 60)
        {
            Left = saved.X;
            Top = saved.Y;
            if (saved.W >= MinWidth && saved.H >= MinHeight && !_sizeApplied)
            {
                Width = saved.W;
                ApplyMaxHeight(saved.H);
                _sizeApplied = true;
            }
        }
    }

    private bool _sizeApplied;
    private readonly ScrollViewer _scroll;

    /// <summary>The card fits its text; this is how tall it may grow before the text scrolls.</summary>
    private void ApplyMaxHeight(double value)
    {
        MaxHeight = value;
        _scroll.MaxHeight = Math.Max(40, value - 62);
    }

    private void SavePosition()
    {
        try { File.WriteAllText(PositionFile, $"{(int)Left} {(int)Top} {(int)Width} {(int)MaxHeight}"); } catch { }
    }

    private static (double X, double Y, double W, double H)? LoadPlace()
    {
        try
        {
            string[] p = File.ReadAllText(PositionFile).Split(' ');
            return (double.Parse(p[0]), double.Parse(p[1]), p.Length > 3 ? double.Parse(p[2]) : 0, p.Length > 3 ? double.Parse(p[3]) : 0);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);
}
