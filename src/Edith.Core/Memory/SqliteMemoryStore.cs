using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Edith.Core.Memory;

/// <summary>
/// Memory v2: one local SQLite file with structured memories (FTS5 over stemmed text) and the
/// full conversation log. On first open it imports the v1 JSON store, once.
/// Values never leave this file (no diagnostics, no network).
/// </summary>
public sealed class SqliteMemoryStore : IMemoryStore, IConversationLog
{
    private readonly string _connectionString;
    private readonly object _gate = new();

    public SqliteMemoryStore(string databasePath, string? importV1JsonPath = null)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        using SqliteConnection connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS memories (
                id          TEXT PRIMARY KEY,
                type        INTEGER NOT NULL,
                key         TEXT NOT NULL,
                content     TEXT NOT NULL,
                confidence  REAL NOT NULL,
                created     TEXT NOT NULL,
                updated     TEXT NOT NULL,
                last_used   TEXT,
                source      TEXT NOT NULL,
                active      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_memories_key ON memories(type, key, active);
            CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(id UNINDEXED, stems);
            CREATE TABLE IF NOT EXISTS conversation (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                ts         TEXT NOT NULL,
                ts_ms      INTEGER NOT NULL,
                session    INTEGER NOT NULL,
                user_text  TEXT NOT NULL,
                assistant  TEXT NOT NULL,
                route      TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_conversation_ts ON conversation(ts_ms);
            CREATE TABLE IF NOT EXISTS episodes (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                start_ms   INTEGER NOT NULL,
                end_ms     INTEGER NOT NULL,
                offset_min INTEGER NOT NULL,
                summary    TEXT NOT NULL,
                topics     TEXT NOT NULL,
                turns      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_episodes_end ON episodes(end_ms);
            CREATE TABLE IF NOT EXISTS memory_vectors (
                id    TEXT PRIMARY KEY,
                stamp TEXT NOT NULL,
                vec   BLOB NOT NULL
            );
            """);

        if (importV1JsonPath is not null)
            ImportV1Once(connection, importV1JsonPath);
    }

    public MemoryItem Upsert(MemoryType type, string key, string content, string source = "voice")
    {
        key = LocalMemoryStore.Normalize(key);
        content = content.Trim();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            MemoryItem? existing = ReadItems(
                connection,
                "SELECT * FROM memories WHERE active = 1 AND type = $type AND key = $key LIMIT 1",
                command =>
                {
                    command.Transaction = transaction;
                    command.Parameters.AddWithValue("$type", (int)type);
                    command.Parameters.AddWithValue("$key", key);
                }).FirstOrDefault();

            MemoryItem saved = existing is not null
                ? existing with { Content = content, UpdatedAt = now, Source = source }
                : new MemoryItem { Type = type, Key = key, Content = content, Source = source, CreatedAt = now, UpdatedAt = now };

            Write(connection, transaction, saved);
            transaction.Commit();
            return saved;
        }
    }

    public int Forget(string query)
    {
        string normalized = LocalMemoryStore.Normalize(query);
        if (normalized.Length == 0)
            return 0;

        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            List<MemoryItem> matches = ReadItems(connection, "SELECT * FROM memories WHERE active = 1",
                    command => command.Transaction = transaction)
                .Where(item => item.Key.Contains(normalized) || LocalMemoryStore.Normalize(item.Content).Contains(normalized))
                .ToList();

            foreach (MemoryItem item in matches)
                Write(connection, transaction, item with { Active = false, UpdatedAt = DateTimeOffset.UtcNow });

            transaction.Commit();
            return matches.Count;
        }
    }

    /// <summary>Overrides the app-wide embedder for this store (tests).</summary>
    public ITextEmbedder? Embedder { get; set; }

    /// <summary>Words first (stemmed full-text), meaning second (embeddings), merged by rank.</summary>
    public IReadOnlyList<MemoryItem> Search(string query, int max = 5)
    {
        IReadOnlyList<MemoryItem> byWords = SearchWords(query, Math.Clamp(max, 1, 10));
        ITextEmbedder? embedder = Embedder ?? EmbeddingService.Provider();
        if (embedder is null || string.IsNullOrWhiteSpace(query))
            return byWords;

        IReadOnlyList<MemoryItem> byMeaning;
        try
        {
            byMeaning = SearchMeaning(embedder, query, Math.Clamp(max, 1, 10));
        }
        catch
        {
            return byWords; // a broken model must not cost the answer
        }

        if (byMeaning.Count == 0)
            return byWords;

        Dictionary<Guid, MemoryItem> byId = byWords.Concat(byMeaning).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
        return Vectors.Fuse<Guid>([byWords.Select(i => i.Id).ToList(), byMeaning.Select(i => i.Id).ToList()], Math.Clamp(max, 1, 10))
            .Select(id => byId[id])
            .ToList();
    }

    /// <summary>Cosine to the question; below this a fact is not "about" it (e5 scores run high).</summary>
    public const float MeaningThreshold = 0.84f;

    private IReadOnlyList<MemoryItem> SearchMeaning(ITextEmbedder embedder, string query, int max)
    {
        float[] question = embedder.EmbedQuery(query);
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            IReadOnlyList<MemoryItem> active = ReadItems(connection, "SELECT * FROM memories WHERE active = 1", _ => { });
            Dictionary<string, (string Stamp, float[] Vec)> stored = LoadVectors(connection);

            var scored = new List<(MemoryItem Item, float Score)>();
            foreach (MemoryItem item in active)
            {
                string id = item.Id.ToString();
                string stamp = Format(item.UpdatedAt);
                float[] vec;
                if (stored.TryGetValue(id, out var have) && have.Stamp == stamp)
                {
                    vec = have.Vec;
                }
                else
                {
                    vec = embedder.EmbedPassage(item.Key + ": " + item.Content);
                    using SqliteCommand save = connection.CreateCommand();
                    save.CommandText = "INSERT OR REPLACE INTO memory_vectors (id, stamp, vec) VALUES ($id, $stamp, $vec)";
                    save.Parameters.AddWithValue("$id", id);
                    save.Parameters.AddWithValue("$stamp", stamp);
                    save.Parameters.AddWithValue("$vec", Vectors.ToBytes(vec));
                    save.ExecuteNonQuery();
                }

                float score = Vectors.Dot(question, vec);
                if (score >= MeaningThreshold)
                    scored.Add((item, score));
            }

            return scored.OrderByDescending(s => s.Score).Take(max).Select(s => s.Item).ToList();
        }
    }

    private static Dictionary<string, (string Stamp, float[] Vec)> LoadVectors(SqliteConnection connection)
    {
        var result = new Dictionary<string, (string, float[])>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, stamp, vec FROM memory_vectors";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = (reader.GetString(1), Vectors.FromBytes((byte[])reader[2]));
        return result;
    }

    private IReadOnlyList<MemoryItem> SearchWords(string query, int max)
    {
        IReadOnlyList<string> stems = RussianStemmer.Stems(query);
        if (stems.Count == 0)
            return [];

        string match = string.Join(" OR ", stems.Select(stem => "\"" + stem.Replace("\"", "") + "\"*"));

        lock (_gate)
        {
            using SqliteConnection connection = Open();
            List<MemoryItem> found = ReadItems(
                connection,
                """
                SELECT m.* FROM memories_fts f
                JOIN memories m ON m.id = f.id
                WHERE memories_fts MATCH $match AND m.active = 1
                ORDER BY bm25(memories_fts), m.updated DESC
                LIMIT $max
                """,
                command =>
                {
                    command.Parameters.AddWithValue("$match", match);
                    command.Parameters.AddWithValue("$max", Math.Clamp(max, 1, 10));
                });

            if (found.Count == 0)
                return found;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            using SqliteTransaction transaction = connection.BeginTransaction();
            foreach (MemoryItem item in found)
            {
                using SqliteCommand touch = connection.CreateCommand();
                touch.Transaction = transaction;
                touch.CommandText = "UPDATE memories SET last_used = $now WHERE id = $id";
                touch.Parameters.AddWithValue("$now", Format(now));
                touch.Parameters.AddWithValue("$id", item.Id.ToString());
                touch.ExecuteNonQuery();
            }
            transaction.Commit();

            return found.Select(item => item with { LastUsedAt = now }).ToList();
        }
    }

    public IReadOnlyList<MemoryItem> ActiveItems()
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            return ReadItems(connection, "SELECT * FROM memories WHERE active = 1 ORDER BY created", _ => { });
        }
    }

    /// <summary>Active item by exact type and key, e.g. (Preference, "обращение").</summary>
    public MemoryItem? Find(MemoryType type, string key)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            return ReadItems(
                connection,
                "SELECT * FROM memories WHERE active = 1 AND type = $type AND key = $key LIMIT 1",
                command =>
                {
                    command.Parameters.AddWithValue("$type", (int)type);
                    command.Parameters.AddWithValue("$key", LocalMemoryStore.Normalize(key));
                }).FirstOrDefault();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $key";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value)";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
        }
    }

    public void AddTurn(ConversationTurn turn)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO conversation (ts, ts_ms, session, user_text, assistant, route) " +
                "VALUES ($ts, $ts_ms, $session, $user, $assistant, $route)";
            command.Parameters.AddWithValue("$ts", Format(turn.Timestamp));
            command.Parameters.AddWithValue("$ts_ms", turn.Timestamp.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$session", turn.Session);
            command.Parameters.AddWithValue("$user", turn.User.Trim());
            command.Parameters.AddWithValue("$assistant", turn.Assistant.Trim());
            command.Parameters.AddWithValue("$route", turn.Route);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ConversationTurn> Turns(DateTimeOffset from, DateTimeOffset to, int max = 200)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT ts, session, user_text, assistant, route FROM conversation " +
                "WHERE ts_ms >= $from AND ts_ms < $to ORDER BY id LIMIT $max";
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$max", Math.Max(0, max));

            var result = new List<ConversationTurn>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new ConversationTurn(
                    DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            return result;
        }
    }

    public void AddEpisode(Episode episode)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "INSERT INTO episodes (start_ms, end_ms, offset_min, summary, topics, turns) " +
                "VALUES ($start, $end, $offset, $summary, $topics, $turns)";
            command.Parameters.AddWithValue("$start", episode.Start.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$end", episode.End.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$offset", (int)episode.End.Offset.TotalMinutes);
            command.Parameters.AddWithValue("$summary", episode.Summary);
            command.Parameters.AddWithValue("$topics", episode.Topics);
            command.Parameters.AddWithValue("$turns", episode.Turns);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Episodes that ended in [from, to), oldest first.</summary>
    public IReadOnlyList<Episode> Episodes(DateTimeOffset from, DateTimeOffset to, int max = 20)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                "SELECT id, start_ms, end_ms, offset_min, summary, topics, turns FROM episodes " +
                "WHERE end_ms >= $from AND end_ms < $to ORDER BY end_ms LIMIT $max";
            command.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$max", Math.Max(0, max));

            var result = new List<Episode>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                var offset = TimeSpan.FromMinutes(reader.GetInt32(3));
                result.Add(new Episode(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToOffset(offset),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)).ToOffset(offset),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6)));
            }

            return result;
        }
    }

    private void ImportV1Once(SqliteConnection connection, string jsonPath)
    {
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "SELECT value FROM meta WHERE key = 'v1-imported'";
            if (check.ExecuteScalar() is not null)
                return;
        }

        List<MemoryItem> items = [];
        try
        {
            if (File.Exists(jsonPath))
                items = JsonSerializer.Deserialize<List<MemoryItem>>(File.ReadAllText(jsonPath)) ?? [];
        }
        catch
        {
            // A broken v1 file must not block v2; it stays on disk untouched for a manual look.
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach (MemoryItem item in items)
            Write(connection, transaction, item);

        using (SqliteCommand mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ('v1-imported', $value)";
            mark.Parameters.AddWithValue("$value", $"{DateTimeOffset.UtcNow:O}; items={items.Count}; from={jsonPath}");
            mark.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private static void Write(SqliteConnection connection, SqliteTransaction transaction, MemoryItem item)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO memories (id, type, key, content, confidence, created, updated, last_used, source, active)
                VALUES ($id, $type, $key, $content, $confidence, $created, $updated, $last_used, $source, $active)
                ON CONFLICT(id) DO UPDATE SET
                    type = excluded.type, key = excluded.key, content = excluded.content,
                    confidence = excluded.confidence, updated = excluded.updated,
                    last_used = excluded.last_used, source = excluded.source, active = excluded.active
                """;
            command.Parameters.AddWithValue("$id", item.Id.ToString());
            command.Parameters.AddWithValue("$type", (int)item.Type);
            command.Parameters.AddWithValue("$key", item.Key);
            command.Parameters.AddWithValue("$content", item.Content);
            command.Parameters.AddWithValue("$confidence", item.Confidence);
            command.Parameters.AddWithValue("$created", Format(item.CreatedAt));
            command.Parameters.AddWithValue("$updated", Format(item.UpdatedAt));
            command.Parameters.AddWithValue("$last_used", item.LastUsedAt is { } used ? Format(used) : DBNull.Value);
            command.Parameters.AddWithValue("$source", item.Source);
            command.Parameters.AddWithValue("$active", item.Active ? 1 : 0);
            command.ExecuteNonQuery();
        }

        using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM memories_fts WHERE id = $id";
            delete.Parameters.AddWithValue("$id", item.Id.ToString());
            delete.ExecuteNonQuery();
        }

        if (!item.Active)
            return;

        using SqliteCommand index = connection.CreateCommand();
        index.Transaction = transaction;
        index.CommandText = "INSERT INTO memories_fts (id, stems) VALUES ($id, $stems)";
        index.Parameters.AddWithValue("$id", item.Id.ToString());
        index.Parameters.AddWithValue("$stems", string.Join(' ', RussianStemmer.Stems(item.Key + " " + item.Content)));
        index.ExecuteNonQuery();
    }

    private static List<MemoryItem> ReadItems(SqliteConnection connection, string sql, Action<SqliteCommand> bind)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var result = new List<MemoryItem>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new MemoryItem
            {
                Id = Guid.Parse(reader.GetString(reader.GetOrdinal("id"))),
                Type = (MemoryType)reader.GetInt32(reader.GetOrdinal("type")),
                Key = reader.GetString(reader.GetOrdinal("key")),
                Content = reader.GetString(reader.GetOrdinal("content")),
                Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
                CreatedAt = Parse(reader.GetString(reader.GetOrdinal("created"))),
                UpdatedAt = Parse(reader.GetString(reader.GetOrdinal("updated"))),
                LastUsedAt = reader.IsDBNull(reader.GetOrdinal("last_used"))
                    ? null
                    : Parse(reader.GetString(reader.GetOrdinal("last_used"))),
                Source = reader.GetString(reader.GetOrdinal("source")),
                Active = reader.GetInt32(reader.GetOrdinal("active")) == 1,
            });
        }

        return result;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
