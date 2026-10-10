using System.Text.RegularExpressions;
using Edith.Core.Documents;
using Edith.Core.Runtime;

namespace Edith.Core.Browser;

public enum BrowserCommandKind
{
    /// <summary>Answer by voice from a background web search; nothing opens.</summary>
    Research,
    Search,
    OpenSite,
    ReadPage,
    AskPage
}

public sealed record BrowserCommand(BrowserCommandKind Kind, string Argument = "", string Url = "");

/// <summary>EDITH's own browser window (separate profile). The app implements it with Playwright.</summary>
public interface IBrowser
{
    Task OpenAsync(string url, CancellationToken cancellationToken);

    /// <summary>Readable text of the current page, or null when no page is open.</summary>
    Task<BrowserPage?> ReadAsync(CancellationToken cancellationToken);
}

public sealed record BrowserPage(string Url, string Title, string Text);

public static class BrowserCommandParser
{
    private static readonly Dictionary<string, string> Sites = new()
    {
        ["ютуб"] = "https://www.youtube.com", ["youtube"] = "https://www.youtube.com",
        ["вк"] = "https://vk.com", ["вконтакте"] = "https://vk.com",
        ["гитхаб"] = "https://github.com", ["github"] = "https://github.com",
        ["хабр"] = "https://habr.com", ["википедию"] = "https://ru.wikipedia.org", ["википедия"] = "https://ru.wikipedia.org",
        ["почту"] = "https://mail.yandex.ru", ["яндекс почту"] = "https://mail.yandex.ru",
        ["кинопоиск"] = "https://www.kinopoisk.ru", ["озон"] = "https://www.ozon.ru", ["вайлдберриз"] = "https://www.wildberries.ru",
        ["авито"] = "https://www.avito.ru", ["дзен"] = "https://dzen.ru", ["твич"] = "https://www.twitch.tv",
        ["реддит"] = "https://www.reddit.com", ["яндекс"] = "https://ya.ru", ["гугл"] = "https://www.google.com",
        ["переводчик"] = "https://translate.yandex.ru", ["карты"] = "https://yandex.ru/maps", ["погоду"] = "https://yandex.ru/pogoda",
        ["леста"] = "https://lesta.ru", ["мир танков"] = "https://tanki.su", ["стим"] = "https://store.steampowered.com",
    };

    private static readonly Regex Youtube = new(@"^(?:найди|поищи|включи)\s+(?:на|в)\s+(?:ютубе|youtube)\s+(?<q>.+)$", RegexOptions.Compiled);
    private static readonly Regex Search = new(
        @"^(?:найди|поищи|погугли|загугли)\s+(?:в интернете|в сети|в браузере|в гугле|в яндексе)\s+(?<q>.+)$|^(?:загугли|погугли)\s+(?<q2>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex Show = new(
        @"^(?:покажи|открой)\s+(?:это\s+|его\s+|результаты\s+)?в\s+браузере(?:\s+(?<q>.+))?$",
        RegexOptions.Compiled);

    private static readonly Regex Open = new(@"^(?:открой|зайди на|перейди на|покажи)\s+(?:сайт\s+)?(?<site>.+)$", RegexOptions.Compiled);
    private static readonly Regex Read = new(
        @"^(?:прочитай|перескажи|что на)\s+(?:эту\s+|текущую\s+)?(?:страниц[уе]|сайт[еа]?|статью)$|^о ч[её]м (?:эта )?(?:страница|статья)$",
        RegexOptions.Compiled);
    private static readonly Regex Ask = new(
        @"^(?:что|найди)\s+(?:на|в)\s+(?:этой\s+)?(?:странице|статье|сайте)\s+(?:про|о|об|по|насчет)\s+(?<q>.+)$",
        RegexOptions.Compiled);
    private static readonly Regex Domain = new(@"^[a-z0-9-]+(?:\.[a-z0-9-]+)+(?:/\S*)?$", RegexOptions.Compiled);

    public static bool TryParse(string text, out BrowserCommand command)
    {
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[«»""?!]|\.$", " ");
        input = Regex.Replace(Regex.Replace(input, @"\s+", " ").Trim(), @"^эдит\s*,?\s*", "");
        command = new BrowserCommand(BrowserCommandKind.ReadPage);

        if (Read.IsMatch(input))
            return true;

        Match match = Ask.Match(input);
        if (match.Success)
        {
            command = new BrowserCommand(BrowserCommandKind.AskPage, match.Groups["q"].Value.Trim());
            return true;
        }

        match = Youtube.Match(input);
        if (match.Success)
        {
            string q = match.Groups["q"].Value.Trim();
            command = new BrowserCommand(BrowserCommandKind.Search, q, "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(q));
            return true;
        }

        match = Show.Match(input);
        if (match.Success)
        {
            // Empty query = the last thing EDITH looked up (filled in by the assistant).
            string q = match.Groups["q"].Value.Trim();
            command = new BrowserCommand(BrowserCommandKind.Search, q, q.Length == 0 ? "" : SearchUrl(q));
            return true;
        }

        match = Search.Match(input);
        if (match.Success)
        {
            string q = (match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["q2"].Value).Trim();
            command = new BrowserCommand(BrowserCommandKind.Research, q, SearchUrl(q));
            return true;
        }

        match = Open.Match(input);
        if (match.Success)
        {
            string site = match.Groups["site"].Value.Trim();
            string? url = Sites.TryGetValue(site, out string? known) ? known
                : Domain.IsMatch(site.Replace(" точка ", ".")) ? "https://" + site.Replace(" точка ", ".")
                : null;

            if (url is not null)
            {
                command = new BrowserCommand(BrowserCommandKind.OpenSite, site, url);
                return true;
            }
        }

        return false;
    }

    public static string SearchUrl(string query) => "https://ya.ru/search/?text=" + Uri.EscapeDataString(query);
}

public sealed record BrowserAnswer(string Reply, string Question = "", IReadOnlyList<string>? Passages = null, string PageTitle = "");

/// <summary>Carries out browser commands; page text goes back as passages (data, never instructions).</summary>
public sealed class BrowserAssistant(IBrowser browser, IEventBus? bus = null)
{
    /// <summary>The last web question, so "покажи в браузере" can open it.</summary>
    public string? LastQuery { get; set; }

    public async Task<BrowserAnswer> HandleAsync(BrowserCommand command, CancellationToken cancellationToken)
    {
        if (command.Kind == BrowserCommandKind.Search && command.Url.Length == 0)
        {
            if (LastQuery is null)
                return new BrowserAnswer("Не поняла, что показать. Скажи, например: покажи в браузере погоду в Москве.");
            command = command with { Argument = LastQuery, Url = BrowserCommandParser.SearchUrl(LastQuery) };
        }

        try
        {
            switch (command.Kind)
            {
                case BrowserCommandKind.Search:
                case BrowserCommandKind.OpenSite:
                    await browser.OpenAsync(command.Url, cancellationToken);
                    Journal(EventKinds.ToolFinished, command.Url);
                    return new BrowserAnswer(command.Kind == BrowserCommandKind.Search
                        ? $"Ищу «{command.Argument}»."
                        : $"Открываю {command.Argument}.");

                case BrowserCommandKind.ReadPage:
                case BrowserCommandKind.AskPage:
                {
                    BrowserPage? page = await browser.ReadAsync(cancellationToken);
                    if (page is null || page.Text.Length == 0)
                        return new BrowserAnswer("В моём браузере сейчас нет открытой страницы.");

                    Journal(EventKinds.ToolFinished, page.Url);
                    string question = command.Kind == BrowserCommandKind.AskPage ? command.Argument : "главное";
                    IReadOnlyList<string> passages = command.Kind == BrowserCommandKind.AskPage
                        ? DocumentQuery.Passages(page.Text, command.Argument)
                        : [DocumentQuery.Opening(page.Text, 600)];

                    if (passages.Count == 0)
                        return new BrowserAnswer($"На странице «{page.Title}» не нашла ничего на тему «{command.Argument}».", question, passages, page.Title);

                    return new BrowserAnswer($"На странице «{page.Title}»: {string.Join(" … ", passages)}", question, passages, page.Title);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Journal(EventKinds.ToolFailed, command.Url.Length > 0 ? command.Url : command.Kind.ToString(), ex.Message);
            return new BrowserAnswer("Браузер не справился: " + ex.Message);
        }

        return new BrowserAnswer("");
    }

    private void Journal(string kind, string target, string detail = "") =>
        bus?.Publish(EdithEvent.Create(kind, "browser", data: new Dictionary<string, string>
        {
            ["tool"] = "browser",
            ["arguments"] = target,
            ["app"] = target,
            ["result"] = detail,
        }));
}
