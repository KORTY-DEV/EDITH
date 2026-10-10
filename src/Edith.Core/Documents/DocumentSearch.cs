using System.Text.RegularExpressions;
using Edith.Core.Memory;

namespace Edith.Core.Documents;

public sealed record DocumentMatch(string Path, int Score, DateTime Modified);

/// <summary>
/// Finds the user's documents by spoken name ("PDF про университет" → ULEY_University_Guide.pdf)
/// in Downloads, Documents and Desktop. Matching is by word stems of the file name, with a
/// small Russian→English dictionary for words people say in Russian about English names.
/// </summary>
public static class DocumentFinder
{
    private static readonly Dictionary<string, string[]> Translations = new()
    {
        ["университет"] = ["university", "uni"], ["универ"] = ["university", "uni"],
        ["руководств"] = ["guide", "manual"], ["инструкц"] = ["manual", "instruction", "guide"],
        ["договор"] = ["contract", "agreement"], ["счет"] = ["invoice", "bill"],
        ["резюм"] = ["resume", "cv"], ["отчет"] = ["report"], ["расписан"] = ["schedule", "timetable"],
        ["заметк"] = ["notes", "note"], ["списк"] = ["list"], ["чек"] = ["receipt"],
    };

    private static readonly HashSet<string> FillerStems =
        ["документ", "файл", "pdf", "пдф", "docx", "ворд", "word", "текст", "скачива", "скачал", "скача", "котор", "тот"];

    public static IEnumerable<string> DefaultDirectories()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Downloads");
        yield return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    public static IReadOnlyList<DocumentMatch> Find(string spokenName, IEnumerable<string> directories, int max = 5)
    {
        List<string> wanted = RussianStemmer.Stems(spokenName)
            .Where(stem => !FillerStems.Contains(stem))
            .ToList();
        if (wanted.Count == 0)
            return [];

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 3 };
        var results = new List<DocumentMatch>();

        foreach (string directory in directories.Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*.*", options).Take(20_000))
            {
                if (!DocumentText.SupportedExtensions.Contains(Path.GetExtension(file)))
                    continue;

                int score = Score(Path.GetFileNameWithoutExtension(file), wanted);
                if (score > 0)
                    results.Add(new DocumentMatch(file, score, SafeModified(file)));
            }
        }

        return results
            .OrderByDescending(m => m.Score)
            .ThenByDescending(m => m.Modified)
            .Take(max)
            .ToList();
    }

    /// <summary>How many of the wanted stems (or their English translations) the file name contains.</summary>
    public static int Score(string fileName, IReadOnlyList<string> wantedStems)
    {
        string name = LocalMemoryStore.Normalize(Regex.Replace(fileName, @"[_\-.]+|(?<=[a-z])(?=[A-Z])", " "));
        List<string> nameStems = RussianStemmer.Words(name).Select(RussianStemmer.Stem).ToList();

        int score = 0;
        foreach (string stem in wantedStems)
        {
            IEnumerable<string> variants = Translations
                .Where(pair => stem.StartsWith(pair.Key, StringComparison.Ordinal) || pair.Key.StartsWith(stem, StringComparison.Ordinal))
                .SelectMany(pair => pair.Value)
                .Prepend(stem);

            if (variants.Any(v => nameStems.Any(n => n.StartsWith(v, StringComparison.Ordinal) || (n.Length >= 4 && v.StartsWith(n, StringComparison.Ordinal)))))
                score++;
        }

        return score;
    }

    private static DateTime SafeModified(string file)
    {
        try
        {
            return File.GetLastWriteTime(file);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}

/// <summary>Passages of a document that answer a question, found by stem overlap (no model).</summary>
public static class DocumentQuery
{
    private static readonly Dictionary<string, string[]> Expansions = new()
    {
        ["срок"] = ["срок", "дедлайн", "deadline", "дата", "date", "until", "due", "январ", "феврал", "март", "апрел", "мая", "июн", "июл", "август", "сентябр", "октябр", "ноябр", "декабр", "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"],
        ["цен"] = ["цен", "стоим", "руб", "₽", "$", "€", "price", "cost", "fee", "tuition"],
        ["стоим"] = ["цен", "стоим", "руб", "₽", "$", "€", "price", "cost", "fee", "tuition"],
        ["документ"] = ["документ", "document", "паспорт", "passport", "справк", "certificate"],
        ["контакт"] = ["контакт", "телефон", "email", "e-mail", "почт", "contact", "phone"],
        ["адрес"] = ["адрес", "address", "улиц", "street"],
        ["требован"] = ["требован", "requirement", "required", "must", "необходим"],
        ["поступлен"] = ["поступлен", "admission", "apply", "application", "enrol"],
        ["общежит"] = ["общежит", "dorm", "housing", "accommodation", "residence"],
        ["виз"] = ["виз", "visa"],
        ["установ"] = ["установ", "install", "setup", "инсталл"],
        ["запуск"] = ["запуск", "запуст", "run", "start", "launch"],
        ["ошибк"] = ["ошибк", "ошибок", "error", "fail", "сбой"],
        ["настройк"] = ["настройк", "настро", "config", "setting"],
        ["оплат"] = ["оплат", "платеж", "payment", "pay"],
        ["экзамен"] = ["экзамен", "exam", "test"],
        ["перезагруз"] = ["перезагруз", "reboot", "restart"],
        ["провер"] = ["провер", "check", "verify", "test"],
        ["шаг"] = ["шаг", "step"],
    };

    public static IReadOnlyList<string> Passages(string text, string question, int max = 3, int maxChars = 350)
    {
        List<string> terms = RussianStemmer.Stems(question)
            .SelectMany(stem => Expansions.Where(e => stem.StartsWith(e.Key, StringComparison.Ordinal)).SelectMany(e => e.Value).Prepend(stem))
            .Distinct()
            .ToList();

        if (terms.Count == 0)
            return [];

        return Split(text)
            .Select((passage, index) => (passage, index, score: Score(passage, terms)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(max)
            .OrderBy(x => x.index)
            .Select(x => x.passage.Length <= maxChars ? x.passage : x.passage[..maxChars].TrimEnd() + "…")
            .ToList();
    }

    /// <summary>The opening of the document, for "что в этом документе".</summary>
    public static string Opening(string text, int maxChars = 300)
    {
        string opening = string.Join(" ", Split(text).Take(3));
        return opening.Length <= maxChars ? opening : opening[..maxChars].TrimEnd() + "…";
    }

    private static IEnumerable<string> Split(string text) =>
        Regex.Split(text, @"\n\s*\n|(?<=[.!?])\s+(?=[A-ZА-ЯЁ0-9])")
            .Select(p => Regex.Replace(Regex.Replace(p, @"[☐☑☒✓✔•▪◦■□●]", " "), @"\s+", " ").Trim())
            .Where(p => p.Length >= 12);

    /// <summary>
    /// Terms match at word starts only ("цен" must not fire inside "сценарии"); currency
    /// symbols match anywhere. A passage with a date gets a bonus when dates were asked for.
    /// </summary>
    private static int Score(string passage, List<string> terms)
    {
        string lower = LocalMemoryStore.Normalize(passage);
        List<string> words = RussianStemmer.Words(lower).ToList();

        int score = terms.Count(term =>
            term.Any(char.IsLetterOrDigit)
                ? words.Any(word => word.StartsWith(term, StringComparison.Ordinal))
                : lower.Contains(term, StringComparison.Ordinal));

        if (score > 0 && terms.Contains("дата") &&
            Regex.IsMatch(passage, @"\b\d{1,2}[./]\d{1,2}(?:[./]\d{2,4})?\b|\b\d{1,2}\s+[а-яa-z]{3,}\s+20\d{2}\b"))
            score++;

        return score;
    }
}
