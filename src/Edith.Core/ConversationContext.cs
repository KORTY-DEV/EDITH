using System.Text.RegularExpressions;
using Edith.Core.Memory;

namespace Edith.Core;

public sealed class ConversationContext
{
    private readonly Queue<(string User, string Assistant)> _turns = new();
    public string? LatestUser => _turns.Count == 0 ? null : _turns.Last().User;
    public void Add(string user, string assistant)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(assistant)) return;
        _turns.Enqueue((user.Trim(), assistant.Trim()));
        while (_turns.Count > 4) _turns.Dequeue();
    }
    public string BuildForPrompt(int maxChars = 700) => BuildForPrompt(null, maxChars);

    public string BuildForPrompt(string? currentInput, int maxChars = 700)
    {
        IEnumerable<(string User, string Assistant)> turns = _turns;
        if (!string.IsNullOrWhiteSpace(currentInput) && _turns.Count > 0)
        {
            string normalized = LocalMemoryStore.Normalize(currentInput);
            bool followUp = Regex.IsMatch(normalized, @"\b(она|он|это|эта|этот|они|сколько|цена|характеристик)\b");
            // She asked something, or the phrase is short ("ничего", "да, давай", "помогаю улучшать тебя"):
            // it is an answer to the last exchange and needs it.
            bool answersHer = _turns.Last().Assistant.TrimEnd().EndsWith('?') ||
                normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 3;
            if (followUp || answersHer)
            {
                turns = _turns.TakeLast(2);
            }
            else
            {
                string[] terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(x => x.Length > 3)
                    .Distinct()
                    .ToArray();
                turns = _turns.Where(x => terms.Any(term => LocalMemoryStore.Normalize(x.User).Contains(term)));
            }
        }

        string text = string.Join(Environment.NewLine, turns.Select(x => "Пользователь: " + x.User + Environment.NewLine + "EDITH: " + x.Assistant));
        return text.Length <= maxChars ? text : text[^maxChars..];
    }
}
