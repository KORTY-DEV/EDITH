using System.Diagnostics;
using System.Runtime.InteropServices;
using Edith.Core.Brain;

namespace Edith.App;

/// <summary>
/// Windows probes for the game-mode detector. Nothing here runs on its own: the monitor
/// evaluates lazily (at most once per poll interval) when the brain is asked something.
/// </summary>
public static class GameMode
{
    // EDITH_GAME_MODE=1 / 0 forces the state (diagnostics and tests); unset = automatic.
    public static GameModeMonitor Monitor { get; } = CreateMonitor();

    private static GameModeMonitor CreateMonitor()
    {
        var monitor =
            new GameModeMonitor(
                ListProcessNames,
                IsFullscreenAppRunning);

        string? forced =
            Environment.GetEnvironmentVariable("EDITH_GAME_MODE");

        if (forced == "1")
        {
            monitor.Force(true);
        }
        else if (forced == "0")
        {
            monitor.Force(false);
        }

        return monitor;
    }

    private static IEnumerable<string> ListProcessNames()
    {
        Process[] processes = Process.GetProcesses();

        try
        {
            return processes
                .Select(p => p.ProcessName)
                .ToArray();
        }
        finally
        {
            foreach (Process p in processes)
            {
                p.Dispose();
            }
        }
    }

    // SHQueryUserNotificationState is the shell's own "is the user in a full-screen app"
    // answer (the one Slack/Teams use to stay quiet during games and presentations).
    private const int QunsBusy = 2;
    private const int QunsRunningD3dFullScreen = 3;
    private const int QunsPresentationMode = 4;

    private static bool IsFullscreenAppRunning()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 &&
                   state is QunsBusy or QunsRunningD3dFullScreen or QunsPresentationMode;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
