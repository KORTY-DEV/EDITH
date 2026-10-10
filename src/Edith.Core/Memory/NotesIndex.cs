using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Edith.Core.Memory;

/// <summary>One searchable piece of a note: the text under one heading.</summary>
public sealed record NoteChunk(string Title, string Heading, string Path, string Text);

/// <summary>
/// The owner's Obsidian vault as read-only knowledge: every Markdown note in the chosen folders is
/// split by headings and indexed (SQLite FTS5, the same light Russian stemming as the memory), so
/// "что у меня записано про Улей" can be answered from the notes. EDITH never writes into the vault.
/// Only changed files are re-read; the index lives in its own database file.
/// </summary>
public sealed class NotesIndex
{
    public const int MaxChunkChars = 900;

    private static readonly Regex Frontmatter = new(@"\A---\r?\n.*?\r?\n---\r?\n", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Heading = new(@"^(#{1,6})\s+(.+?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Wikilink = new(@"\[\[([^\]\|]+)(?:\|([^\]]+))?\]\]", RegexOptions.Compiled);
    private static readonly Regex DataviewBlock = new(@"```dataview.*?```", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly string _connectionString;
    private readonly object _gate = new();

    public NotesIndex(string databasePath)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using SqliteConnection connection = Open();
        Execute(connection, """
            CREATE TABLE IF NOT EXISTS files (path TEXT PRIMARY KEY, stamp TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS chunks (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                path    TEXT NOT NULL,
                title   TEXT NOT NULL,
                heading TEXT NOT NULL,
                text    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_chunks_path ON chunks(path);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(stems);
            CREATE TABLE IF NOT EXISTS chunk_vectors (chunk_id INTEGER PRIMARY KEY, vec BLOB NOT NULL);
            """);
    }

    /// <summary>Overrides the app-wide embedder for this index (tests).</summary>
    public ITextEmbedder? Embedder { get; set; }

    public int FileCount
    {
        get
        {
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM files";
                return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }
    }

    /// <summary>
    /// Brings the index in line with the vault: new and changed notes are read, deleted ones
    /// dropped. <paramref name="folders"/> are top-level folders of the vault (empty = all except
    /// hidden ones); returns how many notes were (re)indexed.
    /// </summary>
    public int Refresh(string vaultPath, IReadOnlyCollection<string> folders, IReadOnlyCollection<string>? extraFiles = null)
    {
        if (!Directory.Exists(vaultPath))
            return 0;

        // extraFiles: notes outside the vault (EDITH's own plan in her repository), read the same way.
        Dictionary<string, FileInfo> files = EnumerateNotes(vaultPath, folders)
            .Concat((extraFiles ?? []).Where(File.Exists).Select(f => new FileInfo(f)))
            .GroupBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        int changed = 0;
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteTransaction transaction = connection.BeginTransaction();

            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (SqliteCommand list = connection.CreateCommand())
            {
                list.Transaction = transaction;
                list.CommandText = "SELECT path, stamp FROM files";
                using SqliteDataReader reader = list.ExecuteReader();
                while (reader.Read())
                    known[reader.GetString(0)] = reader.GetString(1);
            }

            foreach (string gone in known.Keys.Where(path => !files.ContainsKey(path)).ToList())
            {
                Drop(connection, transaction, gone);
                changed++;
            }

            foreach ((string path, FileInfo file) in files)
            {
                string stamp = $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
                if (known.TryGetValue(path, out string? old) && old == stamp)
                    continue;

                string text;
                try
                {
                    text = File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException)
                {
                    continue; // Obsidian is saving it right now: next time
                }

                Drop(connection, transaction, path);
                foreach (NoteChunk chunk in Split(Path.GetFileNameWithoutExtension(path), path, text))
                    Add(connection, transaction, chunk);

                Execute(connection, transaction, "INSERT OR REPLACE INTO files (path, stamp) VALUES ($path, $stamp)",
                    ("$path", path), ("$stamp", stamp));
                changed++;
            }

            transaction.Commit();
        }

        return changed;
    }

    /// <summary>Cosine to the question that makes a chunk a candidate by meaning alone.</summary>
    public const float MeaningThreshold = 0.83f;

    /// <summary>
    /// Computes the meaning vectors of chunks that have none yet, a few per call (the first full
    /// pass over a vault takes some seconds of CPU, so it runs in the background). Returns how many
    /// chunks still wait.
    /// </summary>
    public int EmbedPending(ITextEmbedder embedder, int batch = 40)
    {
        List<(long Id, string Text)> todo;
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.id, c.title, c.heading, c.text FROM chunks c
                LEFT JOIN chunk_vectors v ON v.chunk_id = c.id
                WHERE v.chunk_id IS NULL LIMIT $batch
                """;
            command.Parameters.AddWithValue("$batch", batch);
            todo = [];
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                todo.Add((reader.GetInt64(0), PassageText(reader.GetString(1), reader.GetString(2), reader.GetString(3))));
        }

        foreach ((long id, string text) in todo)
        {
            float[] vector = embedder.EmbedPassage(text);
            lock (_gate)
            {
                using SqliteConnection connection = Open();
                // The chunk may have been replaced while we computed: then its vector is not ours.
                using SqliteCommand save = connection.CreateCommand();
                save.CommandText = "INSERT OR REPLACE INTO chunk_vectors (chunk_id, vec) SELECT $id, $vec WHERE EXISTS (SELECT 1 FROM chunks WHERE id = $id)";
                save.Parameters.AddWithValue("$id", id);
                save.Parameters.AddWithValue("$vec", Vectors.ToBytes(vector));
                save.ExecuteNonQuery();
            }
        }

        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand count = connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM chunks c LEFT JOIN chunk_vectors v ON v.chunk_id = c.id WHERE v.chunk_id IS NULL";
            return Convert.ToInt32(count.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
    }

    private static string PassageText(string title, string heading, string text) =>
        (title + (heading.Length > 0 ? " › " + heading : "") + ": " + text).Substring(0, Math.Min(900, title.Length + heading.Length + text.Length + 4));

    /// <summary>Word search and meaning search, merged by rank.</summary>
    public IReadOnlyList<NoteChunk> Search(string query, int max = 4)
    {
        IReadOnlyList<NoteChunk> byWords = SearchWords(query, max * 2);
        ITextEmbedder? embedder = Embedder ?? EmbeddingService.Provider();
        if (embedder is null || string.IsNullOrWhiteSpace(query))
            return byWords.Take(max).ToList();

        IReadOnlyList<NoteChunk> byMeaning;
        try
        {
            byMeaning = SearchMeaning(embedder, query, max * 2);
        }
        catch
        {
            return byWords.Take(max).ToList();
        }

        if (byMeaning.Count == 0)
            return byWords.Take(max).ToList();

        // Chunks are told apart by file and text.
        static (string, string) Key(NoteChunk c) => (c.Path, c.Text);
        Dictionary<(string, string), NoteChunk> byKey = byWords.Concat(byMeaning).GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
        return Vectors.Fuse<(string, string)>([byWords.Select(Key).ToList(), byMeaning.Select(Key).ToList()], max)
            .Select(k => byKey[k])
            .ToList();
    }

    private IReadOnlyList<NoteChunk> SearchMeaning(ITextEmbedder embedder, string query, int max)
    {
        float[] question = embedder.EmbedQuery(query);
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.title, c.heading, c.path, c.text, v.vec FROM chunk_vectors v
                JOIN chunks c ON c.id = v.chunk_id
                """;
            var scored = new List<(NoteChunk Chunk, float Score)>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                float score = Vectors.Dot(question, Vectors.FromBytes((byte[])reader[4]));
                if (score >= MeaningThreshold)
                    scored.Add((new NoteChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)), score));
            }

            return scored.OrderByDescending(s => s.Score).Take(max).Select(s => s.Chunk).ToList();
        }
    }

    private IReadOnlyList<NoteChunk> SearchWords(string query, int max)
    {
        // "Улей" is written ULEY in the notes: the Latin names join the query.
        List<string> stems = RussianStemmer.Stems(query).Concat(Brain.NotesQuestion.Synonyms(query)).Distinct().ToList();
        if (stems.Count == 0)
            return [];

        string match = string.Join(" OR ", stems.Select(stem => "\"" + stem.Replace("\"", "") + "\"*"));
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.title, c.heading, c.path, c.text FROM chunks_fts f
                JOIN chunks c ON c.id = f.rowid
                WHERE chunks_fts MATCH $match
                ORDER BY bm25(chunks_fts)
                LIMIT $max
                """;
            command.Parameters.AddWithValue("$match", match);
            command.Parameters.AddWithValue("$max", Math.Clamp(max, 1, 10));

            var result = new List<NoteChunk>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
                result.Add(new NoteChunk(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return result;
        }
    }

    /// <summary>A whole note by (part of) its title, for "прочитай заметку про …".</summary>
    public string? ReadNote(string titleQuery, int maxChars = 4000)
    {
        string wanted = LocalMemoryStore.Normalize(titleQuery);
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT DISTINCT path, title FROM chunks";
            var notes = new List<(string Path, string Title)>();
            using (SqliteDataReader reader = command.ExecuteReader())
                while (reader.Read())
                    notes.Add((reader.GetString(0), reader.GetString(1)));

            (string Path, string Title)? best = notes
                .Select(n => (Note: n, Score: Score(LocalMemoryStore.Normalize(n.Title), wanted)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => ((string, string)?)x.Note)
                .FirstOrDefault();
            if (best is null)
                return null;

            using SqliteCommand chunks = connection.CreateCommand();
            chunks.CommandText = "SELECT heading, text FROM chunks WHERE path = $path ORDER BY id";
            chunks.Parameters.AddWithValue("$path", best.Value.Path);
            var text = new StringBuilder("Заметка «").Append(best.Value.Title).AppendLine("»:");
            using SqliteDataReader body = chunks.ExecuteReader();
            while (body.Read() && text.Length < maxChars)
            {
                string heading = body.GetString(0);
                if (heading.Length > 0)
                    text.AppendLine().Append("## ").AppendLine(heading);
                text.AppendLine(body.GetString(1));
            }

            return text.Length > maxChars ? text.ToString(0, maxChars) + "…" : text.ToString().TrimEnd();
        }
    }

    // ---- parsing -------------------------------------------------------------------------------

    /// <summary>Heading by heading; long sections are cut at paragraph ends.</summary>
    public static IEnumerable<NoteChunk> Split(string title, string path, string markdown)
    {
        string body = Frontmatter.Replace(markdown, "", 1);
        body = DataviewBlock.Replace(body, "");
        body = Wikilink.Replace(body, m => m.Groups[2].Success ? m.Groups[2].Value : m.Groups[1].Value);

        string heading = "";
        var section = new StringBuilder();
        var chunks = new List<NoteChunk>();

        void Flush()
        {
            string text = section.ToString().Trim();
            section.Clear();
            if (text.Length < 8)
                return;

            foreach (string piece in Pieces(text))
                chunks.Add(new NoteChunk(title, heading, path, piece));
        }

        foreach (string line in body.Split('\n'))
        {
            Match match = Heading.Match(line.TrimEnd('\r'));
            if (match.Success)
            {
                Flush();
                heading = match.Groups[2].Value.Trim();
                continue;
            }

            section.AppendLine(line.TrimEnd('\r'));
        }

        Flush();
        return chunks;
    }

    private static IEnumerable<string> Pieces(string text)
    {
        if (text.Length <= MaxChunkChars)
        {
            yield return text;
            yield break;
        }

        var current = new StringBuilder();
        foreach (string paragraph in Regex.Split(text, @"\n\s*\n"))
        {
            if (current.Length > 0 && current.Length + paragraph.Length > MaxChunkChars)
            {
                yield return current.ToString().Trim();
                current.Clear();
            }

            current.AppendLine(paragraph).AppendLine();
        }

        if (current.Length > 0)
            yield return current.ToString().Trim();
    }

    private static int Score(string title, string wanted)
    {
        if (title == wanted)
            return 100;
        if (title.Contains(wanted, StringComparison.Ordinal))
            return 60 + wanted.Length;
        string[] words = RussianStemmer.Stems(wanted).ToArray();
        return words.Length == 0 ? 0 : words.Count(w => title.Contains(w, StringComparison.Ordinal)) * 10;
    }

    private static IEnumerable<FileInfo> EnumerateNotes(string vaultPath, IReadOnlyCollection<string> folders)
    {
        var root = new DirectoryInfo(vaultPath);
        IEnumerable<DirectoryInfo> starts = folders.Count == 0
            ? root.EnumerateDirectories().Where(d => !d.Name.StartsWith('.'))
            : folders.Select(f => new DirectoryInfo(Path.Combine(vaultPath, f))).Where(d => d.Exists);

        foreach (DirectoryInfo start in starts)
            foreach (FileInfo file in start.EnumerateFiles("*.md", SearchOption.AllDirectories))
                if (!file.FullName.Contains(@"\.", StringComparison.Ordinal))
                    yield return file;

        // Notes lying directly in the vault root.
        if (folders.Count == 0)
            foreach (FileInfo file in root.EnumerateFiles("*.md"))
                yield return file;
    }

    // ---- sqlite plumbing ----------------------------------------------------------------------

    private void Add(SqliteConnection connection, SqliteTransaction transaction, NoteChunk chunk)
    {
        using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO chunks (path, title, heading, text) VALUES ($path, $title, $heading, $text); SELECT last_insert_rowid();";
        insert.Parameters.AddWithValue("$path", chunk.Path);
        insert.Parameters.AddWithValue("$title", chunk.Title);
        insert.Parameters.AddWithValue("$heading", chunk.Heading);
        insert.Parameters.AddWithValue("$text", chunk.Text);
        long id = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);

        // Title and heading count for search too: "Архитектура EDITH" must find the architecture note.
        string stems = string.Join(' ', RussianStemmer.Stems(chunk.Title + " " + chunk.Heading + " " + chunk.Text));
        Execute(connection, transaction, "INSERT INTO chunks_fts (rowid, stems) VALUES ($id, $stems)", ("$id", id), ("$stems", stems));
    }

    private static void Drop(SqliteConnection connection, SqliteTransaction transaction, string path)
    {
        Execute(connection, transaction, "DELETE FROM chunk_vectors WHERE chunk_id IN (SELECT id FROM chunks WHERE path = $path)", ("$path", path));
        Execute(connection, transaction, "DELETE FROM chunks_fts WHERE rowid IN (SELECT id FROM chunks WHERE path = $path)", ("$path", path));
        Execute(connection, transaction, "DELETE FROM chunks WHERE path = $path", ("$path", path));
        Execute(connection, transaction, "DELETE FROM files WHERE path = $path", ("$path", path));
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

    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
            command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
