using System.IO;

namespace Edith.App;

/// <summary>
/// The librarian's on/off button: the tray menu, a spoken command and (later) a phone bot all flip this one switch.
/// Remembered between runs; off until the owner turns it on.
/// </summary>
public static class LibrarianSwitch
{
    private static readonly string File_ = @"C:\EDITH\data\library\switch.txt";

    public static bool On { get; private set; } = Read();

    public static event Action<bool>? Changed;

    public static void Set(bool on)
    {
        On = on;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, on ? "on" : "off");
        }
        catch
        {
        }

        Changed?.Invoke(on);
    }

    private static bool Read()
    {
        try { return File.ReadAllText(File_).Trim() == "on"; } catch { return false; }
    }

    public static bool TryHandle(string command, out string reply)
    {
        string t = command.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('.', '!', '?');
        reply = "";
        if (t is "включи библиотекаря" or "запусти библиотекаря" or "включи библиотекаря обсидиана" or "библиотекарь включи")
        {
            Set(true);
            reply = "Библиотекарь включён: разберу сессии, когда вы будете заняты другим.";
            return true;
        }

        if (t is "выключи библиотекаря" or "останови библиотекаря" or "отключи библиотекаря" or "библиотекарь выключи")
        {
            Set(false);
            reply = "Библиотекарь выключен.";
            return true;
        }

        if (t is "что с библиотекарем" or "библиотекарь работает" or "работает ли библиотекарь")
        {
            reply = On ? "Библиотекарь включён." : "Библиотекарь выключен.";
            return true;
        }

        return false;
    }
}
