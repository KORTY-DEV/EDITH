using System.Text.RegularExpressions;

namespace Edith.Speech;

/// <summary>
/// Converts currency amounts only for spoken text. UI and conversation text
/// retain their compact numeric representation.
/// </summary>
public static class RussianSpeechText
{
    private static readonly Regex CurrencyAmount = new(
        @"(?<![\d\p{L}])(?<amount>\d{1,3}(?:[ \u00A0]\d{3})+|\d+)\s*(?<currency>руб(?:\.|ль|ля|лей)?|₽)(?![\p{L}])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string[] UnitsMale =
    [
        "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать",
        "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"
    ];

    private static readonly string[] Tens =
    ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];

    private static readonly string[] Hundreds =
    ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];

    public static string NormalizeCurrency(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        return CurrencyAmount.Replace(text, match =>
        {
            string digits = match.Groups["amount"].Value.Replace(" ", "").Replace("\u00A0", "");
            if (!long.TryParse(digits, out long amount) || amount < 1_000 || amount > 999_999_999)
            {
                return match.Value;
            }

            return ToWords(amount) + " " + Rubles(amount);
        });
    }

    private static string ToWords(long number)
    {
        var words = new List<string>();
        int millions = (int)(number / 1_000_000);
        int thousands = (int)(number / 1_000 % 1_000);
        int rest = (int)(number % 1_000);

        AddGroup(words, millions, false, "миллион", "миллиона", "миллионов");
        AddGroup(words, thousands, true, "тысяча", "тысячи", "тысяч");
        AddGroup(words, rest, false, "", "", "");

        return string.Join(" ", words);
    }

    private static void AddGroup(
        List<string> words,
        int value,
        bool feminine,
        string singular,
        string few,
        string many)
    {
        if (value == 0) return;

        int hundreds = value / 100;
        int remainder = value % 100;
        if (hundreds > 0) words.Add(Hundreds[hundreds]);

        if (remainder >= 20)
        {
            words.Add(Tens[remainder / 10]);
            remainder %= 10;
        }

        if (remainder > 0)
        {
            if (feminine && remainder is 1 or 2)
            {
                words.Add(remainder == 1 ? "одна" : "две");
            }
            else
            {
                words.Add(UnitsMale[remainder]);
            }
        }

        if (!string.IsNullOrEmpty(singular)) words.Add(Form(value, singular, few, many));
    }

    private static string Rubles(long amount) => Form(amount, "рубль", "рубля", "рублей");

    private static string Form(long value, string singular, string few, string many)
    {
        long lastTwo = value % 100;
        if (lastTwo is >= 11 and <= 14) return many;

        return (value % 10) switch
        {
            1 => singular,
            2 or 3 or 4 => few,
            _ => many
        };
    }
}
