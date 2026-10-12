using System.Text.RegularExpressions;

namespace Edith.Speech;

/// <summary>
/// Digits for the voice: what a person says out loud. Years and dates in the right case ("в две тысячи двадцать шестом
/// году", "десятого октября"), times ("двадцать один тридцать"), temperatures ("плюс четырнадцать градусов"),
/// percent, decimals, memory sizes, and plain numbers with the feminine one/two where the noun asks for it.
/// What this does not know it leaves alone: the answer is better read with a stray digit than with a wrong word.
/// </summary>
public static class SpokenNumbers
{
    private static readonly string[] UnitsM =
    ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать",
     "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];

    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];

    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];

    // Ordinals, masculine nominative.
    private static readonly string[] OrdUnits =
    ["нулевой", "первый", "второй", "третий", "четвёртый", "пятый", "шестой", "седьмой", "восьмой", "девятый", "десятый", "одиннадцатый",
     "двенадцатый", "тринадцатый", "четырнадцатый", "пятнадцатый", "шестнадцатый", "семнадцатый", "восемнадцатый", "девятнадцатый"];

    private static readonly string[] OrdTens = ["", "", "двадцатый", "тридцатый", "сороковой", "пятидесятый", "шестидесятый", "семидесятый", "восьмидесятый", "девяностый"];

    private static readonly string[] MonthsGenitive =
    ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    private static readonly Regex Year = new(@"(?<![\d.,:])(?<y>(?:19|20)\d{2})(?<sp>\s*)(?<w>году|года|год|г\.)(?![\p{L}])(?<stop>(?<=г\.)(?=\s+(?-i:[А-ЯЁA-Z])))?", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DayMonth = new(@"(?<![\d.,:])(?<d>[1-9]|[12]\d|3[01])\s+(?<m>" + string.Join("|", MonthsGenitive) + @")(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Clock = new(@"(?<![\d.,:])(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)(?![\d:])", RegexOptions.Compiled);
    private static readonly Regex Signed = new(@"(?<![\p{L}\d.,])(?<sign>[+\-−–])(?<n>\d+(?:[.,]\d+)?)(?:(?<deg>\s*°\s*[CcСс]?(?![\p{L}]))|(?=\s*градус))", RegexOptions.Compiled);
    private static readonly Regex Degrees = new(@"(?<n>\d+(?:[.,]\d+)?)\s*°\s*[CcСс]?(?![\p{L}])", RegexOptions.Compiled);
    private static readonly Regex Percent = new(@"(?<n>\d+(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);
    private static readonly Regex Size = new(@"(?<n>\d+(?:[.,]\d+)?)\s*(?<u>ГБ|МБ|ТБ|КБ|GB|MB|TB|KB|ГГц|МГц|GHz|MHz)(?![\p{L}])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Number = new(@"(?<prep>(?<![\p{L}])(?:около|до|от|из|для|без|после|свыше|менее|более|порядка)\s+)?(?<![\p{L}\d.,])(?<n>\d{1,3}(?:[  ]\d{3})+|\d+)(?:[.,](?<f>\d{1,2}))?(?![\d\p{L}]|:\d)(?=(?<next>\s+\p{L}+))?", RegexOptions.Compiled);

    private static readonly Regex Grouped = new(@"(?<![\p{L}\d.,])(?<n>\d{1,3}(?:[   ]\d{3})+)(?![\d])", RegexOptions.Compiled);
    private static readonly Regex NumericDate = new(@"(?<![\d.])(?<d>0?[1-9]|[12]\d|3[01])[./](?<m>0?[1-9]|1[0-2])[./](?<y>(?:19|20)\d{2})(?!\d|\.\d)", RegexOptions.Compiled);
    private static readonly Regex Version = new(@"(?<![\p{L}\d.,])(?<v>\d+(?:\.\d+){2,})(?!\d|\.\d)", RegexOptions.Compiled);
    private static readonly Regex Decade = new(@"(?<![\p{L}\d.,])(?<y>(?:19|20)\d0)-х(?![\p{L}])", RegexOptions.Compiled);
    private static readonly Regex OrdinalSuffix = new(@"(?<![\p{L}\d.,])(?<n>\d{1,2})-(?<s>й|го|м)(?![\p{L}])", RegexOptions.Compiled);
    private static readonly Regex SignedPlain = new(@"(?<=^|[\s(])(?<sign>[+\-−])(?<n>\d+(?:[.,]\d+)?)(?![\d\p{L}\-])", RegexOptions.Compiled);
    private static readonly Regex Rouble = new(@"(?<n>\d+(?:[.,]\d+)?)\s*(?:₽|руб\.?(?![\p{L}]))", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GpuModel = new(@"(?<![\p{L}])(?<p>RTX|GTX|RX|GT)\s+(?<a>[1-9]\d)(?<b>\d{2})(?![\d])", RegexOptions.Compiled);
    private static readonly Regex DigitsLetters = new(@"(?<![\p{L}\d.,])(?<n>\d{1,4})(?<l>[A-Za-z]{1,3})(?![\p{L}\d])", RegexOptions.Compiled);

    private static readonly Regex Fraction = new(@"(?<![\d/.,\p{L}])(?<a>[1-3])/(?<b>[2-4])(?![\d/.,])", RegexOptions.Compiled);

    public static string Apply(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Any(char.IsDigit))
            return text;

        // "8 192 МБ", "199 999": the groups of three are one number, whatever follows.
        text = Grouped.Replace(text, m => System.Text.RegularExpressions.Regex.Replace(m.Groups["n"].Value, @"[   ]", ""));

        text = NumericDate.Replace(text, m =>
            Ordinal(int.Parse(m.Groups["d"].Value), "gen") + " " + MonthsGenitive[int.Parse(m.Groups["m"].Value) - 1] + " " +
            YearWords(int.Parse(m.Groups["y"].Value), "gen") + " года");

        // "1.0.7" is a version: the dots are said.
        text = Version.Replace(text, m => string.Join(" точка ", m.Groups["v"].Value.Split('.').Select(part => Cardinal(long.Parse(part), false))));

        text = Decade.Replace(text, m =>
        {
            int year = int.Parse(m.Groups["y"].Value);
            string tens = OrdTens[year % 100 / 10];
            string hundreds = Cardinal(year / 100 * 100, false);
            if (hundreds.StartsWith("одна тысяча", StringComparison.Ordinal))
                hundreds = hundreds[5..]; // "тысяча девятьсот девяностых", not "одна тысяча"
            return hundreds + " " + tens[..^2] + "ых";
        });

        text = OrdinalSuffix.Replace(text, m =>
        {
            int n = int.Parse(m.Groups["n"].Value);
            if (n is < 1 or > 99)
                return m.Value;
            return Ordinal(n, m.Groups["s"].Value switch { "го" => "gen", "м" => "prep", _ => "nom" });
        });

        text = Rouble.Replace(text, m => m.Groups["n"].Value + " " + Plural(IntegerPart(m.Groups["n"].Value), "рубль", "рубля", "рублей"));

        text = Year.Replace(text, m =>
        {
            int year = int.Parse(m.Groups["y"].Value);
            string word = m.Groups["w"].Value.ToLowerInvariant();
            string @case = word.StartsWith("году") ? "prep" : word.StartsWith("года") || word.StartsWith("г.") ? "gen" : "nom";
            // "в 2026 г. Он приехал": the full stop after "г." also ends the sentence.
            return YearWords(year, @case) + " " + (word.StartsWith("г.") ? "года" : word) + (m.Groups["stop"].Success ? "." : "");
        });

        text = DayMonth.Replace(text, m => Ordinal(int.Parse(m.Groups["d"].Value), "gen") + " " + m.Groups["m"].Value);

        text = Clock.Replace(text, m =>
        {
            int hours = int.Parse(m.Groups["h"].Value), minutes = int.Parse(m.Groups["m"].Value);
            string h = Cardinal(hours, false);
            string min = minutes == 0 ? "ровно" : minutes < 10 ? "ноль " + Cardinal(minutes, false) : Cardinal(minutes, false);
            return hours == 0 && minutes > 0 ? "ноль ноль " + min.Replace("ноль ", "") : h + " " + min;
        });

        text = Signed.Replace(text, m =>
            (m.Groups["sign"].Value == "+" ? "плюс " : "минус ") + Decimal(m.Groups["n"].Value) +
            (m.Groups["deg"].Success ? " " + Plural(IntegerPart(m.Groups["n"].Value), "градус", "градуса", "градусов") : ""));

        text = SignedPlain.Replace(text, m =>
            (m.Groups["sign"].Value == "+" ? "плюс " : "минус ") + Decimal(m.Groups["n"].Value));

        text = Degrees.Replace(text, m => Decimal(m.Groups["n"].Value) + " " + Plural(IntegerPart(m.Groups["n"].Value), "градус", "градуса", "градусов"));
        text = Percent.Replace(text, m => Decimal(m.Groups["n"].Value) + " " + Plural(IntegerPart(m.Groups["n"].Value), "процент", "процента", "процентов"));

        text = Size.Replace(text, m =>
        {
            string unit = m.Groups["u"].Value.ToUpperInvariant();
            long whole = IntegerPart(m.Groups["n"].Value);
            string word = unit switch
            {
                "ГБ" or "GB" => Plural(whole, "гигабайт", "гигабайта", "гигабайт"),
                "МБ" or "MB" => Plural(whole, "мегабайт", "мегабайта", "мегабайт"),
                "ТБ" or "TB" => Plural(whole, "терабайт", "терабайта", "терабайт"),
                "КБ" or "KB" => Plural(whole, "килобайт", "килобайта", "килобайт"),
                "ГГЦ" or "GHZ" => Plural(whole, "гигагерц", "гигагерца", "гигагерц"),
                _ => Plural(whole, "мегагерц", "мегагерца", "мегагерц"),
            };
            return Decimal(m.Groups["n"].Value) + " " + word;
        });

        text = Fraction.Replace(text, m => (m.Groups["a"].Value + "/" + m.Groups["b"].Value) switch
        {
            "1/2" => "одна вторая",
            "1/3" => "одна треть",
            "2/3" => "две трети",
            "1/4" => "одна четверть",
            "3/4" => "три четверти",
            _ => m.Value,
        });

        // Video cards are said in pairs: "RTX 5090" is "пятьдесят девяносто", not "пять тысяч девяносто".
        text = GpuModel.Replace(text, m => m.Groups["p"].Value + " " + Cardinal(int.Parse(m.Groups["a"].Value), false) + " " + Cardinal(int.Parse(m.Groups["b"].Value), false));
        // "3600X", "8b": the number and the letter are two words.
        text = DigitsLetters.Replace(text, m => m.Groups["n"].Value + " " + m.Groups["l"].Value);

        return Number.Replace(text, m =>
        {
            string digits = m.Groups["n"].Value.Replace(" ", "").Replace(" ", "");
            if (digits.Length > 12 || !long.TryParse(digits, out long value))
                return m.Value;

            string next = m.Groups["next"].Success ? m.Groups["next"].Value : "";
            bool feminine = IsFeminine(next.Trim());
            // "около 16 градусов", "от 5 до 7": the number stands in the genitive after these words.
            if (m.Groups["prep"].Success && !m.Groups["f"].Success)
                return m.Groups["prep"].Value + string.Join(' ', Cardinal(value, feminine).Split(' ').Select(Genitive));
            string words = m.Groups["f"].Success && m.Groups["f"].Value == "5" && value >= 1 && HalfUnit(next.Trim())
                ? (value == 1 ? (feminine ? "полторы" : "полтора") : Cardinal(value, feminine) + " с половиной")
                : m.Groups["f"].Success
                    ? Decimal(digits + "," + m.Groups["f"].Value)
                    : Cardinal(value, feminine);
            return words;
        });
    }

    /// <summary>"1,5 часа" is "полтора часа", "2,5 метра" is "два с половиной метра".</summary>
    private static bool HalfUnit(string word)
    {
        string w = word.ToLowerInvariant();
        return w.StartsWith("час", StringComparison.Ordinal) || w.StartsWith("минут", StringComparison.Ordinal) || w.StartsWith("секунд", StringComparison.Ordinal) ||
               w.StartsWith("дн", StringComparison.Ordinal) || w.StartsWith("недел", StringComparison.Ordinal) || w.StartsWith("месяц", StringComparison.Ordinal) ||
               w.StartsWith("год", StringComparison.Ordinal) || w.StartsWith("лет", StringComparison.Ordinal) || w.StartsWith("метр", StringComparison.Ordinal) ||
               w.StartsWith("литр", StringComparison.Ordinal) || w.StartsWith("килограмм", StringComparison.Ordinal) || w.StartsWith("кг", StringComparison.Ordinal);
    }

    private static readonly Dictionary<string, string> GenitiveWords = new()
    {
        ["ноль"] = "ноля", ["один"] = "одного", ["одна"] = "одной", ["два"] = "двух", ["две"] = "двух", ["три"] = "трёх", ["четыре"] = "четырёх",
        ["пять"] = "пяти", ["шесть"] = "шести", ["семь"] = "семи", ["восемь"] = "восьми", ["девять"] = "девяти", ["десять"] = "десяти",
        ["одиннадцать"] = "одиннадцати", ["двенадцать"] = "двенадцати", ["тринадцать"] = "тринадцати", ["четырнадцать"] = "четырнадцати",
        ["пятнадцать"] = "пятнадцати", ["шестнадцать"] = "шестнадцати", ["семнадцать"] = "семнадцати", ["восемнадцать"] = "восемнадцати",
        ["девятнадцать"] = "девятнадцати", ["двадцать"] = "двадцати", ["тридцать"] = "тридцати", ["сорок"] = "сорока", ["пятьдесят"] = "пятидесяти",
        ["шестьдесят"] = "шестидесяти", ["семьдесят"] = "семидесяти", ["восемьдесят"] = "восьмидесяти", ["девяносто"] = "девяноста", ["сто"] = "ста",
        ["двести"] = "двухсот", ["триста"] = "трёхсот", ["четыреста"] = "четырёхсот", ["пятьсот"] = "пятисот", ["шестьсот"] = "шестисот",
        ["семьсот"] = "семисот", ["восемьсот"] = "восьмисот", ["девятьсот"] = "девятисот",
        ["тысяча"] = "тысячи", ["тысячи"] = "тысяч", ["тысяч"] = "тысяч", ["миллион"] = "миллиона", ["миллиона"] = "миллионов", ["миллионов"] = "миллионов",
        ["миллиард"] = "миллиарда", ["миллиарда"] = "миллиардов", ["миллиардов"] = "миллиардов",
    };

    private static string Genitive(string word) => GenitiveWords.TryGetValue(word, out string? g) ? g : word;

    private static bool IsFeminine(string word)
    {
        string w = word.ToLowerInvariant();
        return w.StartsWith("минут", StringComparison.Ordinal) || w.StartsWith("секунд", StringComparison.Ordinal) || w.StartsWith("недел", StringComparison.Ordinal) ||
               w.StartsWith("копе", StringComparison.Ordinal) || w.StartsWith("штук", StringComparison.Ordinal) || w.StartsWith("строк", StringComparison.Ordinal) ||
               w.StartsWith("страниц", StringComparison.Ordinal) || w.StartsWith("ночь", StringComparison.Ordinal) || w.StartsWith("ночи", StringComparison.Ordinal) ||
               w.StartsWith("вещ", StringComparison.Ordinal) || w.StartsWith("задач", StringComparison.Ordinal) || w.StartsWith("заметк", StringComparison.Ordinal) ||
               w.StartsWith("карточ", StringComparison.Ordinal) || w.StartsWith("игр", StringComparison.Ordinal) || w.StartsWith("статья", StringComparison.Ordinal) ||
               w.StartsWith("стать", StringComparison.Ordinal);
    }

    private static long IntegerPart(string number)
    {
        string whole = number.Split('.', ',')[0];
        return long.TryParse(whole, out long v) ? v : 0;
    }

    /// <summary>"14,5" → "четырнадцать целых пять десятых"; "14" → "четырнадцать".</summary>
    public static string Decimal(string number)
    {
        string[] parts = number.Replace('.', ',').Split(',');
        long whole = long.TryParse(parts[0], out long w) ? w : 0;
        if (parts.Length == 1)
            return Cardinal(whole, false);

        string fraction = parts[1].TrimEnd('0');
        if (fraction.Length == 0)
            return Cardinal(whole, false);

        long fractional = long.Parse(fraction);
        bool oneWhole = whole % 10 == 1 && whole % 100 != 11;
        string wholeWord = oneWhole ? "целая" : "целых";
        string unit = fraction.Length == 1
            ? Plural(fractional, "десятая", "десятых", "десятых")
            : Plural(fractional, "сотая", "сотых", "сотых");
        return $"{Cardinal(whole, true)} {wholeWord} {Cardinal(fractional, true)} {unit}";
    }

    public static string Cardinal(long number, bool feminine)
    {
        if (number == 0)
            return "ноль";

        var words = new List<string>();
        long billions = number / 1_000_000_000 % 1000, millions = number / 1_000_000 % 1000, thousands = number / 1000 % 1000, rest = number % 1000;
        Group(words, billions, false, "миллиард", "миллиарда", "миллиардов");
        Group(words, millions, false, "миллион", "миллиона", "миллионов");
        Group(words, thousands, true, "тысяча", "тысячи", "тысяч");
        Group(words, rest, feminine, "", "", "");
        return string.Join(" ", words);
    }

    private static void Group(List<string> words, long value, bool feminine, string one, string few, string many)
    {
        if (value == 0)
            return;

        int h = (int)(value / 100), r = (int)(value % 100);
        if (h > 0)
            words.Add(Hundreds[h]);
        if (r >= 20)
        {
            words.Add(Tens[r / 10]);
            r %= 10;
        }

        if (r > 0)
            words.Add(feminine && r == 1 ? "одна" : feminine && r == 2 ? "две" : UnitsM[r]);

        if (one.Length > 0)
            words.Add(Plural(value, one, few, many));
    }

    /// <summary>"две тысячи двадцать шестой" (nom), "…шестого" (gen), "…шестом" (prep).</summary>
    public static string YearWords(int year, string @case)
    {
        int thousands = year / 1000 * 1000;
        int rest = year % 100;
        int hundreds = year % 1000 - rest;
        string head = Cardinal(thousands + hundreds, false);
        if (rest == 0)
            return head;

        string tail = rest < 20 ? Ordinal(rest, @case) : rest % 10 == 0 ? Ordinal(rest, @case) : Tens[rest / 10] + " " + Ordinal(rest % 10, @case);
        return head + " " + tail;
    }

    public static string Ordinal(int number, string @case)
    {
        string nominative = number < 20 ? OrdUnits[number] : number % 10 == 0 ? OrdTens[number / 10] : Tens[number / 10] + " " + OrdUnits[number % 10];
        if (@case == "nom")
            return nominative;

        int space = nominative.LastIndexOf(' ');
        string lead = space >= 0 ? nominative[..(space + 1)] : "";
        string word = space >= 0 ? nominative[(space + 1)..] : nominative;
        string stem;
        string ending;
        if (word.EndsWith("ий", StringComparison.Ordinal))
        {
            stem = word[..^2];
            ending = @case == "gen" ? "ьего" : "ьем";
        }
        else
        {
            stem = word[..^2];
            ending = @case == "gen" ? "ого" : "ом";
        }

        return lead + stem + ending;
    }

    public static string Plural(long n, string one, string few, string many)
    {
        long mod100 = n % 100, mod10 = n % 10;
        if (mod100 is >= 11 and <= 14)
            return many;
        return mod10 == 1 ? one : mod10 is >= 2 and <= 4 ? few : many;
    }
}
