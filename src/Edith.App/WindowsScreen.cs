using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Edith.Core.Screen;

namespace Edith.App;

/// <summary>
/// The foreground window, read the precise way first (UI Automation: the app's own text),
/// then from pixels (Windows OCR, ru+en) when the app exposes little (games, images, video).
/// Everything runs on the CPU and nothing leaves the PC.
/// </summary>
public sealed class WindowsScreen : IScreen
{
    private const int MaxUiaNodes = 400;
    private static readonly TimeSpan UiaBudget = TimeSpan.FromSeconds(2);
    private const int MinUiaChars = 40;

    private static readonly ControlType[] Clickable =
    [
        ControlType.Button, ControlType.MenuItem, ControlType.Hyperlink, ControlType.TabItem,
        ControlType.ListItem, ControlType.CheckBox, ControlType.RadioButton, ControlType.SplitButton,
        ControlType.TreeItem,
    ];

    public async Task<ScreenContent?> ReadForegroundAsync(CancellationToken cancellationToken)
    {
        IntPtr window = ForegroundWindow(out string app, out string title);
        if (window == IntPtr.Zero)
            return null;

        string uiaText = await Task.Run(() => ReadUia(window), cancellationToken);
        if (uiaText.Length >= MinUiaChars)
            return new ScreenContent(app, title, uiaText, "uia");

        string ocrText = await ReadOcrAsync(window, cancellationToken);
        return ocrText.Length > uiaText.Length
            ? new ScreenContent(app, title, ocrText, "ocr")
            : new ScreenContent(app, title, uiaText, "uia");
    }

    public IReadOnlyList<UiElementInfo> Clickables()
    {
        IntPtr window = ForegroundWindow(out _, out _);
        if (window == IntPtr.Zero)
            return [];

        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            var condition = new OrCondition(Clickable.Select(t => (Condition)new PropertyCondition(AutomationElement.ControlTypeProperty, t)).ToArray());
            return root.FindAll(TreeScope.Descendants, condition)
                .Cast<AutomationElement>()
                .Take(MaxUiaNodes)
                .Select(e => new UiElementInfo(SafeName(e), e.Current.ControlType.ProgrammaticName.Replace("ControlType.", "")))
                .Where(e => e.Name.Length > 0)
                .ToList();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return [];
        }
    }

    public bool Click(UiElementInfo element)
    {
        IntPtr window = ForegroundWindow(out _, out _);
        if (window == IntPtr.Zero)
            return false;

        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            AutomationElement? target = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, element.Name));
            if (target is null)
                return false;

            if (target.TryGetCurrentPattern(InvokePattern.Pattern, out object invoke))
                ((InvokePattern)invoke).Invoke();
            else if (target.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object select))
                ((SelectionItemPattern)select).Select();
            else if (target.TryGetCurrentPattern(TogglePattern.Pattern, out object toggle))
                ((TogglePattern)toggle).Toggle();
            else if (target.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object expand))
                ((ExpandCollapsePattern)expand).Expand();
            else
                return false;

            return true;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException or ElementNotEnabledException)
        {
            return false;
        }
    }

    public void Type(string text)
    {
        var inputs = new List<Input>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(Unicode(c, keyUp: false));
            inputs.Add(Unicode(c, keyUp: true));
        }

        if (inputs.Count > 0)
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<Input>());
    }

    // ------------------------------------------------------------------ foreground window

    private static IntPtr ForegroundWindow(out string app, out string title)
    {
        app = title = "";
        IntPtr window = GetForegroundWindow();
        if (window == IntPtr.Zero)
            return IntPtr.Zero;

        GetWindowThreadProcessId(window, out uint pid);
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            app = process.ProcessName;

            // EDITH's own status panel is not what the user means by "the screen".
            if (pid == (uint)Environment.ProcessId)
                return IntPtr.Zero;
        }
        catch (ArgumentException)
        {
        }

        var buffer = new StringBuilder(512);
        GetWindowText(window, buffer, buffer.Capacity);
        title = buffer.ToString();
        return window;
    }

    // ------------------------------------------------------------------ UI Automation

    public static string ReadUia(IntPtr window)
    {
        try
        {
            AutomationElement root = AutomationElement.FromHandle(window);
            var parts = new List<string>();
            var queue = new Queue<AutomationElement>();
            queue.Enqueue(root);
            TreeWalker walker = TreeWalker.ControlViewWalker;
            Stopwatch clock = Stopwatch.StartNew();
            int visited = 0;

            while (queue.Count > 0 && visited < MaxUiaNodes && clock.Elapsed < UiaBudget)
            {
                AutomationElement element = queue.Dequeue();
                visited++;

                string name = SafeName(element);
                if (name.Length > 0 && element != root)
                    parts.Add(name);

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out object value))
                {
                    string text = ((ValuePattern)value).Current.Value ?? "";
                    if (text.Length > 0)
                        parts.Add(text.Length > 1500 ? text[..1500] : text);
                }
                else if (element.TryGetCurrentPattern(TextPattern.Pattern, out object textPattern))
                {
                    string text = ((TextPattern)textPattern).DocumentRange.GetText(2000) ?? "";
                    if (text.Length > 0)
                        parts.Add(text);
                }

                for (AutomationElement? child = walker.GetFirstChild(element);
                     child is not null && queue.Count < MaxUiaNodes;
                     child = walker.GetNextSibling(child))
                {
                    queue.Enqueue(child);
                }
            }

            return UiTargets.JoinText(parts);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            return "";
        }
    }

    private static string SafeName(AutomationElement element)
    {
        try
        {
            return (element.Current.Name ?? "").Trim();
        }
        catch
        {
            return "";
        }
    }

    // ------------------------------------------------------------------ OCR

    private static async Task<string> ReadOcrAsync(IntPtr window, CancellationToken cancellationToken)
    {
        try
        {
            byte[]? png = CaptureWindowPng(window);
            if (png is null)
                return "";

            return await OcrPngAsync(png, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            VoiceDiagnostics.WriteRaw("screen-ocr-error.txt", ex.ToString());
            return "";
        }
    }

    /// <summary>Windows OCR in Russian (English as a fallback language) over a PNG image.</summary>
    public static async Task<string> OcrPngAsync(byte[] png, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream(png);
        using Windows.Storage.Streams.IRandomAccessStream stream = memory.AsRandomAccessStream();
        Windows.Graphics.Imaging.BitmapDecoder decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        using Windows.Graphics.Imaging.SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);

        Windows.Media.Ocr.OcrEngine? engine =
            Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ru")) ??
            Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            return "";

        cancellationToken.ThrowIfCancellationRequested();
        Windows.Media.Ocr.OcrResult result = await engine.RecognizeAsync(bitmap);
        return UiTargets.JoinText(result.Lines.Select(l => l.Text));
    }

    /// <summary>The foreground window as a JPEG (longest side at most <paramref name="maxSide"/> px), or null.</summary>
    public static byte[]? CaptureForegroundJpeg(int maxSide = 1600, int quality = 75)
    {
        IntPtr window = GetForegroundWindow();
        byte[]? png = window == IntPtr.Zero ? null : CaptureWindowPng(window);
        if (png is null)
            return null;

        using var input = new MemoryStream(png);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0)
            frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>What to look at: the monitor under the mouse, the other monitor(s), all of them, or the foreground window.</summary>
    public enum ScreenTarget { Cursor, Other, All, Window }

    /// <summary>One monitor: where it is and what to call it aloud.</summary>
    public sealed record MonitorInfo(int Left, int Top, int Width, int Height, bool Primary, string Name);

    /// <summary>Monitors from left to right, named for speech ("левый монитор", "правый монитор", "монитор 3").</summary>
    public static IReadOnlyList<MonitorInfo> Monitors()
    {
        var found = new List<(Rect Rect, bool Primary)>();
        MonitorEnumProc callback = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            found.Add((rect, GetMonitorInfo(monitor, ref info) && (info.Flags & 1) != 0));
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        GC.KeepAlive(callback);

        var ordered = found.OrderBy(m => m.Rect.Left).ThenBy(m => m.Rect.Top).ToList();
        return ordered.Select((m, i) => new MonitorInfo(m.Rect.Left, m.Rect.Top, m.Rect.Right - m.Rect.Left, m.Rect.Bottom - m.Rect.Top, m.Primary,
            ordered.Count == 1 ? "экран"
            : ordered.Count == 2 ? (i == 0 ? "левый монитор" : "правый монитор")
            : $"монитор {i + 1}")).ToList();
    }

    /// <summary>The monitor the mouse pointer is on (where the owner is working), or the primary one.</summary>
    public static MonitorInfo? CursorMonitor()
    {
        IReadOnlyList<MonitorInfo> all = Monitors();
        if (!GetCursorPos(out Point point))
            return all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
        return all.FirstOrDefault(m => point.X >= m.Left && point.X < m.Left + m.Width && point.Y >= m.Top && point.Y < m.Top + m.Height)
               ?? all.FirstOrDefault(m => m.Primary) ?? all.FirstOrDefault();
    }

    /// <summary>Whole-monitor picture(s) as JPEG with a spoken name, for the picture-describing model.</summary>
    public static IReadOnlyList<(string Name, byte[] Png, byte[] Jpeg)> CaptureMonitors(ScreenTarget target, string region = "")
    {
        IReadOnlyList<MonitorInfo> all = Monitors();
        MonitorInfo? mine = CursorMonitor();
        IEnumerable<MonitorInfo> chosen = target switch
        {
            ScreenTarget.All => all,
            ScreenTarget.Other => all.Where(m => m != mine),
            _ => mine is null ? [] : [mine],
        };

        var result = new List<(string, byte[], byte[])>();
        foreach (MonitorInfo monitor in chosen)
        {
            byte[]? png = CaptureRectPng(monitor.Left, monitor.Top, monitor.Width, monitor.Height);
            if (png is null)
                continue;

            // A part of the screen is cut at full resolution first: small print on a dense page becomes readable.
            png = Crop(png, region);
            result.Add((region.Length > 0 ? monitor.Name + " (" + RegionName(region) + ")" : monitor.Name, png, ToJpeg(png, 2000, 85)));
        }

        return result;
    }

    private static string RegionName(string region) => region switch
    {
        "left" => "левая половина", "right" => "правая половина", "top" => "верхняя половина", "bottom" => "нижняя половина", "center" => "центр",
        "top-left" => "верхний левый угол", "top-right" => "верхний правый угол", "bottom-left" => "нижний левый угол", "bottom-right" => "нижний правый угол",
        _ => region,
    };

    /// <summary>"left", "bottom-right", "center"… → that part of the picture, as PNG; unknown or empty → the whole picture.</summary>
    public static byte[] Crop(byte[] png, string region)
    {
        (double x, double y, double w, double h)? part = region switch
        {
            "left" => (0, 0, 0.5, 1), "right" => (0.5, 0, 0.5, 1), "top" => (0, 0, 1, 0.5), "bottom" => (0, 0.5, 1, 0.5),
            "center" => (0.2, 0.2, 0.6, 0.6),
            "top-left" => (0, 0, 0.5, 0.5), "top-right" => (0.5, 0, 0.5, 0.5), "bottom-left" => (0, 0.5, 0.5, 0.5), "bottom-right" => (0.5, 0.5, 0.5, 0.5),
            _ => null,
        };
        if (part is null)
            return png;

        using var input = new MemoryStream(png);
        BitmapSource frame = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var rect = new System.Windows.Int32Rect(
            (int)(frame.PixelWidth * part.Value.x), (int)(frame.PixelHeight * part.Value.y),
            (int)(frame.PixelWidth * part.Value.w), (int)(frame.PixelHeight * part.Value.h));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(frame, rect)));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    /// <summary>The visible windows on a monitor, topmost first, as "program — title": exact names for the model, free of guessing.</summary>
    public static IReadOnlyList<string> WindowsOn(MonitorInfo monitor, int limit = 6)
    {
        var found = new List<string>();
        uint self = (uint)Environment.ProcessId;
        EnumWindows((hWnd, _) =>
        {
            if (found.Count >= limit || !IsWindowVisible(hWnd) || IsIconic(hWnd))
                return true;

            var title = new StringBuilder(512);
            GetWindowText(hWnd, title, title.Capacity);
            if (title.Length == 0 || !GetWindowRect(hWnd, out Rect r))
                return true;

            int cloaked = 0;
            DwmGetWindowAttribute(hWnd, 14 /* DWMWA_CLOAKED */, out cloaked, sizeof(int));
            if (cloaked != 0)
                return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid == self)
                return true;

            int overlapW = Math.Min(r.Right, monitor.Left + monitor.Width) - Math.Max(r.Left, monitor.Left);
            int overlapH = Math.Min(r.Bottom, monitor.Top + monitor.Height) - Math.Max(r.Top, monitor.Top);
            if (overlapW <= 0 || overlapH <= 0 || (long)overlapW * overlapH < (long)monitor.Width * monitor.Height / 8)
                return true; // a sliver or a tool window is not "what is on the screen"

            string program = "";
            try { program = Process.GetProcessById((int)pid).ProcessName; } catch { }
            string name = title.ToString();
            if (name is "Program Manager" or "Settings" || program is "TextInputHost" or "ShellExperienceHost" or "SearchHost")
                return true;

            found.Add(program.Length > 0 ? $"{program} — «{(name.Length > 120 ? name[..120] + "…" : name)}»" : $"«{name}»");
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static byte[] ToJpeg(byte[] png, int maxSide, int quality)
    {
        using var input = new MemoryStream(png);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = decoder.Frames[0];
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(frame.PixelWidth, frame.PixelHeight));
        if (scale < 1.0)
            frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));

        var encoder = new JpegBitmapEncoder { QualityLevel = quality };
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private static byte[]? CaptureWindowPng(IntPtr window)
    {
        if (!GetWindowRect(window, out Rect rect))
            return null;

        return CaptureRectPng(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));
    }

    private static byte[]? CaptureRectPng(int left, int top, int width, int height)
    {
        Rect rect = new() { Left = left, Top = top, Right = left + width, Bottom = top + height };
        width = Math.Max(0, width);
        height = Math.Max(0, height);
        if (width < 16 || height < 16)
            return null;

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screen);
        IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
        IntPtr old = SelectObject(memoryDc, bitmap);
        try
        {
            BitBlt(memoryDc, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020 /* SRCCOPY */);
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        finally
        {
            SelectObject(memoryDc, old);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    // ------------------------------------------------------------------ native

    private static Input Unicode(char c, bool keyUp) => new()
    {
        Type = 1, // INPUT_KEYBOARD
        Keyboard = new KeyboardInput { Scan = c, Flags = 0x0004 /* KEYEVENTF_UNICODE */ | (keyUp ? 0x0002u : 0u) },
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Keyboard;
        [FieldOffset(8)] private MouseInputPadding _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPadding
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr data);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
}
