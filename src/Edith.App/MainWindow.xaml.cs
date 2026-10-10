using Edith.Audio;
using Edith.Core;
using Edith.Core.Runtime;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Edith.App;

public partial class MainWindow : Window
{
    private const int WM_HOTKEY = 0x0312;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int HOTKEY_TOGGLE = 1001;
    private const int HOTKEY_EXIT_DEV = 1002;
    private const int HOTKEY_STATUS = 1003;
    private const int HOTKEY_TEXT = 1004;
    private const uint VK_T = 0x54;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    // E
    private const uint VK_E = 0x45;

    // J: journal / status panel
    private const uint VK_J = 0x4A;

    private IntPtr _handle;
    private HwndSource? _source;

    private AssistantState _state =
        AssistantState.Sleeping;

    public MainWindow()
    {
        InitializeComponent();
    }

    public event Action? ManualActivationRequested;
    public event Action? ManualDeactivationRequested;
    public event Action? StatusPanelRequested;
    public event Action? TextBarRequested;
    public event Action<AssistantState>? AssistantStateChanged;

    public void InitializeBackground()
    {
        var helper =
            new WindowInteropHelper(this);

        _handle =
            helper.EnsureHandle();

        _source =
            HwndSource.FromHwnd(_handle);

        _source?.AddHook(WndProc);

        // The orb is a picture, never a target: clicks and focus pass through it.
        int exStyle = GetWindowLong(_handle, GWL_EXSTYLE);
        SetWindowLong(_handle, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        if (!RegisterHotKey(
                _handle,
                HOTKEY_TOGGLE,
                MOD_CONTROL |
                MOD_ALT |
                MOD_NOREPEAT,
                VK_E))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Не удалось зарегистрировать Ctrl+Alt+E.");
        }

        RegisterHotKey(
            _handle,
            HOTKEY_EXIT_DEV,
            MOD_CONTROL |
            MOD_ALT |
            MOD_SHIFT |
            MOD_NOREPEAT,
            VK_E);

        // Optional: another app may own Ctrl+Alt+J; EDITH works without the panel hotkey.
        RegisterHotKey(
            _handle,
            HOTKEY_STATUS,
            MOD_CONTROL |
            MOD_ALT |
            MOD_NOREPEAT,
            VK_J);

        // Quiet mode: type instead of speaking.
        RegisterHotKey(_handle, HOTKEY_TEXT, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_T);

        SetState(
            AssistantState.Sleeping,
            "Sleeping",
            0);
    }

    protected override void OnClosed(
        EventArgs e)
    {
        if (_handle != IntPtr.Zero)
        {
            UnregisterHotKey(
                _handle,
                HOTKEY_TOGGLE);

            UnregisterHotKey(
                _handle,
                HOTKEY_EXIT_DEV);

            UnregisterHotKey(
                _handle,
                HOTKEY_STATUS);

            UnregisterHotKey(
                _handle,
                HOTKEY_TEXT);
        }

        _source?.RemoveHook(
            WndProc);

        base.OnClosed(e);
    }

    private IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg != WM_HOTKEY)
            return IntPtr.Zero;

        int id =
            wParam.ToInt32();

        if (id == HOTKEY_TOGGLE)
        {
            if (IsVisible)
            {
                ManualDeactivationRequested?.Invoke();
            }
            else
            {
                ManualActivationRequested?.Invoke();
            }

            handled = true;
        }
        else if (id == HOTKEY_STATUS)
        {
            StatusPanelRequested?.Invoke();

            handled = true;
        }
        else if (id == HOTKEY_TEXT)
        {
            TextBarRequested?.Invoke();

            handled = true;
        }
        else if (id == HOTKEY_EXIT_DEV)
        {
            Application.Current.Shutdown();

            handled = true;
        }

        return IntPtr.Zero;
    }

    private void PositionTopRight()
    {
        var area =
            SystemParameters.WorkArea;

        Left =
            area.Right -
            Width -
            22;

        Top =
            area.Top +
            18;
    }

    public void SetState(
        AssistantState state,
        string? backendState = null,
        long session = 0)
    {
        bool changed = _state != state;
        _state = state;

        if (changed)
        {
            AssistantStateChanged?.Invoke(state);
            EdithEvents.Publish(
                EventKinds.StateChanged,
                state.ToString(),
                session,
                new Dictionary<string, string> { ["backend"] = backendState ?? "" });
        }

        if (!AssistantStateProjection.OrbVisible(state))
        {
            WriteUiStateLog(
                backendState ?? "Sleeping",
                state,
                IsVisible,
                session);

            // The orb shrinks away, then the window goes. Visibility is part of the state
            // projection: no caller may leave a stale orb on screen after returning to idle,
            // so a state that needs the orb again cancels the pending hide.
            if (IsVisible)
            {
                OrbView.Close(() =>
                {
                    if (AssistantStateProjection.OrbVisible(_state))
                        return;

                    OrbView.Stop();
                    Hide();
                });
            }

            return;
        }

        bool appearing = !IsVisible;
        if (appearing)
        {
            PositionTopRight();
            Opacity = 1;
            Show();
            OrbView.Start();
        }

        OrbView.SetState(state);
        if (appearing)
            OrbView.Burst();

        WriteUiStateLog(
            backendState ?? state.ToString(),
            state,
            IsVisible,
            session);
    }

    /// <summary>Microphone loudness for the listening orb (any thread).</summary>
    public void SetMicrophoneLevel(float level) => OrbView.Level = level;

    /// <summary>Loudness of her own voice for the speaking orb (any thread).</summary>
    public void SetOutputLevel(float level) => OrbView.OutputLevel = level;

    private static void WriteUiStateLog(
        string backendState,
        AssistantState uiState,
        bool orbVisible,
        long session)
    {
        try
        {
            Directory.CreateDirectory(@"C:\EDITH\data\diagnostics");
            File.AppendAllText(
                @"C:\EDITH\data\diagnostics\ui-state.txt",
                $"timestamp={DateTimeOffset.Now:O}; backend-state={backendState}; ui-state={uiState}; orb-visible={orbVisible}; session={session}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool RegisterHotKey(
        IntPtr hWnd,
        int id,
        uint fsModifiers,
        uint vk);

    [DllImport(
        "user32.dll",
        SetLastError = true)]
    private static extern bool UnregisterHotKey(
        IntPtr hWnd,
        int id);
}



