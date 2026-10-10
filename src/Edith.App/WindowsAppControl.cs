using System.Diagnostics;
using System.Runtime.InteropServices;
using Edith.Core.Apps;

namespace Edith.App;

/// <summary>
/// Real window operations. Closing is always the polite WM_CLOSE (Process.CloseMainWindow):
/// the application decides, and may ask to save. EDITH never kills a process from here.
/// </summary>
public sealed class WindowsAppControl : IAppControl
{
    private const int SwMinimize = 6;
    private const int SwRestore = 9;

    public IReadOnlyList<RunningApp> Running()
    {
        Process[] processes = Process.GetProcesses();
        try
        {
            var result = new List<RunningApp>(processes.Length);
            foreach (Process process in processes)
            {
                try
                {
                    result.Add(new RunningApp(process.Id, process.ProcessName, process.MainWindowHandle != IntPtr.Zero));
                }
                catch (InvalidOperationException)
                {
                    // Exited while we were looking.
                }
            }

            return result;
        }
        finally
        {
            foreach (Process process in processes)
                process.Dispose();
        }
    }

    public bool RequestClose(int processId) => WithProcess(processId, p => p.CloseMainWindow());

    public bool Focus(int processId) => WithProcess(processId, p =>
    {
        IntPtr window = p.MainWindowHandle;
        if (window == IntPtr.Zero)
            return false;

        if (IsIconic(window))
            ShowWindow(window, SwRestore);

        return SetForegroundWindow(window);
    });

    public bool Minimize(int processId) => WithProcess(processId, p =>
        p.MainWindowHandle != IntPtr.Zero && ShowWindow(p.MainWindowHandle, SwMinimize));

    public void Launch(string target)
    {
        using Process? _ = Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true,
        });
    }

    private static bool WithProcess(int processId, Func<Process, bool> action)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return action(process);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);
}
