using System.Text;
using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Makes text sayable by a Russian-only TTS (Silero drops Latin letters and symbols):
/// drive letters, units with number agreement, known names and abbreviations, loanwords whose
/// "е" is said as "э", and a rough transliteration of any Latin left.
/// Runs before <see cref="RussianNumbers.Verbalize"/>.
/// </summary>
public static class SpeechNormalizer
{
    // Longest keys first so "Opera GX" wins over "Opera".
    private static readonly (Regex Pattern, string Spoken)[] Names = Build(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["E.D.I.T.H."] = "Эдит", ["EDITH"] = "Эдит", ["Edith.App"] = "Эдит", ["Edith"] = "Эдит",
        ["Opera GX"] = "Опера джи икс", ["Opera"] = "Опера", ["Comet"] = "Комет", ["Telegram"] = "Телеграм",
        ["Discord"] = "Дискорд", ["Steam"] = "Стим", ["VK"] = "вэка", ["YouTube"] = "ютуб", ["Google"] = "гугл",
        ["Windows"] = "виндоус", ["NVIDIA"] = "энвидиа", ["GeForce"] = "джифорс", ["RTX"] = "эр тэ икс", ["GTX"] = "джи ти икс",
        ["Ollama"] = "оллама", ["Uley"] = "Улей", ["LM Studio"] = "эл эм студио", ["Chrome"] = "хром", ["Edge"] = "эдж", ["Wi-Fi"] = "вай фай",
        ["CPU"] = "процессор", ["GPU"] = "видеокарта", ["RAM"] = "оперативная память", ["VRAM"] = "видеопамять",
        ["SSD"] = "эс эс дэ", ["HDD"] = "аш дэ дэ", ["USB"] = "ю эс би", ["PDF"] = "пэ дэ эф", ["OK"] = "окей",
        ["AI"] = "ИИ", ["PC"] = "пэ цэ", ["ПК"] = "пэ ка", ["ОЗУ"] = "оперативка", ["ИИ"] = "И И",
        ["Playwright"] = "плейрайт", ["Chromium"] = "хромиум", ["Python"] = "пайтон", ["JavaScript"] = "джаваскрипт",
        ["TypeScript"] = "тайпскрипт", ["Docker"] = "докер", ["Linux"] = "линукс", ["Obsidian"] = "обсидиан",
        ["ChatGPT"] = "чат джи пи ти", ["GigaChat"] = "гигачат", ["GigaAM"] = "гига эй эм", ["Habr"] = "хабр", ["GitHub"] = "гитхаб",
        ["Yandex"] = "яндекс", ["Sber"] = "сбер", ["Whisper"] = "виспер", ["Silero"] = "силеро", ["Vosk"] = "воск",
        ["Qwen"] = "квен", ["OpenAI"] = "оупен эй ай",
        ["READY"] = "реди", ["TODO"] = "ту ду", ["DONE"] = "дан", ["BUG"] = "баг", ["FIX"] = "фикс",
        ["MS"] = "эм эс", ["IT"] = "ай ти", ["URL"] = "ссылка", ["FPS"] = "фэ пэ эс",
    });

    // Silero reads these with a soft "е"; people say "э".
    private static readonly (Regex Pattern, string Spoken)[] Loanwords = Build(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["мессенджер"] = "мэсэнджер", ["мессенджера"] = "мэсэнджера", ["мессенджере"] = "мэсэнджере",
        ["интерфейс"] = "интэрфейс", ["интерфейса"] = "интэрфейса", ["интернет"] = "интэрнэт", ["интернете"] = "интэрнэте", ["интернета"] = "интэрнэта",
        ["сервер"] = "сэрвер", ["сервера"] = "сэрвера", ["тест"] = "тэст", ["теста"] = "тэста", ["тесты"] = "тэсты",
        ["модель"] = "модэль", ["модели"] = "модэли", ["компьютер"] = "компьютэр", ["компьютера"] = "компьютэра", ["компьютере"] = "компьютэре",
        ["дисплей"] = "дисплэй", ["бренд"] = "брэнд", ["тренд"] = "трэнд", ["планшет"] = "планшэт", ["кэш"] = "кэш",
        ["отель"] = "отэль", ["кафе"] = "кафэ", ["резюме"] = "резюмэ", ["энергия"] = "энергия", ["тенденция"] = "тэндэнция",
        ["сенсор"] = "сэнсор", ["стартап"] = "стартап", ["демо"] = "дэмо", ["сет"] = "сэт", ["тема"] = "тэма",
        ["детектор"] = "дэтэктор", ["процессор"] = "процэссор", ["процессора"] = "процэссора", ["процессы"] = "процэссы", ["процесс"] = "процэсс",
    });

    private static readonly (Regex Pattern, string One, string Few, string Many, string Fraction)[] Units =
    [
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:ГБ|Гб|гб|GB|GiB)\b", RegexOptions.Compiled), "гигабайт", "гигабайта", "гигабайт", "гигабайта"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:МБ|Мб|мб|MB|MiB)\b", RegexOptions.Compiled), "мегабайт", "мегабайта", "мегабайт", "мегабайта"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:ТБ|Тб|TB|TiB)\b", RegexOptions.Compiled), "терабайт", "терабайта", "терабайт", "терабайта"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:КБ|Кб|KB)\b", RegexOptions.Compiled), "килобайт", "килобайта", "килобайт", "килобайта"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:ГГц|GHz)", RegexOptions.Compiled), "гигагерц", "гигагерца", "гигагерц", "гигагерца"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:МГц|MHz)", RegexOptions.Compiled), "мегагерц", "мегагерца", "мегагерц", "мегагерца"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?°\s?C?\b", RegexOptions.Compiled), "градус", "градуса", "градусов", "градуса"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:мс|ms)\b", RegexOptions.Compiled), "миллисекунда", "миллисекунды", "миллисекунд", "миллисекунды"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:₽|руб\.?)", RegexOptions.Compiled), "рубль", "рубля", "рублей", "рубля"),
        (new(@"(?<n>\d+(?:[.,]\d+)?)\s?(?:\$|USD)", RegexOptions.Compiled), "доллар", "доллара", "долларов", "доллара"),
    ];

    private static readonly Dictionary<char, string> DriveLetters = new()
    {
        ['A'] = "А", ['B'] = "Бэ", ['C'] = "Си", ['D'] = "Ди", ['E'] = "И", ['F'] = "Эф", ['G'] = "Джи", ['H'] = "Эйч",
    };

    private static readonly Dictionary<char, string> LetterNames = new()
    {
        ['A'] = "эй", ['B'] = "би", ['C'] = "си", ['D'] = "ди", ['E'] = "и", ['F'] = "эф", ['G'] = "джи", ['H'] = "эйч",
        ['I'] = "ай", ['J'] = "джей", ['K'] = "кей", ['L'] = "эл", ['M'] = "эм", ['N'] = "эн", ['O'] = "оу", ['P'] = "пи",
        ['Q'] = "кью", ['R'] = "ар", ['S'] = "эс", ['T'] = "ти", ['U'] = "ю", ['V'] = "ви", ['W'] = "дабл ю", ['X'] = "экс",
        ['Y'] = "уай", ['Z'] = "зед",
    };

    public static string Normalize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;

        // Initials are not read letter by letter: "А.С. Пушкин" → "Пушкин", as people say it.
        text = Regex.Replace(text, @"(?<![\p{L}])[А-ЯЁ]\.\s?(?:[А-ЯЁ]\.\s?)?(?=[А-ЯЁ][а-яё]{2,})", "");

        // Drive letters: "диске C:" → "диске Си", a bare "D:" → "диск Ди".
        text = Regex.Replace(text, @"(?<![\p{L}])(диск(?:е|а|и|ом|у)?)\s+([A-H]):?\\?(?![\p{L}\d])", m => m.Groups[1].Value + " " + DriveLetters[m.Groups[2].Value[0]]);
        text = Regex.Replace(text, @"(?<![\p{L}\d])([A-H]):\\?(?=[\s,.;)—-]|$)", m => "диск " + DriveLetters[m.Groups[1].Value[0]]);

        // Model numbers are said in pairs: "RTX 5090" → "RTX 50 90" (before names turn Cyrillic).
        text = Regex.Replace(text, @"\b([A-Za-z]{2,})\s(\d{2})(\d{2})\b", "$1 $2 $3");

        foreach ((Regex pattern, string one, string few, string many, string fraction) in Units)
        {
            text = pattern.Replace(text, m =>
            {
                string n = m.Groups["n"].Value;
                if (n.Contains(',') || n.Contains('.'))
                    return n + " " + fraction;
                long value = long.Parse(n);
                return n + " " + RussianNumbers.Plural(value, one, few, many);
            });
        }

        // "Edith.App", "chrome.exe": process and file names are said without the extension.
        text = Regex.Replace(text, @"\b([A-Za-z][\w-]*)\.(?:exe|App|dll)\b", "$1");

        foreach ((Regex pattern, string spoken) in Names)
            text = pattern.Replace(text, spoken);

        foreach ((Regex pattern, string spoken) in Loanwords)
            text = pattern.Replace(text, m => MatchCase(m.Value, spoken));

        // "IG-1", "A-G", "v2-3": the hyphen between Latin and digits is a pause, not part of a word.
        text = Regex.Replace(text, @"(?<=[A-Za-z])[-–—](?=[A-Za-z0-9])|(?<=[0-9])[-–—](?=[A-Za-z])", " ");

        // Any Latin left: abbreviations letter by letter, words transliterated.
        text = Regex.Replace(text, @"\b[A-Za-z][A-Za-z']*\b", m => Latin(m.Value));

        return text;
    }

    // Short English words that are words, not abbreviations, even when written in capitals.
    private static readonly HashSet<string> EnglishWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "FOR", "AND", "THE", "NOT", "NEW", "ALL", "OFF", "ON", "TO", "OF", "IN", "RUN", "NOW", "TOP", "BUG", "FIX",
        "TODO", "DONE", "READY", "WAIT", "STOP", "START", "OPEN", "CLOSE", "BACK", "NEXT", "HOME", "LOCK", "HIDDEN",
    };

    private static string Latin(string word)
    {
        if (word.Length <= 4 && word.All(char.IsUpper) && !EnglishWords.Contains(word))
            return string.Join(" ", word.Select(c => LetterNames.GetValueOrDefault(c, "")));

        return Transliterate(word.ToLowerInvariant());
    }

    /// <summary>English-ish spelling → Russian letters; rough, but audible instead of silent.</summary>
    public static string Transliterate(string word)
    {
        (string From, string To)[] rules =
        [
            ("tion", "шн"), ("ght", "т"), ("igh", "ай"), ("wr", "р"), ("sh", "ш"), ("ch", "ч"), ("th", "т"), ("ph", "ф"), ("ck", "к"), ("oo", "у"), ("ee", "и"),
            ("ea", "и"), ("ou", "ау"), ("ai", "эй"), ("ay", "эй"), ("qu", "кв"), ("wh", "у"), ("kh", "х"), ("zh", "ж"),
            ("ya", "я"), ("yu", "ю"), ("yo", "ё"), ("ey", "ей"), ("oy", "ой"), ("uy", "уй"),
        ];
        var sb = new StringBuilder();
        int i = 0;
        while (i < word.Length)
        {
            bool matched = false;
            foreach ((string from, string to) in rules)
            {
                if (string.CompareOrdinal(word, i, from, 0, from.Length) == 0)
                {
                    sb.Append(to);
                    i += from.Length;
                    matched = true;
                    break;
                }
            }

            if (matched)
                continue;

            char c = word[i++];
            sb.Append(c switch
            {
                'a' => "а", 'b' => "б", 'c' => "к", 'd' => "д", 'e' => "е", 'f' => "ф", 'g' => "г", 'h' => "х",
                'i' => "и", 'j' => "дж", 'k' => "к", 'l' => "л", 'm' => "м", 'n' => "н", 'o' => "о", 'p' => "п",
                'q' => "к", 'r' => "р", 's' => "с", 't' => "т", 'u' => "у", 'v' => "в", 'w' => "в", 'x' => "кс",
                'y' => "и", 'z' => "з", _ => "",
            });
        }

        return sb.ToString();
    }

    private static (Regex, string)[] Build(Dictionary<string, string> map) =>
        map.OrderByDescending(p => p.Key.Length)
            .Select(p => (new Regex(@"(?<![\p{L}\d])" + Regex.Escape(p.Key) + @"(?![\p{L}\d])", RegexOptions.Compiled | RegexOptions.IgnoreCase), p.Value))
            .ToArray();

    private static string MatchCase(string original, string replacement) =>
        original.Length > 0 && char.IsUpper(original[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
}
