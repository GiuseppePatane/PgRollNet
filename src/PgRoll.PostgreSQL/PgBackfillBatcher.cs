using Npgsql;

namespace PgRoll.PostgreSQL;

/// <summary>Progress snapshot reported after each backfill batch.</summary>
public sealed record BackfillProgress(
    string Schema,
    string Table,
    int BatchNumber,
    long RowsUpdatedThisBatch,
    long TotalRowsUpdated);

public sealed class PgBackfillBatcher
{
    /// <summary>
    /// Updates rows in batches, setting <paramref name="tempColumn"/> = <paramref name="upExpression"/>
    /// for rows where the temp column is still NULL. Runs until no more rows remain.
    /// </summary>
    /// <param name="progress">Optional progress reporter invoked after each batch.</param>
    /// <returns>Total number of rows updated.</returns>
    public static async Task<long> BackfillAsync(
        NpgsqlDataSource dataSource,
        string schema,
        string table,
        string tempColumn,
        string upExpression,
        int batchSize = 1000,
        TimeSpan batchDelay = default,
        IProgress<BackfillProgress>? progress = null,
        CancellationToken ct = default)
    {
        // Use a subquery (not FROM) so that column references in upExpression
        // unambiguously resolve to the target table's columns.
        // The inner WHERE excludes rows where upExpression would evaluate to NULL:
        // those rows would be re-selected on every batch (since the dup column stays NULL),
        // causing an infinite loop when the source column contains NULLs.
        var sql = $"""
            UPDATE "{schema}"."{table}"
            SET "{tempColumn}" = ({upExpression})
            WHERE ctid IN (
                SELECT ctid FROM "{schema}"."{table}"
                WHERE "{tempColumn}" IS NULL
                AND ({upExpression}) IS NOT NULL
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            """;

        long total = 0;
        int batchNumber = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            var updated = await cmd.ExecuteNonQueryAsync(ct);
            total += updated;
            batchNumber++;

            if (updated > 0)
                progress?.Report(new BackfillProgress(schema, table, batchNumber, updated, total));

            if (updated == 0) break;

            if (batchDelay > TimeSpan.Zero)
                await Task.Delay(batchDelay, ct);
        }

        return total;
    }
}
