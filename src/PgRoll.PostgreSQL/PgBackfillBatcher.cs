using Npgsql;

namespace PgRoll.PostgreSQL;

public sealed class PgBackfillBatcher
{
    /// <summary>
    /// Updates rows in batches, setting <paramref name="tempColumn"/> = <paramref name="upExpression"/>
    /// for rows where the temp column is still NULL. Runs until no more rows remain.
    /// </summary>
    /// <returns>Total number of rows updated.</returns>
    public static async Task<long> BackfillAsync(
        NpgsqlDataSource dataSource,
        string schema,
        string table,
        string tempColumn,
        string upExpression,
        int batchSize = 1000,
        TimeSpan batchDelay = default,
        CancellationToken ct = default)
    {
        // Use a subquery (not FROM) so that column references in upExpression
        // unambiguously resolve to the target table's columns.
        var sql = $"""
            UPDATE "{schema}"."{table}"
            SET "{tempColumn}" = ({upExpression})
            WHERE ctid IN (
                SELECT ctid FROM "{schema}"."{table}"
                WHERE "{tempColumn}" IS NULL
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            )
            """;

        long total = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            await using var conn = await dataSource.OpenConnectionAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            var updated = await cmd.ExecuteNonQueryAsync(ct);
            total += updated;

            if (updated == 0) break;

            if (batchDelay > TimeSpan.Zero)
                await Task.Delay(batchDelay, ct);
        }

        return total;
    }
}
