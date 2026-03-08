using System.Text;

namespace PgRoll.Core.Helpers;

/// <summary>
/// Generates SQL DDL statements for sequence, index-rename, schema-rename,
/// and primary-key operations that EF Core can emit but that have no dedicated
/// pgroll operation type. Shared by EfCoreMigrationConverter and ReflectionConverter.
/// </summary>
public static class SequenceSqlGenerator
{
    // ── Sequences ─────────────────────────────────────────────────────────────

    /// <summary>Generates a CREATE SEQUENCE statement.</summary>
    public static string GenerateCreate(
        string? schema, string name, Type? clrType,
        long startValue, int incrementBy,
        long? minValue, long? maxValue, bool isCyclic)
    {
        var seqIdent = QualifySequence(schema, name);
        var sb = new StringBuilder();
        sb.Append($"CREATE SEQUENCE {seqIdent}");

        var dataType = MapSequenceType(clrType);
        if (dataType is not null)
            sb.Append($" AS {dataType}");

        sb.Append($" START WITH {startValue}");
        sb.Append($" INCREMENT BY {incrementBy}");
        sb.Append(minValue.HasValue ? $" MINVALUE {minValue.Value}" : " NO MINVALUE");
        sb.Append(maxValue.HasValue ? $" MAXVALUE {maxValue.Value}" : " NO MAXVALUE");
        sb.Append(isCyclic ? " CYCLE" : " NO CYCLE");
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>Generates an ALTER SEQUENCE statement.</summary>
    public static string GenerateAlter(
        string? schema, string name,
        int incrementBy, long? minValue, long? maxValue, bool isCyclic)
    {
        var seqIdent = QualifySequence(schema, name);
        var sb = new StringBuilder();
        sb.Append($"ALTER SEQUENCE {seqIdent}");
        sb.Append($" INCREMENT BY {incrementBy}");
        sb.Append(minValue.HasValue ? $" MINVALUE {minValue.Value}" : " NO MINVALUE");
        sb.Append(maxValue.HasValue ? $" MAXVALUE {maxValue.Value}" : " NO MAXVALUE");
        sb.Append(isCyclic ? " CYCLE" : " NO CYCLE");
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>Generates a DROP SEQUENCE IF EXISTS statement.</summary>
    public static string GenerateDrop(string? schema, string name) =>
        $"DROP SEQUENCE IF EXISTS {QualifySequence(schema, name)};";

    /// <summary>
    /// Generates an ALTER SEQUENCE … RESTART statement.
    /// When <paramref name="startValue"/> is null the sequence restarts from its original start value.
    /// </summary>
    public static string GenerateRestart(string? schema, string name, long? startValue) =>
        startValue.HasValue
            ? $"ALTER SEQUENCE {QualifySequence(schema, name)} RESTART WITH {startValue.Value};"
            : $"ALTER SEQUENCE {QualifySequence(schema, name)} RESTART;";

    /// <summary>
    /// Generates RENAME (and optionally SET SCHEMA) statements for a sequence.
    /// </summary>
    public static string GenerateRename(
        string? schema, string name, string? newName, string? newSchema)
    {
        var sb = new StringBuilder();
        var originalIdent = QualifySequence(schema, name);
        var effectiveName = newName ?? name;

        if (newName is not null && newName != name)
            sb.Append($"ALTER SEQUENCE {originalIdent} RENAME TO {QuoteIdent(effectiveName)};");

        if (newSchema is not null && newSchema != schema)
        {
            if (sb.Length > 0)
                sb.AppendLine();
            // After rename the sequence lives under old schema with new name
            var renamedIdent = QualifySequence(schema, effectiveName);
            sb.Append($"ALTER SEQUENCE {renamedIdent} SET SCHEMA {QuoteIdent(newSchema)};");
        }

        // If nothing changed, emit a no-op comment
        if (sb.Length == 0)
            return $"-- RenameSequenceOperation: no effective change for {QuoteIdent(name)}";

        return sb.ToString();
    }

    // ── Index ─────────────────────────────────────────────────────────────────

    /// <summary>Generates an ALTER INDEX … RENAME TO statement.</summary>
    public static string GenerateRenameIndex(string? table, string name, string newName)
    {
        // In PostgreSQL, ALTER INDEX does not reference the table; index names are schema-scoped.
        // table parameter kept for symmetry with EF Core's RenameIndexOperation.
        _ = table; // unused
        return $"ALTER INDEX {QuoteIdent(name)} RENAME TO {QuoteIdent(newName)};";
    }

    // ── Schema ────────────────────────────────────────────────────────────────

    /// <summary>Generates an ALTER SCHEMA … RENAME TO statement.</summary>
    public static string GenerateRenameSchema(string name, string newName) =>
        $"ALTER SCHEMA {QuoteIdent(name)} RENAME TO {QuoteIdent(newName)};";

    // ── Primary key ───────────────────────────────────────────────────────────

    /// <summary>Generates an ALTER TABLE … ADD CONSTRAINT … PRIMARY KEY statement.</summary>
    public static string GenerateAddPrimaryKey(
        string? schema, string table, string constraintName, IReadOnlyList<string> columns)
    {
        var tableIdent = string.IsNullOrEmpty(schema)
            ? QuoteIdent(table)
            : $"{QuoteIdent(schema)}.{QuoteIdent(table)}";
        var cols = string.Join(", ", columns.Select(QuoteIdent));
        return $"ALTER TABLE {tableIdent} ADD CONSTRAINT {QuoteIdent(constraintName)} PRIMARY KEY ({cols});";
    }

    /// <summary>Generates an ALTER TABLE … DROP CONSTRAINT statement.</summary>
    public static string GenerateDropPrimaryKey(
        string? schema, string table, string constraintName)
    {
        var tableIdent = string.IsNullOrEmpty(schema)
            ? QuoteIdent(table)
            : $"{QuoteIdent(schema)}.{QuoteIdent(table)}";
        return $"ALTER TABLE {tableIdent} DROP CONSTRAINT {QuoteIdent(constraintName)};";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string QualifySequence(string? schema, string name) =>
        string.IsNullOrEmpty(schema)
            ? QuoteIdent(name)
            : $"{QuoteIdent(schema)}.{QuoteIdent(name)}";

    private static string QuoteIdent(string name) =>
        $"\"{name.Replace("\"", "\"\"")}\"";

    private static string? MapSequenceType(Type? clrType)
    {
        if (clrType is null)
            return null;
        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;
        if (t == typeof(short))
            return "smallint";
        if (t == typeof(int))
            return "integer";
        if (t == typeof(long))
            return "bigint";
        if (t == typeof(decimal))
            return "bigint"; // EF Core uses decimal for bigint sequences
        return null;
    }
}
