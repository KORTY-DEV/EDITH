using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace Edith.Core.Runtime;

/// <summary>
/// Append-only SQLite journal of <see cref="EdithEvent"/>s. Writes go through a background
/// queue so a publisher on the audio thread never waits for the disk. There is deliberately
/// no update or delete API: the journal is the record of what EDITH did.
/// </summary>
public sealed class ActionJournal : IAsyncDisposable, IDisposable
{
    public const int MaxFieldLength = 4000;

    private readonly string _connectionString;
    private readonly Channel<EdithEvent> _queue =
        Channel.CreateUnbounded<EdithEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _writer;
    private IDisposable? _subscription;
    private long _written;
    private long _failed;

    public ActionJournal(string databasePath)
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

        using (SqliteConnection connection = Open())
        {
            Execute(connection, "PRAGMA journal_mode=WAL;");
            Execute(connection, """
                CREATE TABLE IF NOT EXISTS events (
                    id       INTEGER PRIMARY KEY AUTOINCREMENT,
                    ts       TEXT    NOT NULL,
                    ts_ms    INTEGER NOT NULL,
                    kind     TEXT    NOT NULL,
                    session  INTEGER NOT NULL,
                    summary  TEXT    NOT NULL,
                    data     TEXT    NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_events_ts ON events(ts_ms);
                CREATE INDEX IF NOT EXISTS ix_events_kind ON events(kind, ts_ms);
                """);
        }

        _writer = Task.Run(WriteLoopAsync);
    }

    /// <summary>Drops events older than <paramref name="keep"/> so the journal cannot grow forever.</summary>
    public int Prune(TimeSpan keep)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM events WHERE ts_ms < $cutoff";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.Now.Add(-keep).ToUnixTimeMilliseconds());
        return command.ExecuteNonQuery();
    }

    public long WrittenCount => Interlocked.Read(ref _written);

    public long FailedCount => Interlocked.Read(ref _failed);

    /// <summary>Starts journaling every event published on <paramref name="bus"/>.</summary>
    public void Attach(IEventBus bus)
    {
        _subscription?.Dispose();
        _subscription = bus.Subscribe(Append);
    }

    public void Append(EdithEvent evt) => _queue.Writer.TryWrite(evt);

    /// <summary>Waits until everything appended so far is on disk.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(EdithEvent.Create(FlushKind, "", data: new FlushMarker(marker))))
            return;

        await marker.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<EdithEvent> Recent(int max = 50, string? kindPrefix = null)
    {
        string sql = kindPrefix is null
            ? "SELECT ts, kind, session, summary, data FROM events ORDER BY id DESC LIMIT $max"
            : "SELECT ts, kind, session, summary, data FROM events WHERE kind LIKE $prefix ESCAPE '\\' ORDER BY id DESC LIMIT $max";

        List<EdithEvent> result = Query(sql, command =>
        {
            command.Parameters.AddWithValue("$max", Math.Max(0, max));
            if (kindPrefix is not null)
                command.Parameters.AddWithValue("$prefix", EscapeLike(kindPrefix) + "%");
        });

        result.Reverse();
        return result;
    }

    public IReadOnlyList<EdithEvent> Since(DateTimeOffset since, int max = 500) =>
        Query(
            "SELECT ts, kind, session, summary, data FROM events WHERE ts_ms >= $since ORDER BY id LIMIT $max",
            command =>
            {
                command.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("$max", Math.Max(0, max));
            });

    public IReadOnlyList<EdithEvent> ForSession(long session) =>
        Query(
            "SELECT ts, kind, session, summary, data FROM events WHERE session = $session ORDER BY id",
            command => command.Parameters.AddWithValue("$session", session));

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        _subscription?.Dispose();
        _subscription = null;
        _queue.Writer.TryComplete();

        try
        {
            await _writer.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private const string FlushKind = "__journal.flush";

    private sealed class FlushMarker(TaskCompletionSource completion) : Dictionary<string, string>
    {
        public TaskCompletionSource Completion { get; } = completion;
    }

    private async Task WriteLoopAsync()
    {
        using SqliteConnection connection = Open();

        while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new List<EdithEvent>();
            var markers = new List<TaskCompletionSource>();

            while (_queue.Reader.TryRead(out EdithEvent? evt))
            {
                if (evt.Data is FlushMarker marker)
                    markers.Add(marker.Completion);
                else
                    batch.Add(evt);
            }

            if (batch.Count > 0)
                WriteBatch(connection, batch);

            foreach (TaskCompletionSource marker in markers)
                marker.TrySetResult();
        }
    }

    private void WriteBatch(SqliteConnection connection, List<EdithEvent> batch)
    {
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO events (ts, ts_ms, kind, session, summary, data) " +
                "VALUES ($ts, $ts_ms, $kind, $session, $summary, $data)";

            SqliteParameter ts = command.Parameters.Add("$ts", SqliteType.Text);
            SqliteParameter tsMs = command.Parameters.Add("$ts_ms", SqliteType.Integer);
            SqliteParameter kind = command.Parameters.Add("$kind", SqliteType.Text);
            SqliteParameter session = command.Parameters.Add("$session", SqliteType.Integer);
            SqliteParameter summary = command.Parameters.Add("$summary", SqliteType.Text);
            SqliteParameter data = command.Parameters.Add("$data", SqliteType.Text);

            foreach (EdithEvent evt in batch)
            {
                ts.Value = evt.Timestamp.ToString("O", CultureInfo.InvariantCulture);
                tsMs.Value = evt.Timestamp.ToUnixTimeMilliseconds();
                kind.Value = evt.Kind;
                session.Value = evt.Session;
                summary.Value = Truncate(evt.Summary);
                data.Value = JsonSerializer.Serialize(
                    evt.Data.ToDictionary(pair => pair.Key, pair => Truncate(pair.Value)));
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            Interlocked.Add(ref _written, batch.Count);
        }
        catch
        {
            Interlocked.Add(ref _failed, batch.Count);
        }
    }

    private List<EdithEvent> Query(string sql, Action<SqliteCommand> bind)
    {
        using SqliteConnection connection = Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var result = new List<EdithEvent>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            IReadOnlyDictionary<string, string> data =
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(4))
                ?? new Dictionary<string, string>();

            result.Add(new EdithEvent(
                DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                data));
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

    private static string Truncate(string? value) =>
        value is null ? "" :
        value.Length <= MaxFieldLength ? value :
        value[..MaxFieldLength] + "…";

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
