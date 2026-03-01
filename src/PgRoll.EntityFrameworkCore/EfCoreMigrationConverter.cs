using Microsoft.EntityFrameworkCore.Migrations;
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
using EfRenameTable = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameTableOperation;
using EfSql = Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation;

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
                    skipped.Add(ri.GetType().Name);
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

        var columns = ct.Columns.Select(c => new ColumnDefinition
        {
            Name = c.Name,
            Type = EfCoreTypeMapper.MapColumnType(c.ColumnType, c.ClrType),
            Nullable = c.IsNullable,
            Default = c.DefaultValueSql,
            PrimaryKey = pkColumns.Contains(c.Name)
        }).ToList();

        result.Add(new CreateTableOperation
        {
            Table = ct.Name,
            Columns = columns
        });

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
        if (alc.ColumnType is not null && alc.OldColumn.ColumnType is not null
            && alc.ColumnType != alc.OldColumn.ColumnType)
        {
            dataType = alc.ColumnType;
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
            Up = null
        };
    }
}
