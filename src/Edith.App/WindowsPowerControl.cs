using System.Diagnostics;
using Edith.Core.Power;

namespace Edith.App;

/// <summary>shutdown.exe with a grace period: Windows shows its own warning and "shutdown /a" aborts.</summary>
public sealed class WindowsPowerControl : IPowerControl
{
    public void ShutdownWithGrace(TimeSpan grace, string reason)
    {
        int seconds = Math.Clamp((int)grace.TotalSeconds, 30, 600);
        Run("/s", "/t", seconds.ToString(), "/c", reason.Replace('"', '\''));
    }

    public void AbortShutdown() => Run("/a");

    private static void Run(params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "shutdown.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);

        using Process? process = Process.Start(start);
        process?.WaitForExit(5000);
    }
}
