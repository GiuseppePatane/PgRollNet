using PgRoll.Core.Helpers;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;

namespace PgRoll.Cli.EfCore;

/// <summary>
/// Result of a reflection-based conversion of EF Core migration operations.
/// </summary>
public sealed record ReflectionConversionResult(
    Migration Migration,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Converts EF Core <c>MigrationOperation</c> objects obtained via reflection
/// (from an externally loaded assembly) into pgroll operations.
/// Works with any EF Core version — matching is done by type name, not by type identity.
/// </summary>
public static class ReflectionConverter
{
    public static ReflectionConversionResult Convert(
        string name, IEnumerable<object> efOperations)
    {
        var pgrollOps = new List<IMigrationOperation>();
        var skipped = new List<string>();

        foreach (var op in efOperations)
        {
            switch (op.GetType().Name)
            {
                case "CreateTableOperation":
                    ConvertCreateTable(op, pgrollOps);
                    break;

                case "DropTableOperation":
                    pgrollOps.Add(new DropTableOperation
                    {
                        Table = Str(op, "Name") ?? ""
                    });
                    break;

                case "RenameTableOperation":
                    pgrollOps.Add(new RenameTableOperation
                    {
                        From = Str(op, "Name") ?? "",
                        To   = Str(op, "NewName") ?? ""
                    });
                    break;

                case "AddColumnOperation":
                    pgrollOps.Add(new AddColumnOperation
                    {
                        Table  = Str(op, "Table") ?? "",
                        Column = MapColumnDef(op),
                        Up     = null
                    });
                    break;

                case "DropColumnOperation":
                    pgrollOps.Add(new DropColumnOperation
                    {
                        Table  = Str(op, "Table") ?? "",
                        Column = Str(op, "Name") ?? ""
                    });
                    break;

                case "RenameColumnOperation":
                    pgrollOps.Add(new RenameColumnOperation
                    {
                        Table = Str(op, "Table") ?? "",
                        From  = Str(op, "Name") ?? "",
                        To    = Str(op, "NewName") ?? ""
                    });
                    break;

                case "AlterColumnOperation":
                    pgrollOps.Add(ConvertAlterColumn(op));
                    break;

                case "CreateIndexOperation":
                    pgrollOps.Add(new CreateIndexOperation
                    {
                        Name    = Str(op, "Name") ?? "",
                        Table   = Str(op, "Table") ?? "",
                        Columns = StrArr(op, "Columns"),
                        Unique  = Bool(op, "IsUnique")
                    });
                    break;

                case "DropIndexOperation":
                    pgrollOps.Add(new DropIndexOperation
                    {
                        Name = Str(op, "Name") ?? ""
                    });
                    break;

                case "AddCheckConstraintOperation":
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table          = Str(op, "Table") ?? "",
                        Name           = Str(op, "Name") ?? "",
                        ConstraintType = "check",
                        Check          = Str(op, "Sql")
                    });
                    break;

                case "DropCheckConstraintOperation":
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = Str(op, "Table") ?? "",
                        Name  = Str(op, "Name") ?? ""
                    });
                    break;

                case "AddUniqueConstraintOperation":
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table          = Str(op, "Table") ?? "",
                        Name           = Str(op, "Name") ?? "",
                        ConstraintType = "unique",
                        Columns        = StrArr(op, "Columns")
                    });
                    break;

                case "DropUniqueConstraintOperation":
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = Str(op, "Table") ?? "",
                        Name  = Str(op, "Name") ?? ""
                    });
                    break;

                case "AddForeignKeyOperation":
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table             = Str(op, "Table") ?? "",
                        Name              = Str(op, "Name") ?? "",
                        ConstraintType    = "foreign_key",
                        Columns           = StrArr(op, "Columns"),
                        ReferencesTable   = Str(op, "PrincipalTable"),
                        ReferencesColumns = StrArr(op, "PrincipalColumns")
                    });
                    break;

                case "DropForeignKeyOperation":
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = Str(op, "Table") ?? "",
                        Name  = Str(op, "Name") ?? ""
                    });
                    break;

                case "SqlOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = Str(op, "Sql") ?? ""
                    });
                    break;

                case "InsertDataOperation":
                {
                    var values = Matrix(op, "Values");
                    if (values.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateInsert(
                                Str(op, "Schema"), Str(op, "Table") ?? "",
                                StrArr(op, "Columns"), values)
                        });
                    break;
                }

                case "UpdateDataOperation":
                {
                    var values = Matrix(op, "Values");
                    if (values.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateUpdate(
                                Str(op, "Schema"), Str(op, "Table") ?? "",
                                StrArr(op, "KeyColumns"), Matrix(op, "KeyValues"),
                                StrArr(op, "Columns"), values)
                        });
                    break;
                }

                case "DeleteDataOperation":
                {
                    var keyValues = Matrix(op, "KeyValues");
                    if (keyValues.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateDelete(
                                Str(op, "Schema"), Str(op, "Table") ?? "",
                                StrArr(op, "KeyColumns"), keyValues)
                        });
                    break;
                }

                // ── Schema ops ────────────────────────────────────────────────

                case "EnsureSchemaOperation":
                    pgrollOps.Add(new CreateSchemaOperation
                    {
                        Schema = Str(op, "Name") ?? ""
                    });
                    break;

                case "DropSchemaOperation":
                    pgrollOps.Add(new DropSchemaOperation
                    {
                        Schema = Str(op, "Name") ?? ""
                    });
                    break;

                // Npgsql-specific — not in standard EF Core, handled via reflection only
                case "RenameSchemaOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRenameSchema(
                            Str(op, "Name") ?? "", Str(op, "NewName") ?? "")
                    });
                    break;

                // ── Sequences ─────────────────────────────────────────────────

                case "CreateSequenceOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateCreate(
                            Str(op, "Schema"), Str(op, "Name") ?? "", ClrType(op),
                            Long(op, "StartValue"), Int(op, "IncrementBy"),
                            NullableLong(op, "MinValue"), NullableLong(op, "MaxValue"),
                            Bool(op, "IsCyclic"))
                    });
                    break;

                case "AlterSequenceOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateAlter(
                            Str(op, "Schema"), Str(op, "Name") ?? "",
                            Int(op, "IncrementBy"),
                            NullableLong(op, "MinValue"), NullableLong(op, "MaxValue"),
                            Bool(op, "IsCyclic"))
                    });
                    break;

                case "DropSequenceOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateDrop(
                            Str(op, "Schema"), Str(op, "Name") ?? "")
                    });
                    break;

                case "RestartSequenceOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRestart(
                            Str(op, "Schema"), Str(op, "Name") ?? "",
                            NullableLong(op, "StartValue"))
                    });
                    break;

                case "RenameSequenceOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRename(
                            Str(op, "Schema"), Str(op, "Name") ?? "",
                            Str(op, "NewName"), Str(op, "NewSchema"))
                    });
                    break;

                // ── Index ─────────────────────────────────────────────────────

                case "RenameIndexOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRenameIndex(
                            Str(op, "Table"), Str(op, "Name") ?? "", Str(op, "NewName") ?? "")
                    });
                    break;

                // ── Primary key ───────────────────────────────────────────────

                case "AddPrimaryKeyOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateAddPrimaryKey(
                            Str(op, "Schema"), Str(op, "Table") ?? "",
                            Str(op, "Name") ?? "", StrArr(op, "Columns"))
                    });
                    break;

                case "DropPrimaryKeyOperation":
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateDropPrimaryKey(
                            Str(op, "Schema"), Str(op, "Table") ?? "",
                            Str(op, "Name") ?? "")
                    });
                    break;

                default:
                    skipped.Add(op.GetType().Name);
                    break;
            }
        }

        return new ReflectionConversionResult(
            new Migration { Name = name, Operations = pgrollOps },
            skipped);
    }

    // ── CreateTable (also emits extra constraint operations) ──────────────────

    private static void ConvertCreateTable(object op, List<IMigrationOperation> result)
    {
        var tableName = Str(op, "Name") ?? "";

        var pkObj    = Obj(op, "PrimaryKey");
        var pkCols   = pkObj is not null ? StrArr(pkObj, "Columns") : [];

        // For composite PKs (> 1 column) we emit the PK as a separate raw_sql after the table,
        // because pgroll's create_table only supports single-column PRIMARY KEY inline.
        var isCompositePk = pkCols.Count > 1;  // IReadOnlyList<string>.Count

        var columns = List(op, "Columns")
            .Select(c => new ColumnDefinition
            {
                Name       = Str(c, "Name") ?? "",
                Type       = MapType(Str(c, "ColumnType"), ClrType(c)),
                Nullable   = Bool(c, "IsNullable"),
                Default    = Str(c, "DefaultValueSql"),
                PrimaryKey = !isCompositePk && pkCols.Contains(Str(c, "Name") ?? "")
            })
            .ToList();

        result.Add(new CreateTableOperation { Table = tableName, Columns = columns });

        // Emit composite PK as raw_sql ADD CONSTRAINT PRIMARY KEY (col1, col2, ...)
        if (isCompositePk && pkObj is not null)
        {
            var pkName = Str(pkObj, "Name") ?? $"PK_{tableName}";
            var schema = Str(pkObj, "Schema");
            result.Add(new RawSqlOperation
            {
                Sql = SequenceSqlGenerator.GenerateAddPrimaryKey(schema, tableName, pkName, pkCols)
            });
        }

        foreach (var uc in List(op, "UniqueConstraints"))
            result.Add(new CreateConstraintOperation
            {
                Table          = tableName,
                Name           = Str(uc, "Name") ?? "",
                ConstraintType = "unique",
                Columns        = StrArr(uc, "Columns")
            });

        foreach (var fk in List(op, "ForeignKeys"))
            result.Add(new CreateConstraintOperation
            {
                Table             = tableName,
                Name              = Str(fk, "Name") ?? "",
                ConstraintType    = "foreign_key",
                Columns           = StrArr(fk, "Columns"),
                ReferencesTable   = Str(fk, "PrincipalTable"),
                ReferencesColumns = StrArr(fk, "PrincipalColumns")
            });

        foreach (var ck in List(op, "CheckConstraints"))
            result.Add(new CreateConstraintOperation
            {
                Table          = tableName,
                Name           = Str(ck, "Name") ?? "",
                ConstraintType = "check",
                Check          = Str(ck, "Sql")
            });
    }

    // ── AlterColumn ───────────────────────────────────────────────────────────

    private static AlterColumnOperation ConvertAlterColumn(object op)
    {
        var oldCol = Obj(op, "OldColumn");

        var newColType = Str(op, "ColumnType");
        var oldColType = oldCol is not null ? Str(oldCol, "ColumnType") : null;
        var dataType   = newColType is not null && oldColType is not null && newColType != oldColType
            ? newColType
            : null;

        var newNullable = Bool(op, "IsNullable");
        var oldNullable = oldCol is not null && Bool(oldCol, "IsNullable");
        bool? notNull   = (!newNullable && oldNullable) ? true : null;

        var newDefault = Str(op, "DefaultValueSql");
        var oldDefault = oldCol is not null ? Str(oldCol, "DefaultValueSql") : null;
        var defaultVal = newDefault != oldDefault ? newDefault : null;

        return new AlterColumnOperation
        {
            Table    = Str(op, "Table") ?? "",
            Column   = Str(op, "Name") ?? "",
            DataType = dataType,
            NotNull  = notNull,
            Default  = defaultVal,
            Up       = null
        };
    }

    // ── Column definition helper ──────────────────────────────────────────────

    private static ColumnDefinition MapColumnDef(object op) => new()
    {
        Name    = Str(op, "Name") ?? "",
        Type    = MapType(Str(op, "ColumnType"), ClrType(op)),
        Nullable = Bool(op, "IsNullable"),
        Default = Str(op, "DefaultValueSql")
    };

    // ── Type mapper ───────────────────────────────────────────────────────────

    internal static string MapType(string? columnType, Type? clrType)
    {
        // Explicit ColumnType always wins (handles text[], jsonb, uuid, custom types, etc.)
        if (!string.IsNullOrWhiteSpace(columnType))
            return columnType;

        if (clrType is null)
            return "text";

        var t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        if (t == typeof(string))            return "text";
        if (t == typeof(int))               return "integer";
        if (t == typeof(long))              return "bigint";
        if (t == typeof(short))             return "smallint";
        if (t == typeof(bool))              return "boolean";
        if (t == typeof(Guid))              return "uuid";
        if (t == typeof(decimal))           return "numeric";
        if (t == typeof(double))            return "double precision";
        if (t == typeof(float))             return "real";
        if (t == typeof(byte[]))            return "bytea";
        if (t == typeof(DateTime))          return "timestamp with time zone";
        if (t == typeof(DateTimeOffset))    return "timestamp with time zone";
        if (t == typeof(DateOnly))          return "date";
        if (t == typeof(TimeOnly))          return "time";

        return "text";
    }

    // ── Reflection helpers ────────────────────────────────────────────────────

    private static string? Str(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o) as string;

    private static bool Bool(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o) is true;

    private static object? Obj(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o);

    private static Type? ClrType(object o) =>
        o.GetType().GetProperty("ClrType")?.GetValue(o) as Type;

    private static IEnumerable<object> List(object o, string prop)
    {
        var val = o.GetType().GetProperty(prop)?.GetValue(o);
        return val is System.Collections.IEnumerable e ? e.Cast<object>() : [];
    }

    private static IReadOnlyList<string> StrArr(object o, string prop)
    {
        var val = o.GetType().GetProperty(prop)?.GetValue(o);
        return val switch
        {
            string[] arr            => arr,
            IEnumerable<string> seq => seq.ToArray(),
            System.Collections.IEnumerable e => e.Cast<string>().ToArray(),
            _ => []
        };
    }

    private static int Int(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o) is int v ? v : 0;

    private static long Long(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o) is long v ? v : 0L;

    private static long? NullableLong(object o, string prop)
    {
        var val = o.GetType().GetProperty(prop)?.GetValue(o);
        return val is long v ? v : null;
    }

    private static object?[,] Matrix(object o, string prop) =>
        o.GetType().GetProperty(prop)?.GetValue(o) as object?[,] ?? new object?[0, 0];
}
