namespace Edith.Core.Runtime;

/// <summary>
/// Turns journal events into a short spoken answer to "что ты делала?".
/// Only real actions count (tools, permissions, deferred tasks); chatter is skipped.
/// </summary>
public static class ActivityReport
{
    private static readonly string[] ActivityQuestions =
    [
        "что ты делала",
        "что ты сделала",
        "что ты успела сделать",
        "чем ты занималась",
        "что ты натворила",
        "какие действия ты выполнила",
        "что ты выполнила",
    ];

    /// <summary>Normalized (lowercase, ё→е) command asks what EDITH did.</summary>
    public static bool IsActivityQuery(string command)
    {
        string text = command.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('?', '.', '!');
        return ActivityQuestions.Any(q => text == q || text.StartsWith(q + " ", StringComparison.Ordinal));
    }

    public static string Summarize(IEnumerable<EdithEvent> events, int maxItems = 3)
    {
        List<string> lines = events
            .Where(evt => EventKinds.IsAction(evt.Kind))
            .Where(evt => evt.Kind is not (EventKinds.ToolStarted or EventKinds.PermissionApproved))
            .Select(Describe)
            .Where(line => line.Length > 0)
            .ToList();

        if (lines.Count == 0)
            return "Пока ничего не делала, только разговаривала.";

        IEnumerable<string> last = lines.TakeLast(Math.Max(1, maxItems));
        string prefix = lines.Count > maxItems ? $"Последние {maxItems} действия: " : "";
        return prefix + string.Join(" ", last);
    }

    private static string Describe(EdithEvent evt)
    {
        string tool = evt.Get("tool") ?? "";
        string what = evt.Summary.Trim().TrimEnd('.');
        string app = evt.Get("app") is { Length: > 0 } name ? ": " + name : "";

        return evt.Kind switch
        {
            EventKinds.ToolFinished => $"{Capitalize(ToolTitle(tool, what))}{app}.",
            EventKinds.ToolFailed => $"Не получилось: {ToolTitle(tool, what)}{app}.",
            EventKinds.PermissionRequested => $"Спросила разрешения: {what}.",
            EventKinds.PermissionExecuted => $"С твоего разрешения выполнила: {what}.",
            EventKinds.PermissionDenied => $"Отменила по твоему слову: {what}.",
            EventKinds.PermissionExpired => $"Не дождалась подтверждения: {what}.",
            EventKinds.PermissionFailed => $"Не смогла выполнить: {what}.",
            EventKinds.TaskDeferred => $"Отложила на потом: {what}.",
            EventKinds.TaskResumed => $"Вернулась к отложенному: {what}.",
            EventKinds.PowerScheduled => $"Запланировала выключение. {what}.",
            EventKinds.PowerCancelled => "Отменила выключение.",
            EventKinds.PowerIssued => "Запустила выключение компьютера.",
            "reminder.added" => $"Поставила напоминание: {what}.",
            "reminder.due" => $"Напомнила: {what}.",
            "reminder.cancelled" => $"Отменила напоминание: {what}.",
            "reminder.acknowledged" => "",
            "memory.learned" => $"Запомнила о тебе: {what}.",
            _ => "",
        };
    }

    private static string ToolTitle(string tool, string fallback) => tool switch
    {
        "system_snapshot" => "проверила состояние системы",
        "processes" => "посмотрела запущенные процессы",
        "gpu_status" => "проверила видеокарту",
        "ollama_status" => "проверила локальную модель",
        "list_directory" => "посмотрела содержимое папки",
        "read_text_file" => "прочитала файл",
        "find_files" => "поискала файлы",
        "git_status" => "проверила состояние репозитория",
        "open_app" => "открыла приложение",
        "close_app" => "закрыла приложение",
        "focus_app" => "переключилась на приложение",
        "minimize_app" => "свернула приложение",
        "read_document" => "прочитала документ",
        "browser" => "поработала в браузере",
        "files" => "навела порядок в файлах",
        "read_screen" => "прочитала экран",
        "click_ui" => "нажала",
        "type_text" => "напечатала текст",
        "rename_file" => "переименовала файл",
        "move_file" => "переместила файл",
        "delete_file" => "отправила файл в корзину",
        "open_url" => "открыла ссылку",
        "search_web" => "поискала в интернете",
        _ => fallback,
    };

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
