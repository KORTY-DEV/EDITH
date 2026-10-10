using System.Globalization;
using System.Text.RegularExpressions;

namespace Edith.Core.Time;

/// <summary>
/// Spoken Russian time expressions shared by shutdown and reminders:
/// "через 4 часа", "через полтора часа", "через двадцать минут", "в 23:30", "завтра в 9".
/// </summary>
public static class SpokenTime
{
    /// <summary>"через ..." amount and unit; the amount may be a number, a word or empty ("через час").</summary>
    public const string DelayPattern =
        @"через\s+(?<amount>[\p{L}\d.,]+(?:\s+(?:с\s+)?половиной)?)?\s*(?<unit>минут[уы]?|час(?:а|ов)?|секунд[уы]?)";

    /// <summary>"[сегодня|завтра|послезавтра] в HH[:MM] [часов]".</summary>
    public const string AtPattern =
        @"(?:(?<day>сегодня|завтра|послезавтра)\s+)?в\s+(?<h>\d{1,2})(?:[:.\s](?<m>\d{2}))?(?:\s+(?:часов|часа|час)\b)?(?:\s+(?<part>утра|дня|вечера|ночи))?";

    private static readonly Dictionary<string, double> Numbers = new()
    {
        ["один"] = 1, ["одну"] = 1, ["полтора"] = 1.5, ["полторы"] = 1.5, ["два"] = 2, ["две"] = 2, ["три"] = 3,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["пятнадцать"] = 15, ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["сорок пять"] = 45, ["пятьдесят"] = 50,
    };

    /// <summary>Lowercase, ё→е, "полчаса" → "30 минут", "четверть часа" → "15 минут".</summary>
    public static string Normalize(string text)
    {
        string input = Regex.Replace(text.Trim().ToLowerInvariant().Replace('ё', 'е'), @"[!?,]", " ");
        input = Regex.Replace(input, @"\s+", " ");
        input = input.Replace("полчаса", "30 минут", StringComparison.Ordinal)
                     .Replace("четверть часа", "15 минут", StringComparison.Ordinal);
        return input.Trim();
    }

    /// <summary>Delay from a <see cref="DelayPattern"/> match; between 10 seconds and 7 days.</summary>
    public static bool TryDelay(Match match, out TimeSpan delay, TimeSpan? max = null)
    {
        delay = default;
        string amountText = match.Groups["amount"].Value.Trim();
        string unit = match.Groups["unit"].Value;

        bool half = amountText.EndsWith("половиной", StringComparison.Ordinal);
        string number = Regex.Replace(amountText, @"\s*(?:с\s+)?половиной$", "").Trim();

        double amount;
        if (number.Length == 0)
            amount = 1;
        else if (!Numbers.TryGetValue(number, out amount) &&
                 !double.TryParse(number.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out amount))
            return false;

        if (half)
            amount += 0.5;

        delay = unit.StartsWith("час", StringComparison.Ordinal) ? TimeSpan.FromHours(amount)
              : unit.StartsWith("секунд", StringComparison.Ordinal) ? TimeSpan.FromSeconds(amount)
              : TimeSpan.FromMinutes(amount);

        return delay >= TimeSpan.FromSeconds(10) && delay <= (max ?? TimeSpan.FromDays(7));
    }

    /// <summary>
    /// Absolute time from an <see cref="AtPattern"/> match. Without a day word the next
    /// occurrence is used; "вечера/дня" move 1-11 into the afternoon.
    /// </summary>
    public static bool TryAt(Match match, DateTimeOffset now, out DateTimeOffset at)
    {
        at = default;
        if (!int.TryParse(match.Groups["h"].Value, out int hour) || hour is < 0 or > 23)
            return false;

        int minute = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute is < 0 or > 59)
            return false;

        string part = match.Groups["part"].Value;
        if (part is "дня" or "вечера" && hour is >= 1 and <= 11)
            hour += 12;
        if (part == "ночи" && hour == 12)
            hour = 0;

        string day = match.Groups["day"].Value;
        int days = day switch { "завтра" => 1, "послезавтра" => 2, _ => 0 };

        at = new DateTimeOffset(now.Year, now.Month, now.Day, hour, minute, 0, now.Offset).AddDays(days);
        if (day.Length == 0 && at <= now)
            at = at.AddDays(1);

        return day != "сегодня" || at > now;
    }

    /// <summary>"в 18:30", "завтра в 09:00", "в пятницу в 10:00" for a spoken confirmation.</summary>
    public static string Describe(DateTimeOffset at, DateTimeOffset now)
    {
        string clock = at.ToString("HH:mm", CultureInfo.InvariantCulture);
        int days = (at.Date - now.Date).Days;
        return days switch
        {
            0 => $"в {clock}",
            1 => $"завтра в {clock}",
            2 => $"послезавтра в {clock}",
            _ => $"{at.ToString("d MMMM", CultureInfo.GetCultureInfo("ru-RU"))} в {clock}",
        };
    }
}
