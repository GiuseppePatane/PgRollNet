using Microsoft.EntityFrameworkCore.Migrations;
using PgRoll.Core.Helpers;
using PgRoll.Core.Operations;
using PgRollMigration = PgRoll.Core.Models.Migration;
using EfAddColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation;
using EfAddCheckConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.AddCheckConstraintOperation;
using EfAddForeignKey = Microsoft.EntityFrameworkCore.Migrations.Operations.AddForeignKeyOperation;
using EfAddUniqueConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.AddUniqueConstraintOperation;
using EfAlterColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation;
using EfCreateIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation;
using EfCreateTable = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation;
using EfDropCheckConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.DropCheckConstraintOperation;
using EfDropColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation;
using EfDropForeignKey = Microsoft.EntityFrameworkCore.Migrations.Operations.DropForeignKeyOperation;
using EfDropIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.DropIndexOperation;
using EfDropTable = Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation;
using EfDropUniqueConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.DropUniqueConstraintOperation;
using EfMigrationOperation = Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation;
using EfRenameColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameColumnOperation;
using EfRenameIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameIndexOperation;
using EfDeleteData = Microsoft.EntityFrameworkCore.Migrations.Operations.DeleteDataOperation;
using EfInsertData = Microsoft.EntityFrameworkCore.Migrations.Operations.InsertDataOperation;
using EfRenameTable = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameTableOperation;
using EfSql = Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation;
using EfUpdateData = Microsoft.EntityFrameworkCore.Migrations.Operations.UpdateDataOperation;
using EfEnsureSchema = Microsoft.EntityFrameworkCore.Migrations.Operations.EnsureSchemaOperation;
using EfDropSchema = Microsoft.EntityFrameworkCore.Migrations.Operations.DropSchemaOperation;
using EfCreateSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateSequenceOperation;
using EfAlterSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.AlterSequenceOperation;
using EfDropSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.DropSequenceOperation;
using EfRestartSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.RestartSequenceOperation;
using EfRenameSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameSequenceOperation;
using EfAddPrimaryKey = Microsoft.EntityFrameworkCore.Migrations.Operations.AddPrimaryKeyOperation;
using EfDropPrimaryKey = Microsoft.EntityFrameworkCore.Migrations.Operations.DropPrimaryKeyOperation;

namespace PgRoll.EntityFrameworkCore;

/// <summary>
/// Result of converting EF Core migration operations to a pgroll <see cref="PgRollMigration"/>.
/// </summary>
public sealed record ConversionResult(
    PgRollMigration Migration,
    IReadOnlyList<string> Skipped);

/// <summary>
/// Converts EF Core <see cref="EfMigrationOperation"/> instances into equivalent pgroll operations.
/// </summary>
public static class EfCoreMigrationConverter
{
    /// <summary>
    /// Converts a list of EF Core migration operations into a pgroll <see cref="ConversionResult"/>.
    /// </summary>
    public static ConversionResult Convert(string name, IEnumerable<EfMigrationOperation> operations)
    {
        var pgrollOps = new List<IMigrationOperation>();
        var skipped = new List<string>();

        foreach (var op in operations)
        {
            switch (op)
            {
                case EfCreateTable ct:
                    ConvertCreateTable(ct, pgrollOps);
                    break;

                case EfDropTable dt:
                    pgrollOps.Add(new DropTableOperation { Table = dt.Name });
                    break;

                case EfRenameTable rt:
                    pgrollOps.Add(new RenameTableOperation
                    {
                        From = rt.Name ?? throw new InvalidOperationException("RenameTableOperation.Name is required."),
                        To = rt.NewName ?? throw new InvalidOperationException("RenameTableOperation.NewName is required.")
                    });
                    break;

                case EfAddColumn ac:
                    pgrollOps.Add(new AddColumnOperation
                    {
                        Table = ac.Table,
                        Column = MapColumnDefinition(ac),
                        Up = null
                    });
                    break;

                case EfDropColumn dc:
                    pgrollOps.Add(new DropColumnOperation
                    {
                        Table = dc.Table,
                        Column = dc.Name
                    });
                    break;

                case EfRenameColumn rc:
                    pgrollOps.Add(new RenameColumnOperation
                    {
                        Table = rc.Table,
                        From = rc.Name,
                        To = rc.NewName
                    });
                    break;

                case EfAlterColumn alc:
                    pgrollOps.Add(ConvertAlterColumn(alc));
                    break;

                case EfCreateIndex ci:
                    pgrollOps.Add(new CreateIndexOperation
                    {
                        Name = ci.Name,
                        Table = ci.Table,
                        Columns = ci.Columns,
                        Unique = ci.IsUnique
                    });
                    break;

                case EfDropIndex di:
                    pgrollOps.Add(new DropIndexOperation { Name = di.Name });
                    break;

                case EfRenameIndex ri:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRenameIndex(ri.Table, ri.Name, ri.NewName)
                    });
                    break;

                case EfAddCheckConstraint acc:
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table = acc.Table,
                        Name = acc.Name,
                        ConstraintType = "check",
                        Check = acc.Sql
                    });
                    break;

                case EfDropCheckConstraint dcc:
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = dcc.Table,
                        Name = dcc.Name
                    });
                    break;

                case EfAddUniqueConstraint auc:
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table = auc.Table,
                        Name = auc.Name,
                        ConstraintType = "unique",
                        Columns = auc.Columns
                    });
                    break;

                case EfDropUniqueConstraint duc:
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = duc.Table,
                        Name = duc.Name
                    });
                    break;

                case EfAddForeignKey afk:
                    pgrollOps.Add(new CreateConstraintOperation
                    {
                        Table = afk.Table,
                        Name = afk.Name,
                        ConstraintType = "foreign_key",
                        Columns = afk.Columns,
                        ReferencesTable = afk.PrincipalTable,
                        ReferencesColumns = afk.PrincipalColumns
                    });
                    break;

                case EfDropForeignKey dfk:
                    pgrollOps.Add(new DropConstraintOperation
                    {
                        Table = dfk.Table,
                        Name = dfk.Name
                    });
                    break;

                case EfSql sql:
                    pgrollOps.Add(new RawSqlOperation { Sql = sql.Sql });
                    break;

                case EfInsertData id:
                    if (id.Values.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateInsert(
                                id.Schema, id.Table, id.Columns, id.Values)
                        });
                    break;

                case EfUpdateData ud:
                    if (ud.Values.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateUpdate(
                                ud.Schema, ud.Table,
                                ud.KeyColumns, ud.KeyValues,
                                ud.Columns, ud.Values)
                        });
                    break;

                case EfDeleteData dd:
                    if (dd.KeyValues.GetLength(0) > 0)
                        pgrollOps.Add(new RawSqlOperation
                        {
                            Sql = DataSeedingSqlGenerator.GenerateDelete(
                                dd.Schema, dd.Table, dd.KeyColumns, dd.KeyValues)
                        });
                    break;

                // ── Schema ops ────────────────────────────────────────────────

                case EfEnsureSchema es:
                    pgrollOps.Add(new CreateSchemaOperation { Schema = es.Name });
                    break;

                case EfDropSchema ds:
                    pgrollOps.Add(new DropSchemaOperation { Schema = ds.Name });
                    break;

                // ── Sequences ─────────────────────────────────────────────────

                case EfCreateSequence cs:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateCreate(
                            cs.Schema, cs.Name, cs.ClrType,
                            cs.StartValue, cs.IncrementBy,
                            cs.MinValue, cs.MaxValue, cs.IsCyclic)
                    });
                    break;

                case EfAlterSequence als:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateAlter(
                            als.Schema, als.Name,
                            als.IncrementBy, als.MinValue, als.MaxValue, als.IsCyclic)
                    });
                    break;

                case EfDropSequence dseq:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateDrop(dseq.Schema, dseq.Name)
                    });
                    break;

                case EfRestartSequence rseq:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRestart(rseq.Schema, rseq.Name, rseq.StartValue)
                    });
                    break;

                case EfRenameSequence rnseq:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateRename(
                            rnseq.Schema, rnseq.Name, rnseq.NewName, rnseq.NewSchema)
                    });
                    break;

                // ── Primary key ───────────────────────────────────────────────

                case EfAddPrimaryKey apk:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateAddPrimaryKey(
                            apk.Schema, apk.Table, apk.Name, apk.Columns)
                    });
                    break;

                case EfDropPrimaryKey dpk:
                    pgrollOps.Add(new RawSqlOperation
                    {
                        Sql = SequenceSqlGenerator.GenerateDropPrimaryKey(
                            dpk.Schema, dpk.Table, dpk.Name)
                    });
                    break;

                default:
                    skipped.Add(op.GetType().Name);
                    break;
            }
        }

        var migration = new PgRollMigration
        {
            Name = name,
            Operations = pgrollOps
        };

        return new ConversionResult(migration, skipped);
    }

    /// <summary>
    /// Converts EF Core operations built by a <see cref="MigrationBuilder"/> action.
    /// </summary>
    public static ConversionResult Convert(
        string name,
        Action<MigrationBuilder> upAction,
        string? activeProvider = null)
    {
        var builder = new MigrationBuilder(activeProvider);
        upAction(builder);
        return Convert(name, builder.Operations);
    }

    private static void ConvertCreateTable(EfCreateTable ct, List<IMigrationOperation> result)
    {
        var pkColumns = ct.PrimaryKey?.Columns ?? [];

        // For composite PKs (> 1 column) we emit the PK as a separate raw_sql after the table,
        // because pgroll's create_table only supports single-column PRIMARY KEY inline.
        var isCompositePk = pkColumns.Length > 1;

        var columns = ct.Columns.Select(c => new ColumnDefinition
        {
            Name = c.Name,
            Type = EfCoreTypeMapper.MapColumnType(c.ColumnType, c.ClrType),
            Nullable = c.IsNullable,
            Default = c.DefaultValueSql,
            PrimaryKey = !isCompositePk && pkColumns.Contains(c.Name)
        }).ToList();

        result.Add(new CreateTableOperation
        {
            Table = ct.Name,
            Columns = columns
        });

        // Emit composite PK as raw_sql ADD CONSTRAINT PRIMARY KEY (col1, col2, ...)
        if (isCompositePk && ct.PrimaryKey is not null)
        {
            result.Add(new RawSqlOperation
            {
                Sql = SequenceSqlGenerator.GenerateAddPrimaryKey(
                    ct.PrimaryKey.Schema, ct.Name, ct.PrimaryKey.Name, ct.PrimaryKey.Columns)
            });
        }

        foreach (var uc in ct.UniqueConstraints)
        {
            result.Add(new CreateConstraintOperation
            {
                Table = ct.Name,
                Name = uc.Name,
                ConstraintType = "unique",
                Columns = uc.Columns
            });
        }

        foreach (var fk in ct.ForeignKeys)
        {
            result.Add(new CreateConstraintOperation
            {
                Table = ct.Name,
                Name = fk.Name,
                ConstraintType = "foreign_key",
                Columns = fk.Columns,
                ReferencesTable = fk.PrincipalTable,
                ReferencesColumns = fk.PrincipalColumns
            });
        }

        foreach (var ck in ct.CheckConstraints)
        {
            result.Add(new CreateConstraintOperation
            {
                Table = ct.Name,
                Name = ck.Name,
                ConstraintType = "check",
                Check = ck.Sql
            });
        }
    }

    private static ColumnDefinition MapColumnDefinition(EfAddColumn ac) =>
        new()
        {
            Name = ac.Name,
            Type = EfCoreTypeMapper.MapColumnType(ac.ColumnType, ac.ClrType),
            Nullable = ac.IsNullable,
            Default = ac.DefaultValueSql
        };

    private static AlterColumnOperation ConvertAlterColumn(EfAlterColumn alc)
    {
        string? dataType = null;
        string? up = null;
        string? down = null;
        if (alc.ColumnType is not null && alc.OldColumn.ColumnType is not null
            && alc.ColumnType != alc.OldColumn.ColumnType)
        {
            dataType = alc.ColumnType;
            up = $"{alc.Name}::{alc.ColumnType}";
            down = $"{alc.Name}::{alc.OldColumn.ColumnType}";
        }

        bool? notNull = null;
        if (!alc.IsNullable && alc.OldColumn.IsNullable)
            notNull = true;

        string? defaultValue = null;
        if (alc.DefaultValueSql != alc.OldColumn.DefaultValueSql)
            defaultValue = alc.DefaultValueSql;

        return new AlterColumnOperation
        {
            Table = alc.Table,
            Column = alc.Name,
            DataType = dataType,
            NotNull = notNull,
            Default = defaultValue,
            Up = up,
            Down = down
        };
    }
}
