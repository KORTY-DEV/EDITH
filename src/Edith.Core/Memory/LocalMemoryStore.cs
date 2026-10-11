using System.Text.Json;

namespace Edith.Core.Memory;

/// <summary>
/// Small durable JSON store. EDITH has no database today; this keeps canonical
/// structured data locally without adding a resident database process.
/// </summary>
public sealed class LocalMemoryStore : IMemoryStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private List<MemoryItem>? _items;

    public LocalMemoryStore(string path) => _path = path;

    public MemoryItem Upsert(MemoryType type, string key, string content, string source = "voice")
    {
        key = Normalize(key);
        content = content.Trim();
        lock (_gate)
        {
            List<MemoryItem> items = Load();
            int index = items.FindIndex(x => x.Active && x.Type == type && x.Key == key);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            MemoryItem saved = index >= 0
                ? items[index] with { Content = content, UpdatedAt = now, Source = source }
                : new MemoryItem { Type = type, Key = key, Content = content, Source = source, CreatedAt = now, UpdatedAt = now };
            if (index >= 0) items[index] = saved; else items.Add(saved);
            Persist(items);
            return saved;
        }
    }

    public int Forget(string query)
    {
        string normalized = Normalize(query);
        lock (_gate)
        {
            List<MemoryItem> items = Load();
            int changed = 0;
            for (int i = 0; i < items.Count; i++)
            {
                MemoryItem item = items[i];
                if (item.Active && (item.Key.Contains(normalized) || Normalize(item.Content).Contains(normalized)))
                {
                    items[i] = item with { Active = false, UpdatedAt = DateTimeOffset.UtcNow };
                    changed++;
                }
            }
            if (changed > 0) Persist(items);
            return changed;
        }
    }

    public IReadOnlyList<MemoryItem> Search(string query, int max = 5)
    {
        string[] terms = Tokens(query);
        lock (_gate)
        {
            List<MemoryItem> found = Load().Where(x => x.Active)
                .Select(x => new { Item = x, Score = Score(x, terms) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score).ThenByDescending(x => x.Item.UpdatedAt)
                .Take(Math.Clamp(max, 1, 10)).Select(x => x.Item with { LastUsedAt = DateTimeOffset.UtcNow }).ToList();
            if (found.Count > 0)
            {
                foreach (MemoryItem used in found)
                {
                    int index = _items!.FindIndex(x => x.Id == used.Id);
                    if (index >= 0) _items[index] = used;
                }
                Persist(_items!);
            }
            return found;
        }
    }

    public IReadOnlyList<MemoryItem> ActiveItems() { lock (_gate) return Load().Where(x => x.Active).ToList(); }

    private List<MemoryItem> Load()
    {
        if (_items is not null) return _items;
        _items = SafeJsonFile.Load<List<MemoryItem>>(_path, () => []);
        return _items;
    }

    private void Persist(List<MemoryItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }

    private static int Score(MemoryItem item, IEnumerable<string> terms)
    {
        string text = item.Key + " " + Normalize(item.Content);
        return terms.Sum(term => text.Contains(term) ? term.Length : 0);
    }
    private static string[] Tokens(string text) => Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(x => x.Length > 2).Distinct().ToArray();
    public static string Normalize(string text) => text.Trim().ToLowerInvariant().Replace('ё', 'е');
}
