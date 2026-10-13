using Microsoft.Data.Sqlite;

namespace BroadcastRouter.Infrastructure;

public sealed partial class SqliteDataStore
{
    private readonly DiagnosticPersistenceQueue _diagnostics;
    public DiagnosticPersistenceStatus DiagnosticStatus => _diagnostics.Status;

    public bool EnqueueDiagnostic(string level, string category, string message, string? sourceId = null,
        string? correlationId = null, string? coalesceKey = null)
    {
        var redacted = LogRedactor.Redact(message);
        // Ordinary identical messages coalesce; lifecycle callers use explicit
        // event-state keys so changing child PIDs do not defeat the queue bound.
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(redacted)));
        var key = coalesceKey ?? $"{level}\0{category}\0{sourceId}\0{digest}";
        return _diagnostics.TryEnqueue(key, level, category, redacted, sourceId, correlationId);
    }

    public Task FlushDiagnosticsAsync(CancellationToken cancellationToken = default) => _diagnostics.FlushAsync(cancellationToken);
    public ValueTask StopDiagnosticsAsync() => _diagnostics.DisposeAsync();

    private async Task WriteDiagnosticBatchAsync(IReadOnlyList<DiagnosticLogRecord> records, CancellationToken cancellationToken)
    {
        // Optional summaries never monopolize the essential-state writer queue.
        if (!await _writeGate.WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken))
            throw new IOException("Diagnostic writer is busy; summaries could not be committed.");
        try
        {
            var diagnosticConnection = new SqliteConnectionStringBuilder(_connectionString) { DefaultTimeout = 1 };
            await using var connection = new SqliteConnection(diagnosticConnection.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
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
            await transaction.CommitAsync(cancellationToken);
        }
        finally { _writeGate.Release(); }
    }
}
