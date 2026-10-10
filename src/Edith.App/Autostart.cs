using System.IO;

namespace Edith.App;

/// <summary>EDITH starting with Windows: a shortcut in the user's Startup folder (no registry, no admin).</summary>
public static class Autostart
{
    private static string LinkPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "EDITH.lnk");

    public static bool IsEnabled => File.Exists(LinkPath);

    public static void Set(bool enabled)
    {
        if (!enabled)
        {
            if (File.Exists(LinkPath))
                File.Delete(LinkPath);
            return;
        }

        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Не знаю, где лежит EDITH.");

        Type shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell недоступен.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic link = shell.CreateShortcut(LinkPath);
        link.TargetPath = exe;
        link.WorkingDirectory = Path.GetDirectoryName(exe)!;
        link.IconLocation = Path.Combine(Path.GetDirectoryName(exe)!, "edith.ico") + ",0";
        link.Description = "E.D.I.T.H. — голосовой помощник";
        link.Save();
    }
}
