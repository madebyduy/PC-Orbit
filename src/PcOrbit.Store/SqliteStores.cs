using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using PcOrbit.Core.Abstractions;
using PcOrbit.Core.Events;
using PcOrbit.Core.Model;
using PcOrbit.Core.Serialization;
using PcOrbit.Core.Transactions;

namespace PcOrbit.Store;

internal static class SqliteHelpers
{
    /// <summary>Round-trip format. Never a locale-specific one: this data outlives the session.</summary>
    internal static string ToText(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>The inverse of <see cref="ToText"/>, for the columns we read back as values.</summary>
    internal static DateTimeOffset ToTimestamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonDefaults.Readable);

    internal static T Deserialize<T>(string json, string what) =>
        JsonSerializer.Deserialize<T>(json, JsonDefaults.Readable)
        ?? throw new InvalidOperationException($"Stored {what} row deserialised to null.");
}

public sealed class SqliteSnapshotStore(PcOrbitDatabase database) : ISnapshotStore
{
    private readonly PcOrbitDatabase _database = database
        ?? throw new ArgumentNullException(nameof(database));

    public async Task SaveAsync(StateSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO snapshots (id, taken_at, machine_fingerprint, json)
            VALUES ($id, $takenAt, $fingerprint, $json)
            ON CONFLICT (id) DO UPDATE SET
                taken_at = excluded.taken_at,
                machine_fingerprint = excluded.machine_fingerprint,
                json = excluded.json;
            """;

        command.Parameters.AddWithValue("$id", snapshot.Id);
        command.Parameters.AddWithValue("$takenAt", SqliteHelpers.ToText(snapshot.TakenAt));
        command.Parameters.AddWithValue("$fingerprint", snapshot.Machine.Fingerprint);
        command.Parameters.AddWithValue("$json", SqliteHelpers.Serialize(SnapshotRecord.From(snapshot)));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<StateSnapshot?> LoadAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotId);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT json FROM snapshots WHERE id = $id;";
        command.Parameters.AddWithValue("$id", snapshotId);

        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return json is string text
            ? SqliteHelpers.Deserialize<SnapshotRecord>(text, "snapshot").ToSnapshot()
            : null;
    }

    public async Task<StateSnapshot?> LoadLatestAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT json FROM snapshots ORDER BY taken_at DESC LIMIT 1;";

        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return json is string text
            ? SqliteHelpers.Deserialize<SnapshotRecord>(text, "snapshot").ToSnapshot()
            : null;
    }

    public async Task<IReadOnlyList<SnapshotSummary>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        // Ordered by id as well as time: two scans in the same second would otherwise come back in
        // an order SQLite is free to change, and a diff that picks a different "previous scan" on
        // each run is not a diff anyone can trust.
        command.CommandText = """
            SELECT id, taken_at, machine_fingerprint
            FROM snapshots
            ORDER BY taken_at DESC, id DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", limit);

        List<SnapshotSummary> summaries = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            summaries.Add(new SnapshotSummary(
                reader.GetString(0),
                SqliteHelpers.ToTimestamp(reader.GetString(1)),
                reader.GetString(2)));
        }

        return summaries;
    }

    /// <summary>
    /// Persisted shape of a snapshot. Explicit rather than serialising <see cref="StateSnapshot"/>
    /// directly, because that type is a lookup structure and its stored form should be a flat,
    /// readable list a human can diff.
    /// </summary>
    private sealed record SnapshotRecord(
        string Id,
        DateTimeOffset TakenAt,
        MachineIdentity Machine,
        List<CapabilityReading> Readings)
    {
        internal static SnapshotRecord From(StateSnapshot snapshot) =>
            new(snapshot.Id, snapshot.TakenAt, snapshot.Machine, [.. snapshot.Readings]);

        internal StateSnapshot ToSnapshot() => new(Id, TakenAt, Machine, Readings);
    }
}

public sealed class SqliteTransactionStore(PcOrbitDatabase database) : ITransactionStore
{
    private readonly PcOrbitDatabase _database = database
        ?? throw new ArgumentNullException(nameof(database));

    public async Task SaveAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        // Upsert: a transaction is checkpointed many times as it moves through its phases, and the
        // latest checkpoint is the only one that matters for resume. The event log keeps the story.
        command.CommandText = """
            INSERT INTO transactions (id, outcome_id, plan_hash, state, is_finished, created_at, updated_at, json)
            VALUES ($id, $outcome, $hash, $state, $finished, $created, $updated, $json)
            ON CONFLICT (id) DO UPDATE SET
                state = excluded.state,
                is_finished = excluded.is_finished,
                updated_at = excluded.updated_at,
                json = excluded.json;
            """;

        command.Parameters.AddWithValue("$id", transaction.Id);
        command.Parameters.AddWithValue("$outcome", transaction.Plan.OutcomeId);
        command.Parameters.AddWithValue("$hash", transaction.PlanHash);
        command.Parameters.AddWithValue("$state", transaction.State.ToString());
        command.Parameters.AddWithValue("$finished", transaction.IsFinished ? 1 : 0);
        command.Parameters.AddWithValue("$created", SqliteHelpers.ToText(transaction.CreatedAt));
        command.Parameters.AddWithValue("$updated", SqliteHelpers.ToText(transaction.UpdatedAt));
        command.Parameters.AddWithValue("$json", SqliteHelpers.Serialize(transaction));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Transaction?> LoadAsync(string transactionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transactionId);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT json FROM transactions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", transactionId);

        object? json = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return json is string text ? SqliteHelpers.Deserialize<Transaction>(text, "transaction") : null;
    }

    public Task<IReadOnlyList<Transaction>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        QueryAsync(
            "SELECT json FROM transactions WHERE is_finished = 0 ORDER BY updated_at DESC LIMIT 50;",
            parameters: null,
            cancellationToken);

    public Task<IReadOnlyList<Transaction>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        QueryAsync(
            "SELECT json FROM transactions ORDER BY updated_at DESC LIMIT $limit;",
            command => command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500)),
            cancellationToken);

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Both call sites pass a string literal; the only runtime values are bound " +
                        "as parameters by the callback.")]
    private async Task<IReadOnlyList<Transaction>> QueryAsync(
        string sql,
        Action<SqliteCommand>? parameters,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;
        parameters?.Invoke(command);

        List<Transaction> results = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteHelpers.Deserialize<Transaction>(reader.GetString(0), "transaction"));
        }

        return results;
    }
}

public sealed class SqliteEventLog(PcOrbitDatabase database) : IEventLog
{
    private readonly PcOrbitDatabase _database = database
        ?? throw new ArgumentNullException(nameof(database));

    public async Task AppendAsync(ChangeEvent changeEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeEvent);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        // No upsert and no update path: history is append-only by construction, not by convention.
        command.CommandText = """
            INSERT INTO events (
                id, timestamp, source, category, component, before_value, after_value,
                initiator, confidence, related_transaction, related_restart, message_key, json)
            VALUES (
                $id, $timestamp, $source, $category, $component, $before, $after,
                $initiator, $confidence, $transaction, $restart, $messageKey, $json);
            """;

        command.Parameters.AddWithValue("$id", changeEvent.Id);
        command.Parameters.AddWithValue("$timestamp", SqliteHelpers.ToText(changeEvent.Timestamp));
        command.Parameters.AddWithValue("$source", changeEvent.Source.ToString());
        command.Parameters.AddWithValue("$category", changeEvent.Category.ToString());
        command.Parameters.AddWithValue("$component", changeEvent.Component);
        command.Parameters.AddWithValue("$before", changeEvent.Before ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$after", changeEvent.After ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$initiator", changeEvent.Initiator.ToString());
        command.Parameters.AddWithValue("$confidence", changeEvent.Confidence.ToString());
        command.Parameters.AddWithValue("$transaction", changeEvent.RelatedTransaction ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$restart", changeEvent.RelatedRestart ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$messageKey", changeEvent.MessageKey ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$json", SqliteHelpers.Serialize(changeEvent));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The WHERE clause is assembled from a closed set of literals in this method. " +
                        "Every value from the caller is bound as a SqliteParameter, including the limit.")]
    public async Task<IReadOnlyList<ChangeEvent>> QueryAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using SqliteConnection connection = _database.Connect();
        await using SqliteCommand command = connection.CreateCommand();

        List<string> filters = [];

        if (query.Since is { } since)
        {
            filters.Add("timestamp >= $since");
            command.Parameters.AddWithValue("$since", SqliteHelpers.ToText(since));
        }

        if (query.Until is { } until)
        {
            filters.Add("timestamp <= $until");
            command.Parameters.AddWithValue("$until", SqliteHelpers.ToText(until));
        }

        if (!string.IsNullOrWhiteSpace(query.Component))
        {
            filters.Add("component = $component");
            command.Parameters.AddWithValue("$component", query.Component);
        }

        if (!string.IsNullOrWhiteSpace(query.TransactionId))
        {
            filters.Add("related_transaction = $transaction");
            command.Parameters.AddWithValue("$transaction", query.TransactionId);
        }

        if (query.Category is { } category)
        {
            filters.Add("category = $category");
            command.Parameters.AddWithValue("$category", category.ToString());
        }

        string where = filters.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filters);
        command.Parameters.AddWithValue("$limit", Math.Clamp(query.Limit, 1, 5000));

        // Filters are built from a fixed set of literals; every value is a parameter (CA2100).
        command.CommandText = $"SELECT json FROM events {where} ORDER BY timestamp DESC LIMIT $limit;";

        List<ChangeEvent> results = [];

        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(SqliteHelpers.Deserialize<ChangeEvent>(reader.GetString(0), "event"));
        }

        return results;
    }
}
