using System.Text.Json.Nodes;
using Edith.Core.Agent;

namespace Edith.App;

/// <summary>
/// Everything the brain may call, with JSON schemas for Ollama's native tool calling.
/// Execution stays in <see cref="BrainToolRouter"/>; risky tools ask the user themselves.
/// </summary>
public static class BrainToolCatalog
{
    public static IReadOnlyList<ToolSpec> All { get; } =
    [
        Tool("system_snapshot", "Состояние ПК: оперативная память, диски, версия Windows.", ToolRisk.Read),
        Tool("processes", "Запущенные процессы с загрузкой CPU и памятью.", ToolRisk.Read),
        Tool("gpu_status", "Видеокарта NVIDIA: загрузка, память, температура.", ToolRisk.Read),
        Tool("ollama_status", "Какие локальные модели Ollama сейчас загружены.", ToolRisk.Read),
        Tool("list_directory", "Содержимое папки.", ToolRisk.Read,
            ("path", "Путь к папке, можно %USERPROFILE%\\Downloads и т.п.", true)),
        Tool("read_text_file", "Прочитать текстовый файл.", ToolRisk.Read,
            ("path", "Путь к файлу.", true)),
        Tool("find_files", "Найти файлы по маске имени в папке и подпапках.", ToolRisk.Read,
            ("root", "Папка, где искать.", true),
            ("pattern", "Маска имени, например *.pdf или *отчёт*.", true)),
        Tool("git_status", "Состояние git-репозитория.", ToolRisk.Read,
            ("path", "Путь к репозиторию.", true)),
        Tool("search_web", "Фоновый поиск в интернете для свежих фактов (новости, цены, погода, «сейчас/лучший»). Не открывает браузер.", ToolRisk.Read,
            ("query", "Поисковый запрос.", true)),
        Tool("read_web_page", "Прочитать текст страницы по ссылке из результатов search_web: цены, характеристики, подробности. Не открывает браузер.", ToolRisk.Read,
            ("url", "Адрес страницы (http/https).", true)),
        Tool("search_notes", "Поиск по личным заметкам хозяина (Obsidian): проекты ULEY и EDITH, учёба ИДЭУ, решения, идеи, задачи. Используй, когда спрашивают, что записано, запланировано или решено.", ToolRisk.Read,
            ("query", "О чём искать в заметках.", true)),
        Tool("read_note", "Прочитать заметку целиком по названию, например «Задачи ULEY» или «EDITH — Главная».", ToolRisk.Read,
            ("title", "Название заметки.", true)),
        Tool("browser_open", "Открыть сайт в собственном браузере EDITH и показать, что на странице и что можно нажать. Для работы с сайтами по шагам.", ToolRisk.Change,
            ("url", "Адрес страницы (http/https).", true)),
        Tool("browser_look", "Посмотреть текущую страницу в браузере EDITH: текст и пронумерованные кнопки, ссылки и поля.", ToolRisk.Read),
        Tool("browser_click", "Нажать на странице в браузере EDITH ссылку или кнопку по номеру из browser_look или по названию. Рискованное (купить, оплатить, отправить, удалить) спросит подтверждение.", ToolRisk.Change,
            ("target", "Номер элемента или его название.", true)),
        Tool("browser_type", "Ввести текст в поле на странице браузера EDITH (поиск, форма). Пароли не вводятся.", ToolRisk.Change,
            ("field", "Номер поля из browser_look или его название.", true),
            ("text", "Что ввести.", true),
            ("submit", "true — после ввода нажать Enter (отправить поиск).", false)),
        Tool("browser_back", "Вернуться на предыдущую страницу в браузере EDITH.", ToolRisk.Read),
        Tool("describe_screen", "Описать, что нарисовано или показано в активном окне (картинка, фото, дизайн, график, кадр видео, игра): окно уходит на распознавание в облако. Для текста и ошибок используй read_screen.", ToolRisk.Read,
            ("question", "Что именно хозяин хочет узнать о картинке.", false)),
        Tool("find_image", "Найти настоящую фотографию или картинку предмета, места, человека, устройства в интернете (Wikimedia Commons) и показать в окне ответов. Используй, когда просят показать, как выглядит что-то, или показать пейзаж, животное, видеокарту. Запрос лучше на английском, коротко: «landscape mountains lake», «NVIDIA GeForce RTX 4090». Для «самый лучший / новейший / сейчас» СНАЧАЛА узнай актуальное название через search_web (твои знания о технике устарели, сейчас 2026 год), потом ищи картинку по конкретной модели. После успеха картинка уже на экране — второй раз ничего не вызывай. Если ничего не нашлось — скажи об этом; нарисовать (draw_image) только если хозяин попросит.", ToolRisk.Read,
            ("query", "Что искать, коротко, лучше по-английски.", true)),
        Tool("show_image", "Показать картинку в окне ответов: ТОЛЬКО файл на этом ПК, путь к которому назвал хозяин, или ссылка https, которую назвал хозяин или которую ты получил из результата другого инструмента. Никогда не выдумывай ссылку: для «покажи …» без ссылки используй find_image.", ToolRisk.Read,
            ("source", "Путь к файлу или ссылка https.", true),
            ("caption", "Короткая подпись.", false)),
        Tool("draw_image", "Нарисовать картинку по описанию (облако) и показать в окне ответов. Только если хозяин просит нарисовать или сгенерировать картинку.", ToolRisk.Read,
            ("prompt", "Что нарисовать, подробно.", true)),
        Tool("read_screen", "Прочитать текст активного окна на экране (что видит пользователь).", ToolRisk.Read),
        Tool("click_ui", "Нажать кнопку/пункт/ссылку в активном окне по названию. Рискованные кнопки спрашивают подтверждение.", ToolRisk.Change,
            ("name", "Название элемента, как написано на экране.", true)),
        Tool("type_text", "Напечатать текст в активное поле ввода (без нажатия Enter).", ToolRisk.Change,
            ("text", "Текст.", true)),
        Tool("open_app", "Открыть приложение по названию. Только если пользователь явно попросил открыть.", ToolRisk.Change,
            ("name", "Название приложения.", true)),
        Tool("open_url", "Открыть ссылку в браузере. Только если пользователь явно попросил открыть или показать.", ToolRisk.Change,
            ("url", "Адрес.", true)),
        Tool("move_file", "Переместить файл (с подтверждением пользователя). Только в пределах Загрузок, Документов, Рабочего стола, Изображений, Видео, Музыки.", ToolRisk.Change,
            ("source", "Путь к файлу.", true),
            ("destination", "Новый путь или папка назначения.", true)),
        Tool("rename_file", "Переименовать файл (с подтверждением пользователя).", ToolRisk.Change,
            ("path", "Путь к файлу.", true),
            ("new_name", "Новое имя файла; расширение сохраняется, если не указано.", true)),
        Tool("delete_file", "Отправить файл в корзину (с подтверждением пользователя). Безвозвратно не удаляет.", ToolRisk.Destructive,
            ("path", "Путь к файлу.", true)),
        Tool("kill_process", "Завершить процесс по PID (с подтверждением пользователя).", ToolRisk.Destructive,
            ("pid", "Идентификатор процесса.", true)),
    ];

    /// <summary>Tools for this request: while gaming, nothing that opens windows over the game.</summary>
    public static IReadOnlyList<ToolSpec> For(bool gameActive) =>
        gameActive ? All.Where(t => t.Name is not ("open_app" or "open_url" or "click_ui" or "type_text")).ToList() : All;

    private static ToolSpec Tool(string name, string description, ToolRisk risk, params (string Name, string Description, bool Required)[] parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach ((string parameter, string text, bool isRequired) in parameters)
        {
            properties[parameter] = new JsonObject
            {
                ["type"] = parameter == "pid" ? "integer" : "string",
                ["description"] = text,
            };
            if (isRequired)
                required.Add(parameter);
        }

        return new ToolSpec(name, description, new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        }, risk);
    }
}
