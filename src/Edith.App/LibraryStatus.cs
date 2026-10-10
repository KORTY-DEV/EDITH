namespace Edith.App;

/// <summary>What the librarian is doing right now, for the status panel.</summary>
public static class LibraryStatus
{
    public static string State { get; private set; } = "выключен";

    public static string Current { get; private set; } = "";

    public static string LastNote { get; private set; } = "";

    public static DateTimeOffset? LastNoteAt { get; private set; }

    public static event Action? Changed;

    public static void Waiting(string reason) => Set(LibrarianSwitch.On ? "ждёт: " + reason : "выключен", "");

    public static void Idle() => Set(LibrarianSwitch.On ? "включён, очередь пуста" : "выключен", "");

    public static void Progress(string stage, int part, int parts, string what)
    {
        switch (stage)
        {
            case "part":
                Set("работает", $"{what}: часть {part} из {parts}");
                break;
            case "header":
                Set("работает", $"{what}: заголовок и краткое содержание");
                break;
            case "picture":
                Set("работает", $"картина проекта {what}");
                break;
            case "done":
                LastNote = what;
                LastNoteAt = DateTimeOffset.Now;
                Set("работает", "");
                break;
        }
    }

    private static void Set(string state, string current)
    {
        if (State == state && Current == current)
            return;
        State = state;
        Current = current;
        Changed?.Invoke();
    }
}
