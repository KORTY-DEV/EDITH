using System.Globalization;
using System.Text.RegularExpressions;

namespace Edith.Core;

/// <summary>
/// Arithmetic said aloud: "сколько будет две тысячи двадцать шесть минус тысяча девятьсот девяносто девять". A language model gets such sums
/// wrong (it once answered 2027 for 2026 − 1999), so the arithmetic is done here, exactly, and only when the whole phrase is understood.
/// Numbers are digits or Russian number words; operations: плюс, минус, умножить на, разделить на (and × ÷ + − * /); × and ÷ bind first.
/// </summary>
public static class SpokenMath
{
    private static readonly Regex Lead = new(
        @"^(?:сколько\s+будет|сколько\s+получится|чему\s+равно|чему\s+равен|сколько\s+это|посчитай|подсчитай|вычисли|реши|посчитай\s+мне|сколько)\s+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Dictionary<string, long> Units = new()
    {
        ["ноль"] = 0, ["нуль"] = 0, ["один"] = 1, ["одна"] = 1, ["одно"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5,
        ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10, ["одиннадцать"] = 11, ["двенадцать"] = 12, ["тринадцать"] = 13,
        ["четырнадцать"] = 14, ["пятнадцать"] = 15, ["шестнадцать"] = 16, ["семнадцать"] = 17, ["восемнадцать"] = 18, ["девятнадцать"] = 19,
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50, ["шестьдесят"] = 60, ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["сто"] = 100, ["двести"] = 200, ["триста"] = 300, ["четыреста"] = 400, ["пятьсот"] = 500, ["шестьсот"] = 600, ["семьсот"] = 700, ["восемьсот"] = 800, ["девятьсот"] = 900,
    };

    private static readonly Dictionary<string, long> Scales = new()
    {
        ["тысяча"] = 1_000, ["тысячи"] = 1_000, ["тысяч"] = 1_000, ["тыща"] = 1_000, ["тысячу"] = 1_000,
        ["миллион"] = 1_000_000, ["миллиона"] = 1_000_000, ["миллионов"] = 1_000_000,
        ["миллиард"] = 1_000_000_000, ["миллиарда"] = 1_000_000_000, ["миллиардов"] = 1_000_000_000,
    };

    /// <summary>True with the answer text when <paramref name="phrase"/> is a complete spoken arithmetic question.</summary>
    public static bool TryAnswer(string phrase, out string answer)
    {
        answer = "";
        string text = phrase.Trim().ToLowerInvariant().Replace('ё', 'е').TrimEnd('.', '?', '!', ' ');
        text = Regex.Replace(text, @"(?<!\d),|,(?!\d)|;", " "); // a comma between digits is a decimal comma
        Match lead = Lead.Match(text);
        if (!lead.Success)
            return false;
        text = text[lead.Length..];

        // Symbols to words.
        text = text.Replace("+", " плюс ").Replace("−", " минус ").Replace("×", " умножить на ").Replace("÷", " разделить на ")
            .Replace("*", " умножить на ").Replace("/", " разделить на ");
        text = Regex.Replace(text, @"(?<=\d)\s*-\s*(?=\d)", " минус ");
        text = Regex.Replace(text, @"(умножить|умножь|помножить|помножь)\s+на", "умножить");
        text = Regex.Replace(text, @"(разделить|раздели|делить|подели)\s+на", "разделить");
        text = Regex.Replace(text, @"\bумножь\b|\bпомножить\b|\bпомножь\b", "умножить");
        text = Regex.Replace(text, @"\bраздели\b|\bделить\b|\bподели\b", "разделить");

        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var numbers = new List<decimal>();
        var operators = new List<char>();
        var pending = new List<string>();
        bool expectNumber = true;

        bool Flush()
        {
            if (pending.Count == 0)
                return false;
            if (!TryNumber(pending, out decimal value))
                return false;
            numbers.Add(value);
            pending.Clear();
            return true;
        }

        foreach (string token in tokens)
        {
            char? op = token switch { "плюс" => '+', "минус" => '-', "умножить" => '*', "разделить" => '/', _ => null };
            if (op is { } c)
            {
                if (!Flush())
                    return false;
                operators.Add(c);
                continue;
            }

            if (token == "и" && pending.Count > 0)
                continue;
            pending.Add(token);
            expectNumber = false;
        }

        if (!Flush() || expectNumber || operators.Count == 0 || numbers.Count != operators.Count + 1)
            return false;

        try
        {
            decimal result = Evaluate(numbers, operators);
            answer = Format(result);
            return true;
        }
        catch (DivideByZeroException)
        {
            answer = "На ноль делить нельзя.";
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal Evaluate(List<decimal> numbers, List<char> operators)
    {
        // × and ÷ first.
        var values = new List<decimal> { numbers[0] };
        var adds = new List<char>();
        for (int i = 0; i < operators.Count; i++)
        {
            decimal next = numbers[i + 1];
            switch (operators[i])
            {
                case '*':
                    values[^1] = decimal.Multiply(values[^1], next);
                    break;
                case '/':
                    values[^1] = decimal.Divide(values[^1], next);
                    break;
                default:
                    adds.Add(operators[i]);
                    values.Add(next);
                    break;
            }
        }

        decimal sum = values[0];
        for (int i = 0; i < adds.Count; i++)
            sum = adds[i] == '+' ? sum + values[i + 1] : sum - values[i + 1];
        return sum;
    }

    public static string Format(decimal value)
    {
        value = Math.Round(value, 6, MidpointRounding.AwayFromZero);
        string text = value == Math.Truncate(value)
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.######", CultureInfo.InvariantCulture).Replace('.', ',');
        return text;
    }

    /// <summary>A number from digits ("2026", "3,5") or from words ("две тысячи двадцать шесть"); false when something is not a number.</summary>
    public static bool TryNumber(IReadOnlyList<string> words, out decimal value)
    {
        value = 0;
        if (words.Count == 1 && Regex.IsMatch(words[0], @"^\d+(?:[.,]\d+)?$"))
            return decimal.TryParse(words[0].Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out value);

        long total = 0, current = 0;
        bool any = false;
        foreach (string word in words)
        {
            if (Units.TryGetValue(word, out long unit))
            {
                current += unit;
                any = true;
            }
            else if (Scales.TryGetValue(word, out long scale))
            {
                total += (current == 0 ? 1 : current) * scale;
                current = 0;
                any = true;
            }
            else if (Regex.IsMatch(word, @"^\d+$"))
            {
                current += long.Parse(word, CultureInfo.InvariantCulture);
                any = true;
            }
            else
            {
                return false;
            }
        }

        value = total + current;
        return any;
    }
}
