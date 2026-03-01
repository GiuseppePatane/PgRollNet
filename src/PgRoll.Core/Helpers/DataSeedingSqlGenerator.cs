using System.Text;

namespace PgRoll.Core.Helpers;

/// <summary>
/// Generates SQL DML statements from EF Core-style data seed operation data.
/// Shared by EfCoreMigrationConverter and ReflectionConverter to convert
/// InsertDataOperation / UpdateDataOperation / DeleteDataOperation to raw_sql.
/// </summary>
public static class DataSeedingSqlGenerator
{
    /// <summary>
    /// Generates a single INSERT statement with all rows as a VALUES list.
    /// </summary>
    public static string GenerateInsert(
        string? schema, string table, IReadOnlyList<string> columns, object?[,] values)
    {
        var tableSql = QualifyTable(schema, table);
        var cols = string.Join(", ", columns.Select(QuoteIdent));
        var sb = new StringBuilder();
        sb.Append($"INSERT INTO {tableSql} ({cols}) VALUES");

        int rows = values.GetLength(0);
        int colCount = values.GetLength(1);

        for (int r = 0; r < rows; r++)
        {
            if (r > 0) sb.Append(',');
            sb.AppendLine();
            sb.Append("  (");
            for (int c = 0; c < colCount; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append(FormatValue(values[r, c]));
            }
            sb.Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Generates one UPDATE statement per row.
    /// </summary>
    public static string GenerateUpdate(
        string? schema, string table,
        IReadOnlyList<string> keyColumns, object?[,] keyValues,
        IReadOnlyList<string> columns, object?[,] values)
    {
        var tableSql = QualifyTable(schema, table);
        int rows = values.GetLength(0);
        var sb = new StringBuilder();

        for (int r = 0; r < rows; r++)
        {
            if (r > 0) sb.AppendLine();
            sb.Append($"UPDATE {tableSql} SET ");
            sb.Append(string.Join(", ", Enumerable.Range(0, columns.Count)
                .Select(c => $"{QuoteIdent(columns[c])} = {FormatValue(values[r, c])}")));
            sb.Append(" WHERE ");
            sb.Append(string.Join(" AND ", Enumerable.Range(0, keyColumns.Count)
                .Select(c => $"{QuoteIdent(keyColumns[c])} = {FormatValue(keyValues[r, c])}")));
            sb.Append(';');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates one DELETE statement per row.
    /// </summary>
    public static string GenerateDelete(
        string? schema, string table, IReadOnlyList<string> keyColumns, object?[,] keyValues)
    {
        var tableSql = QualifyTable(schema, table);
        int rows = keyValues.GetLength(0);
        var sb = new StringBuilder();

        for (int r = 0; r < rows; r++)
        {
            if (r > 0) sb.AppendLine();
            sb.Append($"DELETE FROM {tableSql} WHERE ");
            sb.Append(string.Join(" AND ", Enumerable.Range(0, keyColumns.Count)
                .Select(c => $"{QuoteIdent(keyColumns[c])} = {FormatValue(keyValues[r, c])}")));
            sb.Append(';');
        }

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string QualifyTable(string? schema, string table) =>
        string.IsNullOrEmpty(schema)
            ? QuoteIdent(table)
            : $"{QuoteIdent(schema)}.{QuoteIdent(table)}";

    private static string QuoteIdent(string name) =>
        $"\"{name.Replace("\"", "\"\"")}\"";

    public static string FormatValue(object? value) =>
        value switch
        {
            null or DBNull => "NULL",
            bool b => b ? "true" : "false",
            string s => $"'{s.Replace("'", "''")}'",
            char c => $"'{c.ToString().Replace("'", "''")}'",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss.ffffff}'",
            DateTimeOffset dto => $"'{dto:O}'",
            Guid g => $"'{g}'",
            byte[] bytes => $"'\\x{Convert.ToHexString(bytes)}'",
            _ => value.ToString() ?? "NULL"
        };
}
