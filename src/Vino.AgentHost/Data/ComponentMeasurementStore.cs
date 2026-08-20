using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Vino.AgentHost.Data;

/// <summary>
/// One component's last MEASURED solve facts: wall-clock of its last python.execute, the
/// table-known input volume that solve consumed, and the per-output-socket item counts from its
/// last committed output inspection. Feeds the predicted-solve-time gate (predicted = last
/// duration × current/last input-volume ratio) — measurement-driven, never a guess; the forced
/// cheap first solve (the 10k first-pass ceiling) doubles as the calibration probe.
/// </summary>
public sealed record ComponentMeasurementRecord(
    Guid ComponentId,
    long? SolveMilliseconds,
    long? InputItems,
    IReadOnlyDictionary<Guid, long> OutputCounts,
    long Revision,
    DateTimeOffset ObservedAt);

/// <summary>
/// Durable mirror of the per-runtime component measurement table, keyed by the same durable
/// docKey as every other per-document store. Restored knowledge, not authority: a stale or
/// missing row only ever makes the predicted-time gate skip (the slider gate, first-solve
/// ceiling, and the injected watchdog still stand), so losing this store entirely is a cold
/// start with no user data at risk.
/// </summary>
public sealed class ComponentMeasurementStore
{
    // Bump on any incompatible table change; a mismatch drops the table and starts cold (safe —
    // see class remarks).
    private const string SchemaVersion = "1";

    // Per-document row cap: rows for deleted components are never removed individually, so
    // growth is bounded here — compaction keeps the most recently written rows.
    public const int MaximumRowsPerDocument = 512;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ComponentMeasurementStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA journal_mode=WAL;", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS component_measurements_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """, cancellationToken).ConfigureAwait(false);
            var storedVersion = await ReadMetaAsync(connection, "schema_version", cancellationToken)
                .ConfigureAwait(false);
            if (storedVersion is not null &&
                !string.Equals(storedVersion, SchemaVersion, StringComparison.Ordinal))
            {
                await ExecuteAsync(
                    connection, "DROP TABLE IF EXISTS component_measurements;", cancellationToken)
                    .ConfigureAwait(false);
            }
            await ExecuteAsync(connection, """
                CREATE TABLE IF NOT EXISTS component_measurements (
                    doc_key TEXT NOT NULL,
                    component_id TEXT NOT NULL,
                    solve_ms INTEGER NULL,
                    input_items INTEGER NULL,
                    output_counts TEXT NOT NULL,
                    revision INTEGER NOT NULL,
                    seq INTEGER NOT NULL,
                    updated_at TEXT NOT NULL,
                    PRIMARY KEY(doc_key, component_id)
                );
                CREATE INDEX IF NOT EXISTS ix_component_measurements_doc_seq
                    ON component_measurements(doc_key, seq);
                """, cancellationToken).ConfigureAwait(false);
            await using var stamp = connection.CreateCommand();
            stamp.CommandText =
                "INSERT OR REPLACE INTO component_measurements_meta(key,value) VALUES('schema_version',$version);";
            stamp.Parameters.AddWithValue("$version", SchemaVersion);
            await stamp.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Upserts the rows one commit measured, then compacts the document down to
    /// <see cref="MaximumRowsPerDocument"/> keeping the most recently written rows (store-side
    /// monotonic seq, same eviction rationale as <see cref="ResourceLedgerStore"/> — revisions
    /// reset on reopen, seq only ever grows).
    /// </summary>
    public async Task UpsertAsync(
        string docKey,
        IReadOnlyList<ComponentMeasurementRecord> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docKey);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }
        var canonicalDocKey = docKey.Trim().ToLowerInvariant();
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = connection.BeginTransaction();
            long nextSequence;
            await using (var readSequence = connection.CreateCommand())
            {
                readSequence.Transaction = transaction;
                readSequence.CommandText = "SELECT COALESCE(MAX(seq),0) FROM component_measurements;";
                nextSequence = (long)(await readSequence.ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false))!;
            }
            foreach (var entry in entries)
            {
                await using var upsert = connection.CreateCommand();
                upsert.Transaction = transaction;
                upsert.CommandText = """
                    INSERT INTO component_measurements(
                        doc_key,component_id,solve_ms,input_items,output_counts,revision,seq,updated_at)
                    VALUES($doc,$component,$solve,$items,$outputs,$revision,$seq,$updated)
                    ON CONFLICT(doc_key,component_id) DO UPDATE SET
                        solve_ms=excluded.solve_ms,
                        input_items=excluded.input_items,
                        output_counts=excluded.output_counts,
                        revision=excluded.revision,
                        seq=excluded.seq,
                        updated_at=excluded.updated_at;
                    """;
                upsert.Parameters.AddWithValue("$doc", canonicalDocKey);
                upsert.Parameters.AddWithValue("$component", entry.ComponentId.ToString("D"));
                upsert.Parameters.AddWithValue("$solve", (object?)entry.SolveMilliseconds ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$items", (object?)entry.InputItems ?? DBNull.Value);
                upsert.Parameters.AddWithValue("$outputs", JsonSerializer.Serialize(
                    entry.OutputCounts.ToDictionary(pair => pair.Key.ToString("D"), pair => pair.Value)));
                upsert.Parameters.AddWithValue("$revision", entry.Revision);
                upsert.Parameters.AddWithValue("$seq", ++nextSequence);
                upsert.Parameters.AddWithValue("$updated", now);
                await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await using (var compact = connection.CreateCommand())
            {
                compact.Transaction = transaction;
                compact.CommandText = """
                    DELETE FROM component_measurements
                    WHERE doc_key=$doc AND rowid NOT IN (
                        SELECT rowid FROM component_measurements
                        WHERE doc_key=$doc
                        ORDER BY seq DESC
                        LIMIT $cap);
                    """;
                compact.Parameters.AddWithValue("$doc", canonicalDocKey);
                compact.Parameters.AddWithValue("$cap", MaximumRowsPerDocument);
                await compact.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Reads one document's rows (hydration input). Unreadable rows are skipped, never thrown.</summary>
    public async Task<IReadOnlyList<ComponentMeasurementRecord>> ReadDocumentAsync(
        string docKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(docKey);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT component_id,solve_ms,input_items,output_counts,revision,updated_at
                FROM component_measurements
                WHERE doc_key=$doc COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$doc", docKey.Trim());
            var records = new List<ComponentMeasurementRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!Guid.TryParse(reader.GetString(0), out var componentId))
                {
                    continue;
                }
                IReadOnlyDictionary<Guid, long> outputCounts;
                try
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(3))
                        ?? new Dictionary<string, long>();
                    var counts = new Dictionary<Guid, long>(parsed.Count);
                    foreach (var pair in parsed)
                    {
                        if (Guid.TryParse(pair.Key, out var parameterId))
                        {
                            counts[parameterId] = pair.Value;
                        }
                    }
                    outputCounts = counts;
                }
                catch (JsonException)
                {
                    continue;
                }
                records.Add(new ComponentMeasurementRecord(
                    componentId,
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    outputCounts,
                    reader.GetInt64(4),
                    DateTimeOffset.TryParse(
                        reader.GetString(5),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var observedAt)
                        ? observedAt
                        : DateTimeOffset.MinValue));
            }
            return records;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Follows a Save As docKey rename, same contract as the other per-document stores.</summary>
    public async Task<int> RemapDocKeyAsync(
        string oldDocKey,
        string newDocKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldDocKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(newDocKey);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE OR REPLACE component_measurements
                SET doc_key=$new
                WHERE doc_key=$old COLLATE NOCASE;
                """;
            command.Parameters.AddWithValue("$new", newDocKey.Trim().ToLowerInvariant());
            command.Parameters.AddWithValue("$old", oldDocKey.Trim());
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string?> ReadMetaAsync(
        SqliteConnection connection,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM component_measurements_meta WHERE key=$key;";
        command.Parameters.AddWithValue("$key", key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value as string;
    }
}
