using System.IO;

namespace Edith.App;

/// <summary>
/// Two switches the owner flips by voice: the answer window (the answer is also written on the desktop) and
/// quiet mode (nothing is spoken, the answer is only written). Remembered between runs.
/// </summary>
public static class UiModes
{
    private static readonly string File_ = @"C:\EDITH\data\ui-modes.txt";

    static UiModes()
    {
        try
        {
            string[] parts = File.ReadAllText(File_).Split(' ');
            WindowOn = parts[0] != "window=0";
            Quiet = parts.Length > 1 && parts[1] == "quiet=1";
        }
        catch
        {
        }
    }

    public static bool WindowOn { get; private set; } = true;

    public static bool Quiet { get; private set; }

    /// <summary>From the settings window.</summary>
    public static void Set(bool window, bool quiet)
    {
        WindowOn = window || quiet;
        Quiet = quiet;
        Save();
        if (!WindowOn)
            HideRequested?.Invoke();
    }

    public static event Action? ShowRequested;

    public static event Action? HideRequested;

    private static void Save()
    {
        try { File.WriteAllText(File_, $"window={(WindowOn ? 1 : 0)} quiet={(Quiet ? 1 : 0)}"); } catch { }
    }

    /// <summary>Recognises a window / quiet command; <paramref name="reply"/> is what to answer.</summary>
    public static bool TryHandle(string command, out string reply)
    {
        string t = command.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('.', '!', '?');
        reply = "";

        if (t is "убери окно" or "скрой окно" or "спрячь окно" or "закрой окно" or "убери ответ" or "скрой ответ"
            or "уберите окно" or "убери окошко" or "убери картинку" or "скрой картинку" or "убери фото" or "закрой картинку" or "окно" or "окошко")
        {
            HideRequested?.Invoke();
            reply = "Убрала.";
            return true;
        }

        if (t is "покажи окно" or "покажи ответ" or "покажи ответ в окне" or "открой окно")
        {
            ShowRequested?.Invoke();
            reply = "";
            return true;
        }

        if (t is "включи окно" or "включи окно ответов" or "показывай ответы" or "пиши ответы" or "показывай окно")
        {
            WindowOn = true;
            Save();
            reply = "Буду показывать ответы в окне.";
            return true;
        }

        if (t is "выключи окно" or "отключи окно" or "не показывай окно" or "не показывай ответы" or "выключи окно ответов")
        {
            WindowOn = false;
            Quiet = false;
            Save();
            HideRequested?.Invoke();
            reply = "Хорошо, окно не покажу.";
            return true;
        }

        if (t is "тихий режим" or "включи тихий режим" or "режим тишины" or "отвечай текстом" or "отвечай письменно" or "не говори вслух")
        {
            Quiet = true;
            WindowOn = true;
            Save();
            reply = "Тихий режим включён: отвечаю письменно.";
            return true;
        }

        if (t is "выключи тихий режим" or "отключи тихий режим" or "говори вслух" or "включи голос" or "отвечай голосом")
        {
            Quiet = false;
            Save();
            reply = "Снова говорю вслух.";
            return true;
        }

        return false;
    }
}
