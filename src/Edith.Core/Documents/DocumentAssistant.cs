using System.Text.RegularExpressions;
using Edith.Core.Runtime;

namespace Edith.Core.Documents;

public enum DocumentCommandKind
{
    Find,
    Ask,
    Overview
}

/// <summary><paramref name="Name"/> empty = the document talked about last.</summary>
public sealed record DocumentCommand(DocumentCommandKind Kind, string Name, string Question = "");

public sealed record DocumentAnswer(
    string Reply,
    string? DocumentPath = null,
    string Question = "",
    IReadOnlyList<string>? Passages = null);

public static class DocumentCommandParser
{
    private const string Doc = @"(?:документ\w*|файл\w*|pdf|пдф|pdf-?файл\w*|ворд\w*|текст\w*)";
    private const string About = @"(?:про|о|об|по|насчет|по поводу|касательно)";
    private const string AlreadyHad = @"(?:\s*,?\s*(?:который|которую|что)\s+я\s+[\p{L}]+)?";

    private static readonly Regex FindAndAsk = new(
        @"^(?:найди|открой|поищи|посмотри)\s+(?:мне\s+)?" + Doc + @"\s+(?:" + About + @"\s+)?(?<name>.+?)" + AlreadyHad +
        @"\s*,?\s*и\s+(?:расскажи|скажи|посмотри|проверь)\s*,?\s*(?:что\s+)?(?:там\s+|в нем\s+)?(?:написано\s+)?" + About + @"\s+(?<q>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex AskNamed = new(
        @"^(?:что|расскажи что|скажи что)\s+(?:написано\s+)?в\s+" + Doc + @"\s+(?:" + About + @"\s+)?(?<name>.+?)\s+" + About + @"\s+(?<q>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex AskCurrent = new(
        @"^(?:а\s+)?(?:что|расскажи что|скажи что)\s+(?:там|в нем|в этом документе|в документе)\s+(?:написано\s+)?" + About + @"\s+(?<q>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex OverviewNamed = new(
        @"^(?:прочитай|что в|о ч[её]м|перескажи)\s+" + Doc + @"\s+(?:" + About + @"\s+)?(?<name>.+)$",
        RegexOptions.Compiled);

    private static readonly Regex OverviewCurrent = new(
        @"^(?:о ч[её]м|что в)\s+(?:этот|этом|этого)\s+" + Doc + @"$",
        RegexOptions.Compiled);

    private static readonly Regex Find = new(
        @"^(?:найди|поищи|где)\s+(?:мне\s+)?" + Doc + @"\s+(?:" + About + @"\s+)?(?<name>.+?)" + AlreadyHad + @"$",
        RegexOptions.Compiled);

    public static bool TryParse(string text, out DocumentCommand command)
    {
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[«»""?!.]", " ");
        input = Regex.Replace(input, @"\s+", " ").Trim();
        input = Regex.Replace(input, @"^эдит\s*,?\s*", "");

        (Regex regex, DocumentCommandKind kind)[] patterns =
        [
            (FindAndAsk, DocumentCommandKind.Ask),
            (AskNamed, DocumentCommandKind.Ask),
            (AskCurrent, DocumentCommandKind.Ask),
            (OverviewCurrent, DocumentCommandKind.Overview),
            (OverviewNamed, DocumentCommandKind.Overview),
            (Find, DocumentCommandKind.Find),
        ];

        foreach ((Regex regex, DocumentCommandKind kind) in patterns)
        {
            Match match = regex.Match(input);
            if (!match.Success)
                continue;

            command = new DocumentCommand(
                kind,
                match.Groups["name"].Value.Trim(' ', ','),
                match.Groups["q"].Value.Trim(' ', ','));
            return true;
        }

        command = new DocumentCommand(DocumentCommandKind.Find, "");
        return false;
    }
}

/// <summary>
/// Finds a document, reads it and answers from it. Remembers the last document so
/// "а что там про стоимость?" works as a follow-up. Deterministic; a model may later turn
/// <see cref="DocumentAnswer.Passages"/> into a nicer answer.
/// </summary>
public sealed class DocumentAssistant(Func<IEnumerable<string>> directories, IEventBus? bus = null)
{
    private readonly object _gate = new();
    private DocumentContent? _current;

    public string? CurrentPath
    {
        get
        {
            lock (_gate)
                return _current?.Path;
        }
    }

    public DocumentAnswer Handle(DocumentCommand command)
    {
        DocumentContent? document;
        IReadOnlyList<DocumentMatch> found = [];

        if (command.Name.Length == 0)
        {
            lock (_gate)
                document = _current;

            if (document is null)
                return new DocumentAnswer("Не поняла, о каком документе речь. Назови его, например: найди PDF про университет.");
        }
        else
        {
            found = DocumentFinder.Find(command.Name, directories());
            if (found.Count == 0)
                return new DocumentAnswer($"Не нашла документ про «{command.Name}» в Загрузках, Документах и на рабочем столе.");

            try
            {
                document = DocumentText.Extract(found[0].Path);
            }
            catch (Exception ex)
            {
                Journal(EventKinds.ToolFailed, found[0].Path, ex.Message);
                return new DocumentAnswer($"Нашла «{Path.GetFileName(found[0].Path)}», но не смогла его прочитать: {ex.Message}");
            }

            lock (_gate)
                _current = document;
        }

        string name = Path.GetFileName(document.Path);
        Journal(EventKinds.ToolFinished, document.Path, command.Kind.ToString());

        switch (command.Kind)
        {
            case DocumentCommandKind.Find:
            {
                string others = found.Count > 1 ? $" Есть ещё похожие: {string.Join(", ", found.Skip(1).Take(2).Select(m => "«" + Path.GetFileName(m.Path) + "»"))}." : "";
                string pages = document.Pages > 0 ? $", {document.Pages} стр." : "";
                return new DocumentAnswer($"Нашла «{name}»{pages}.{others} Что в нём посмотреть?", document.Path);
            }

            case DocumentCommandKind.Overview:
                return document.Text.Length == 0
                    ? new DocumentAnswer($"В «{name}» нет текста, который я могу прочитать. Возможно, это скан.", document.Path)
                    : new DocumentAnswer($"«{name}» начинается так: {DocumentQuery.Opening(document.Text)}", document.Path);

            default:
            {
                IReadOnlyList<string> passages = DocumentQuery.Passages(document.Text, command.Question);
                return passages.Count == 0
                    ? new DocumentAnswer($"В «{name}» не нашла ничего на тему «{command.Question}».", document.Path, command.Question, passages)
                    : new DocumentAnswer($"Вот что нашла в «{name}»: {string.Join(" … ", passages)}", document.Path, command.Question, passages);
            }
        }
    }

    private void Journal(string kind, string path, string detail) =>
        bus?.Publish(EdithEvent.Create(
            kind,
            "read_document",
            data: new Dictionary<string, string>
            {
                ["tool"] = "read_document",
                ["arguments"] = path,
                ["app"] = Path.GetFileName(path),
                ["result"] = detail,
            }));
}
