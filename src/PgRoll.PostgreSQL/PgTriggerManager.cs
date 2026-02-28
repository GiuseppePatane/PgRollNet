using Npgsql;

namespace PgRoll.PostgreSQL;

public sealed class PgTriggerManager
{
    public static string TriggerName(string table, string column) =>
        $"_pgroll_trigger_{table}_{column}";

    /// <summary>
    /// Creates a BEFORE INSERT OR UPDATE trigger that propagates writes from the base schema
    /// to the temporary column using the given Up expression, unless the write came via the
    /// version schema (to avoid infinite recursion).
    /// </summary>
    public static async Task CreateTriggerAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        string column,
        string tempColumn,
        string upExpression,
        string versionSchema,
        CancellationToken ct = default)
    {
        var triggerName = TriggerName(table, column);
        var escapedUpExpr = upExpression.Replace("'", "''");
        var escapedVersionSchema = versionSchema.Replace("'", "''");

        // Use FROM (SELECT NEW.*) AS _r so that unqualified column names in upExpression
        // (e.g. "UPPER(name)") resolve correctly to NEW.column_name in PL/pgSQL context.
        var functionSql = $"""
            CREATE OR REPLACE FUNCTION "{schema}"."{triggerName}"()
            RETURNS TRIGGER LANGUAGE PLPGSQL AS $func$
            DECLARE _sp text;
            BEGIN
                SELECT current_setting('search_path', TRUE) INTO _sp;
                IF _sp IS DISTINCT FROM '{escapedVersionSchema}' THEN
                    NEW."{tempColumn}" = (SELECT {escapedUpExpr} FROM (SELECT NEW.*) AS _r);
                END IF;
                RETURN NEW;
            END;
            $func$
            """;

        var triggerSql = $"""
            CREATE TRIGGER "{triggerName}"
            BEFORE INSERT OR UPDATE ON "{schema}"."{table}"
            FOR EACH ROW EXECUTE FUNCTION "{schema}"."{triggerName}"()
            """;

        await ExecAsync(conn, functionSql, ct);
        await ExecAsync(conn, triggerSql, ct);
    }

    /// <summary>Drops the trigger and its associated function.</summary>
    public static async Task DropTriggerAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        string column,
        CancellationToken ct = default)
    {
        var triggerName = TriggerName(table, column);
        await ExecAsync(conn, $"""DROP TRIGGER IF EXISTS "{triggerName}" ON "{schema}"."{table}" """, ct);
        await ExecAsync(conn, $"""DROP FUNCTION IF EXISTS "{schema}"."{triggerName}"()""", ct);
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
