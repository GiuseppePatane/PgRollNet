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
        // Uses a FROM subquery to avoid evaluating upExpression twice (once in SET, once in WHERE).
        // Double evaluation is unsafe for volatile functions (e.g. NOW(), gen_random_uuid()).
        // The WHERE excludes rows where upExpression is NULL to prevent an infinite loop when
        // the source column is NULL (NULL::type = NULL → row would be re-selected every batch).
        var sql = $"""
            UPDATE "{schema}"."{table}" AS _t
            SET "{tempColumn}" = ({upExpression})
            FROM (
                SELECT ctid FROM "{schema}"."{table}"
                WHERE "{tempColumn}" IS NULL
                AND ({upExpression}) IS NOT NULL
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            ) AS _batch
            WHERE _t.ctid = _batch.ctid
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

            if (updated == 0)
                break;

            if (batchDelay > TimeSpan.Zero)
                await Task.Delay(batchDelay, ct);
        }

        return total;
    }
}
