using System.Text;
using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Digits → Russian words for speech: Silero skips digits entirely ("284 файла" was read as "файла").
/// Nominative case; times ("18:30"), decimals ("12,6") and percents are handled.
/// </summary>
public static class RussianNumbers
{
    private static readonly string[] Units = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] UnitsFeminine = ["ноль", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять"];
    private static readonly string[] Teens = ["десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];

    private static readonly (long Value, string One, string Few, string Many, bool Feminine)[] Scales =
    [
        (1_000_000_000, "миллиард", "миллиарда", "миллиардов", false),
        (1_000_000, "миллион", "миллиона", "миллионов", false),
        (1_000, "тысяча", "тысячи", "тысяч", true),
    ];

    public static string ToWords(long n)
    {
        if (n == 0)
            return "ноль";
        if (n < 0)
            return "минус " + ToWords(-n);

        var words = new List<string>();
        foreach ((long value, string one, string few, string many, bool feminine) in Scales)
        {
            long chunk = n / value;
            if (chunk > 0)
            {
                words.Add(Triplet((int)(chunk % 1000), feminine));
                words.Add(Plural(chunk, one, few, many));
                n %= value;
            }
        }

        if (n > 0)
            words.Add(Triplet((int)n, feminine: false));

        return string.Join(' ', words.Where(w => w.Length > 0));
    }

    public static string Plural(long n, string one, string few, string many)
    {
        long mod100 = n % 100, mod10 = n % 10;
        return mod10 == 1 && mod100 != 11 ? one
            : mod10 is >= 2 and <= 4 && mod100 is < 12 or > 14 ? few
            : many;
    }

    /// <summary>Replaces numbers in running text with words, keeping everything else.</summary>
    public static string Verbalize(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(char.IsDigit))
            return text;

        // Model names are said in pairs: "RTX 5090" → "RTX пятьдесят девяносто".
        text = Regex.Replace(text, @"(?<=\b[A-Za-z]{2,}\s)(\d{2})(\d{2})\b", m =>
            ToWords(int.Parse(m.Groups[1].Value)) + " " +
            (m.Groups[2].Value == "00" ? "сотая" : m.Groups[2].Value[0] == '0' ? "ноль " + ToWords(int.Parse(m.Groups[2].Value)) : ToWords(int.Parse(m.Groups[2].Value))));

        // 18:30 → восемнадцать тридцать; 09:05 → девять ноль пять
        text = Regex.Replace(text, @"\b(\d{1,2}):(\d{2})\b", m =>
        {
            int h = int.Parse(m.Groups[1].Value), min = int.Parse(m.Groups[2].Value);
            string minutes = min == 0 ? "ноль ноль" : min < 10 ? "ноль " + ToWords(min) : ToWords(min);
            return ToWords(h) + " " + minutes;
        });

        // 12,6 / 12.6 → двенадцать и шесть
        text = Regex.Replace(text, @"\b(\d+)[.,](\d{1,2})\b", m =>
        {
            // 1,5 → полтора; 2,5 → два с половиной
            long whole = long.Parse(m.Groups[1].Value);
            if (m.Groups[2].Value == "5")
                return whole == 1 ? "полтора" : ToWords(whole) + " с половиной";
            return ToWords(whole) + " и " + ToWords(long.Parse(m.Groups[2].Value));
        });

        // 45% → сорок пять процентов
        text = Regex.Replace(text, @"\b(\d{1,12})\s?%", m =>
        {
            long n = long.Parse(m.Groups[1].Value);
            return ToWords(n) + " " + Plural(n, "процент", "процента", "процентов");
        });

        // 1 000 000 / 1000 → words
        text = Regex.Replace(text, @"\b\d{1,3}(?:[  ]\d{3})+\b|\b\d{1,15}\b", m =>
            long.TryParse(m.Value.Replace(" ", "").Replace(" ", ""), out long n) ? ToWords(n) : m.Value);

        return text;
    }

    private static string Triplet(int n, bool feminine)
    {
        var sb = new StringBuilder();
        int h = n / 100, rest = n % 100;
        if (h > 0)
            sb.Append(Hundreds[h]).Append(' ');

        if (rest >= 10 && rest < 20)
            sb.Append(Teens[rest - 10]);
        else
        {
            int t = rest / 10, u = rest % 10;
            if (t > 0)
                sb.Append(Tens[t]).Append(' ');
            if (u > 0 || (h == 0 && t == 0 && n == 0))
                sb.Append(feminine ? UnitsFeminine[u] : Units[u]);
        }

        return sb.ToString().Trim();
    }
}
