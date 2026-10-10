using System.Text.RegularExpressions;

namespace Edith.Core;

public static class ProductGroundingPolicy
{
    public static string Apply(string answer, string query, ConversationEntity? entity)
    {
        bool productQuery = query.Contains("видеокарт", StringComparison.OrdinalIgnoreCase) || query.Contains("gpu", StringComparison.OrdinalIgnoreCase);
        if (!productQuery) return answer;
        if (entity is null)
            return "Не удалось надёжно подтвердить актуальную модель видеокарты по свежим авторитетным источникам.";
        if (answer.TrimStart().StartsWith("{", StringComparison.Ordinal) || answer.Contains("\"description\"", StringComparison.OrdinalIgnoreCase) || answer.Contains("\"title\"", StringComparison.OrdinalIgnoreCase))
            return $"Сейчас по доступным независимым тестам лидирует {entity.CanonicalName}.";
        MatchCollection products = Regex.Matches(answer, @"\bRTX\s+\d{4}\b", RegexOptions.IgnoreCase);
        string canonicalModel = entity.CanonicalName.Replace("NVIDIA GeForce ", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.OrdinalIgnoreCase);
        bool hasUnsupported = products.Cast<Match>().Any(x => !x.Value.Replace(" ", "", StringComparison.OrdinalIgnoreCase).Equals(canonicalModel, StringComparison.OrdinalIgnoreCase));
        if (hasUnsupported)
            return $"Сейчас подтверждённый вариант — {entity.CanonicalName}. Точную актуальную цену нужно сверить по свежим российским предложениям.";
        if (answer.Contains("не подтвержд", StringComparison.OrdinalIgnoreCase) || answer.Contains("не удалось подтвердить", StringComparison.OrdinalIgnoreCase))
            return $"Сейчас по доступным независимым тестам лидирует {entity.CanonicalName}.";
        if (!answer.Contains(entity.CanonicalName, StringComparison.OrdinalIgnoreCase))
            return $"Сейчас по доступным независимым тестам лидирует {entity.CanonicalName}.";
        return Regex.IsMatch(answer.TrimEnd(), @"[.!?…]$") ? answer : answer.TrimEnd() + ".";
    }
}
