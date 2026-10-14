using Microsoft.Data.Sqlite;

namespace BroadcastRouter.Infrastructure;

public sealed partial class SqliteDataStore
{
    private readonly DiagnosticPersistenceQueue _diagnostics;
    private long _diagnosticRetentionEvictions, _diagnosticMaintenanceFailures;
    private readonly string _diagnosticSessionId = Guid.NewGuid().ToString("N");
    private int _diagnosticSessionStarted, _diagnosticHistoricalIncomplete, _diagnosticSessionStopped;
    private const int DiagnosticRowLimit = 100_000;
    private const int DiagnosticPruneBatch = 1000;
    // Covers bounded delete/index/WAL churn as well as the <=32 incoming rows.
    private const long DiagnosticTransactionHeadroom = 128L * 1024 * 1024;
    public DiagnosticPersistenceStatus DiagnosticStatus => _diagnostics.Status with
    {
        RetentionEvictions = Interlocked.Read(ref _diagnosticRetentionEvictions),
        MaintenanceFailures = Interlocked.Read(ref _diagnosticMaintenanceFailures),
        HistoricalIncomplete = Volatile.Read(ref _diagnosticHistoricalIncomplete) != 0
    };

    public bool EnqueueDiagnostic(string level, string category, string message, string? sourceId = null,
        string? correlationId = null, string? coalesceKey = null)
    {
        if (Volatile.Read(ref _diagnosticSessionStarted) == 0) return false;
        var redacted = LogRedactor.Redact(message);
        // Ordinary identical messages coalesce; lifecycle callers use explicit
        // event-state keys so changing child PIDs do not defeat the queue bound.
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(redacted)));
        var key = coalesceKey ?? $"{level}\0{category}\0{sourceId}\0{digest}";
        return _diagnostics.TryEnqueue(key, level, category, redacted, sourceId, correlationId);
    }

    public Task FlushDiagnosticsAsync(CancellationToken cancellationToken = default) => _diagnostics.FlushAsync(cancellationToken);
    public async ValueTask StopDiagnosticsAsync()
    {
        await _diagnostics.DisposeAsync();
        if (Volatile.Read(ref _diagnosticSessionStarted) == 0
            || Interlocked.Exchange(ref _diagnosticSessionStopped, 1) != 0) return;
        // A durable dirty marker precedes all admission. Failure to checkpoint a
        // clean drain leaves that marker in place; a restart must show uncertainty.
        if (!await _writeGate.WaitAsync(TimeSpan.FromMilliseconds(250))) return;
        try
        {
            await using var connection = await OpenDiagnosticConnectionAsync(CancellationToken.None);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE diagnostic_session SET dirty=0,incomplete=MAX(incomplete,$loss)
                WHERE id=1 AND owner=$owner
                """;
            command.Parameters.AddWithValue("$owner", _diagnosticSessionId);
            command.Parameters.AddWithValue("$loss", DiagnosticStatus.IsHealthy ? 0 : 1);
            await command.ExecuteNonQueryAsync();
        }
        catch (Exception) { Volatile.Write(ref _diagnosticHistoricalIncomplete, 1); }
        finally { _writeGate.Release(); }
    }

    private async Task BeginDiagnosticSessionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE diagnostic_session
            SET incomplete=MAX(incomplete,CASE WHEN dirty=1 AND owner<>$owner THEN 1 ELSE 0 END),
                dirty=1,owner=$owner WHERE id=1;
            SELECT incomplete FROM diagnostic_session WHERE id=1;
            """;
        command.Parameters.AddWithValue("$owner", _diagnosticSessionId);
        Volatile.Write(ref _diagnosticHistoricalIncomplete,
            Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)));
        Volatile.Write(ref _diagnosticSessionStarted, 1);
    }

    public void ReportDiagnosticMaintenanceFailure() => Interlocked.Increment(ref _diagnosticMaintenanceFailures);

    public async Task<int> PruneDiagnosticLogsAsync(CancellationToken cancellationToken = default)
    {
        EnsureDiagnosticReserve();
        if (!await _writeGate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken))
            throw new IOException("Diagnostic retention yielded to the essential writer.");
        try
        {
            EnsureDiagnosticReserve();
            await using var connection = await OpenDiagnosticConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var result = await PruneDiagnosticBatchAsync(connection, transaction, 0, cancellationToken);
            EnsureDiagnosticReserve();
            await transaction.CommitAsync(cancellationToken);
            Interlocked.Exchange(ref _diagnosticRetentionEvictions, result.Evictions);
            return result.Removed;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            ReportDiagnosticMaintenanceFailure();
            throw;
        }
        finally { _writeGate.Release(); }
    }

    private void EnsureDiagnosticReserve()
    {
        var space = _readDiagnosticVolumeSpace(DatabasePath);
        if (space.TotalBytes <= 0 || space.AvailableBytes < 0 || space.AvailableBytes > space.TotalBytes)
            throw new IOException("Cannot establish diagnostic volume capacity.");
        var floor = Math.Max(20_000_000_000L, (long)Math.Ceiling(space.TotalBytes * .2m));
        if (space.AvailableBytes - floor < DiagnosticTransactionHeadroom)
            throw new IOException("Diagnostic write refused to preserve the disk reserve.");
    }

    private async Task<SqliteConnection> OpenDiagnosticConnectionAsync(CancellationToken cancellationToken)
    {
        var options = new SqliteConnectionStringBuilder(_connectionString) { DefaultTimeout = 1 };
        var connection = new SqliteConnection(options.ToString());
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    private async Task<(int Removed, long Remaining, long Evictions)> PruneDiagnosticBatchAsync(
        SqliteConnection connection, SqliteTransaction transaction, int incoming, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 1;
        // The initial migration counts legacy rows once, before routing starts.
        // Insert/delete triggers then maintain this value in the same transaction
        // for queued diagnostics, essential logs, and retention alike.
        command.CommandText = "SELECT row_count FROM diagnostic_log_count WHERE id=1";
        var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        var cutoff = _diagnosticClock.UtcNow.AddDays(-30).ToString("O");
        command.CommandText = """
            SELECT id, timestamp_utc < $cutoff,
                COALESCE(length(CAST(timestamp_utc AS BLOB)),0)*3
                + COALESCE(length(CAST(category AS BLOB)),0)*2
                + COALESCE(length(CAST(message AS BLOB)),0)
                + COALESCE(length(CAST(level AS BLOB)),0)
                + COALESCE(length(CAST(source_id AS BLOB)),0)
                + COALESCE(length(CAST(correlation_id AS BLOB)),0)
            FROM logs INDEXED BY ix_logs_retention ORDER BY timestamp_utc,id LIMIT $batch
            """;
        command.Parameters.AddWithValue("$cutoff", cutoff);
        var excess = Math.Max(0, count + incoming - DiagnosticRowLimit);
        command.Parameters.AddWithValue("$batch", DiagnosticPruneBatch);
        var ids = new List<long>();
        long early = 0, estimatedBytes = 8 * 1024 * 1024;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var expired = reader.GetBoolean(1);
                if (!expired && ids.Count >= excess) break;
                // Include every legacy field, plus timestamp/category indexes,
                // two page images and bounded B-tree churn. No field contents are
                // materialized. An impossible oldest row is degraded, never a
                // successful healthy no-op that silently blocks every later pass.
                var rowBytes = checked(reader.GetInt64(2) * 2 + 65536);
                if (rowBytes > DiagnosticTransactionHeadroom - 8 * 1024 * 1024)
                    throw new IOException("Diagnostic retention blocked by an oversized legacy row; maintenance is required.");
                if (estimatedBytes + rowBytes > DiagnosticTransactionHeadroom) break;
                estimatedBytes += rowBytes;
                ids.Add(reader.GetInt64(0));
                if (!expired) early++;
            }
        }
        command.Parameters.Clear();
        if (ids.Count > 0)
        {
            command.CommandText = "DELETE FROM logs WHERE id IN (" + string.Join(',', ids.Select((_, i) => "$id" + i)) + ")";
            for (var i = 0; i < ids.Count; i++) command.Parameters.AddWithValue("$id" + i, ids[i]);
            await command.ExecuteNonQueryAsync(cancellationToken);
            command.Parameters.Clear();
            command.CommandText = "UPDATE diagnostic_retention SET early_evictions=early_evictions+$early WHERE id=1";
            command.Parameters.AddWithValue("$early", early);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        command.Parameters.Clear();
        command.CommandText = "SELECT early_evictions FROM diagnostic_retention WHERE id=1";
        return (ids.Count, count - ids.Count, Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
    }

    private async Task WriteDiagnosticBatchAsync(IReadOnlyList<DiagnosticLogRecord> records, CancellationToken cancellationToken)
    {
        EnsureDiagnosticReserve();
        // The single bounded worker may wait without holding the writer gate.
        // A slow essential commit is backpressure, not evidence loss. Shutdown
        // cancels this wait after the existing five-second drain deadline.
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            EnsureDiagnosticReserve();
            await using var connection = await OpenDiagnosticConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var retention = await PruneDiagnosticBatchAsync(connection, transaction, records.Count, cancellationToken);
            if (retention.Remaining + records.Count > DiagnosticRowLimit)
                throw new IOException("Diagnostic capacity exceeded; bounded maintenance must catch up before optional writes.");
            foreach (var record in records)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandTimeout = 1;
                command.CommandText = "INSERT INTO logs(timestamp_utc,level,category,message,source_id,correlation_id) VALUES($now,$level,$category,$message,$source,$correlation)";
                command.Parameters.AddWithValue("$now", record.LastUtc.ToString("O"));
                command.Parameters.AddWithValue("$level", record.Level);
                command.Parameters.AddWithValue("$category", record.Category);
                var message = record.Occurrences == 1 ? record.Message :
                    $"{record.Message} [Coalesced {record.Occurrences} events; first {record.FirstUtc:O}, last {record.LastUtc:O}; latest message shown.]";
                command.Parameters.AddWithValue("$message", message);
                command.Parameters.AddWithValue("$source", (object?)record.SourceId ?? DBNull.Value);
                command.Parameters.AddWithValue("$correlation", (object?)record.CorrelationId ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            EnsureDiagnosticReserve();
            await transaction.CommitAsync(cancellationToken);
            Interlocked.Exchange(ref _diagnosticRetentionEvictions, retention.Evictions);
        }
        finally { _writeGate.Release(); }
    }
}
