using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;

namespace PcOrbit.Store;

/// <summary>
/// Where local state lives. Local-first is an architectural requirement, not a preference
/// (spec 6.8, 15): nothing here needs a network or an account to work.
/// </summary>
public static class StorePaths
{
    public const string ApplicationFolderName = "PC Orbit";

    public static string DefaultDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ApplicationFolderName);

    public static string DefaultDatabasePath => Path.Combine(DefaultDirectory, "pcorbit.db");
}

/// <summary>
/// The SQLite file behind snapshots, transaction checkpoints and the event log.
/// </summary>
/// <remarks>
/// <para>
/// <c>synchronous = FULL</c> is deliberate and not the usual default. This database is written
/// immediately before the machine is asked to restart, and sometimes while firmware is being
/// changed. A checkpoint that is still in an OS buffer when the power goes is a checkpoint that
/// does not exist, and spec 28 asks for fault injection including power loss — so we pay the
/// fsync. These are a handful of small writes per transaction, not a hot path.
/// </para>
/// <para>
/// Records are stored as indexed columns plus the full JSON. The columns make history queryable;
/// the JSON means a schema addition does not lose old records, and a support engineer can read a
/// row without the app.
/// </para>
/// </remarks>
public sealed class PcOrbitDatabase
{
    private const int CurrentSchemaVersion = 1;

    private readonly string _connectionString;

    public PcOrbitDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        DatabasePath = databasePath;

        string? directory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    public string DatabasePath { get; }

    public static PcOrbitDatabase OpenDefault() => Open(StorePaths.DefaultDatabasePath);

    public static PcOrbitDatabase Open(string databasePath)
    {
        var database = new PcOrbitDatabase(databasePath);
        database.Migrate();
        return database;
    }

    /// <summary>An in-memory database for tests. Kept alive by the caller holding the instance.</summary>
    public static PcOrbitDatabase OpenTemporary()
    {
        string path = Path.Combine(Path.GetTempPath(), $"pcorbit-test-{Guid.NewGuid():N}.db");
        return Open(path);
    }

    internal SqliteConnection Connect()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = FULL; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void Migrate()
    {
        using SqliteConnection connection = Connect();
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");

        int version;

        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_version;";
            version = Convert.ToInt32(read.ExecuteScalar(), provider: null);
        }

        if (version >= CurrentSchemaVersion)
        {
            transaction.Commit();
            return;
        }

        if (version < 1)
        {
            Execute(connection, transaction, Schema.V1);
        }

        using (SqliteCommand stamp = connection.CreateCommand())
        {
            stamp.Transaction = transaction;
            stamp.CommandText = "INSERT INTO schema_version (version) VALUES ($version);";
            stamp.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            stamp.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Only ever called with the compile-time constants in Schema. No caller " +
                        "accepts a runtime string, and none may be added: every data value in this " +
                        "assembly goes through SqliteParameter.")]
    private static void Execute(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static class Schema
    {
        internal const string V1 = """
            CREATE TABLE snapshots (
                id                  TEXT PRIMARY KEY,
                taken_at            TEXT NOT NULL,
                machine_fingerprint TEXT NOT NULL,
                json                TEXT NOT NULL
            );

            CREATE INDEX ix_snapshots_taken_at ON snapshots (taken_at DESC);

            CREATE TABLE transactions (
                id          TEXT PRIMARY KEY,
                outcome_id  TEXT NOT NULL,
                plan_hash   TEXT NOT NULL,
                state       TEXT NOT NULL,
                is_finished INTEGER NOT NULL,
                created_at  TEXT NOT NULL,
                updated_at  TEXT NOT NULL,
                json        TEXT NOT NULL
            );

            CREATE INDEX ix_transactions_unfinished ON transactions (is_finished, updated_at DESC);

            -- Append-only. Nothing in the product updates or deletes a row here (spec 14).
            CREATE TABLE events (
                id                  TEXT PRIMARY KEY,
                timestamp           TEXT NOT NULL,
                source              TEXT NOT NULL,
                category            TEXT NOT NULL,
                component           TEXT NOT NULL,
                before_value        TEXT NULL,
                after_value         TEXT NULL,
                initiator           TEXT NOT NULL,
                confidence          TEXT NOT NULL,
                related_transaction TEXT NULL,
                related_restart     TEXT NULL,
                message_key         TEXT NULL,
                json                TEXT NOT NULL
            );

            CREATE INDEX ix_events_timestamp ON events (timestamp DESC);
            CREATE INDEX ix_events_component ON events (component, timestamp DESC);
            CREATE INDEX ix_events_transaction ON events (related_transaction);
            CREATE INDEX ix_events_restart ON events (related_restart, timestamp DESC);
            """;
    }
}
