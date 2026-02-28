using Npgsql;

namespace PgRoll.PostgreSQL;

public sealed class PgTriggerManager
{
    public static string TriggerName(string table, string column) =>
        $"_pgroll_trigger_{table}_{column}";

    /// <summary>
    /// Creates a BEFORE INSERT OR UPDATE trigger that propagates writes bidirectionally:
    /// <list type="bullet">
    ///   <item>Old-app write (base schema path): <c>tempColumn = upExpression</c></item>
    ///   <item>New-app write (version-schema path, when <paramref name="downExpression"/> is set):
    ///         <c>column = downExpression</c></item>
    /// </list>
    /// <para>
    /// When <paramref name="tempColumnAlias"/> is provided the <c>tempColumn</c> is exposed under
    /// that alias inside the Down expression's <c>FROM (SELECT …) AS _r</c> sub-select, allowing
    /// the user to write natural expressions such as <c>"SUBSTR(full_name, 5)"</c> instead of the
    /// internal dup-column name.
    /// </para>
    /// </summary>
    public static async Task CreateTriggerAsync(
        NpgsqlConnection conn,
        string schema,
        string table,
        string column,
        string tempColumn,
        string upExpression,
        string versionSchema,
        CancellationToken ct = default,
        string? downExpression = null,
        string? tempColumnAlias = null)
    {
        var triggerName = TriggerName(table, column);
        var escapedUpExpr = upExpression.Replace("'", "''");
        var escapedVersionSchema = versionSchema.Replace("'", "''");

        // Build the DOWN branch (only when a down expression is supplied).
        string downBranch;
        if (downExpression is not null)
        {
            var escapedDownExpr = downExpression.Replace("'", "''");
            // If the caller supplies an alias (e.g. "full_name" for "_pgroll_dup_name") the
            // sub-select exposes the dup column under that name so the user can write natural
            // expressions. Without an alias just use NEW.* directly.
            var downFrom = tempColumnAlias is not null
                ? $"SELECT NEW.*, NEW.\"{tempColumn}\" AS \"{tempColumnAlias}\""
                : "SELECT NEW.*";
            downBranch = $"""
                    ELSE
                        NEW."{column}" = (SELECT {escapedDownExpr} FROM ({downFrom}) AS _r);
                """;
        }
        else
        {
            downBranch = string.Empty;
        }

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
                {downBranch}END IF;
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
