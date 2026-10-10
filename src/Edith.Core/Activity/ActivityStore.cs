using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Edith.Core.Activity;

/// <summary>One note for the owner: a thing EDITH noticed on her own ("Улей: три дня без коммитов").</summary>
public sealed record PulseCard(long Id, DateTimeOffset Created, string Kind, string Key, string Title, string Body, int State);

public sealed record UsageRow(DateOnly Day, int Hour, string App, int Seconds);

/// <summary>
/// What the owner's PC was used for, kept only on this machine: seconds per day and hour per program
/// (names, never window titles or contents) and the cards the background "pulse" prepared.
/// One small SQLite file.
/// </summary>
public sealed class ActivityStore
{
    public const int CardNew = 0, CardSeen = 1, CardDismissed = 2;

    private readonly string _connectionString;
    private readonly object _gate = new();

    public ActivityStore(string databasePath)
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
        Run(connection, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS app_usage (
                day     TEXT NOT NULL,
                hour    INTEGER NOT NULL,
                app     TEXT NOT NULL,
                seconds INTEGER NOT NULL,
                PRIMARY KEY (day, hour, app)
            );
            CREATE TABLE IF NOT EXISTS cards (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                ts_ms   INTEGER NOT NULL,
                kind    TEXT NOT NULL,
                key     TEXT NOT NULL UNIQUE,
                title   TEXT NOT NULL,
                body    TEXT NOT NULL,
                state   INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """);
    }

    /// <summary>Adds time spent in <paramref name="app"/> (a program name) at <paramref name="at"/>.</summary>
    public void AddUsage(DateTimeOffset at, string app, int seconds)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_usage(day, hour, app, seconds) VALUES($d, $h, $a, $s)
                ON CONFLICT(day, hour, app) DO UPDATE SET seconds = seconds + $s;
                """;
            command.Parameters.AddWithValue("$d", at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$h", at.Hour);
            command.Parameters.AddWithValue("$a", app);
            command.Parameters.AddWithValue("$s", seconds);
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<UsageRow> Usage(DateTimeOffset from, DateTimeOffset to)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT day, hour, app, seconds FROM app_usage WHERE day >= $a AND day <= $b ORDER BY day, hour";
            command.Parameters.AddWithValue("$a", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$b", to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var rows = new List<UsageRow>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new UsageRow(
                    DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                    reader.GetInt32(1), reader.GetString(2), reader.GetInt32(3)));
            }

            return rows;
        }
    }

    /// <summary>False when a card with this key already exists (each thing is told once).</summary>
    public bool AddCard(PulseCard card)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT OR IGNORE INTO cards(ts_ms, kind, key, title, body, state) VALUES($t, $k, $key, $ti, $b, 0)";
            command.Parameters.AddWithValue("$t", card.Created.ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("$k", card.Kind);
            command.Parameters.AddWithValue("$key", card.Key);
            command.Parameters.AddWithValue("$ti", card.Title);
            command.Parameters.AddWithValue("$b", card.Body);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public IReadOnlyList<PulseCard> Cards(bool includeDismissed = false, int max = 30)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT id, ts_ms, kind, key, title, body, state FROM cards " +
                (includeDismissed ? "" : "WHERE state < 2 ") + "ORDER BY ts_ms DESC LIMIT $n";
            command.Parameters.AddWithValue("$n", max);
            var cards = new List<PulseCard>();
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cards.Add(new PulseCard(
                    reader.GetInt64(0),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)).ToLocalTime(),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetInt32(6)));
            }

            return cards;
        }
    }

    public void SetCardState(long id, int state)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE cards SET state = $s WHERE id = $i";
            command.Parameters.AddWithValue("$s", state);
            command.Parameters.AddWithValue("$i", id);
            command.ExecuteNonQuery();
        }
    }

    public string? GetMeta(string key)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM meta WHERE key = $k";
            command.Parameters.AddWithValue("$k", key);
            return command.ExecuteScalar() as string;
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_gate)
        {
            using SqliteConnection connection = Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "INSERT INTO meta(key, value) VALUES($k, $v) ON CONFLICT(key) DO UPDATE SET value = $v";
            command.Parameters.AddWithValue("$k", key);
            command.Parameters.AddWithValue("$v", value);
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private static void Run(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
