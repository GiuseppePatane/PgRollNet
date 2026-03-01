using FluentAssertions;
using Microsoft.EntityFrameworkCore.Migrations;
using PgRoll.Core.Operations;
using PgRoll.EntityFrameworkCore;
using PgRollMigration = PgRoll.Core.Models.Migration;

// EF Core input type aliases (avoid name collision with PgRoll operations)
using EfAddCheckConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.AddCheckConstraintOperation;
using EfAddColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.AddColumnOperation;
using EfAddForeignKey = Microsoft.EntityFrameworkCore.Migrations.Operations.AddForeignKeyOperation;
using EfAddPrimaryKey = Microsoft.EntityFrameworkCore.Migrations.Operations.AddPrimaryKeyOperation;
using EfDropPrimaryKey = Microsoft.EntityFrameworkCore.Migrations.Operations.DropPrimaryKeyOperation;
using EfAddUniqueConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.AddUniqueConstraintOperation;
using EfAlterColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.AlterColumnOperation;
// ColumnOperation is abstract; use AddColumnOperation for OldColumn in tests
using EfCreateIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateIndexOperation;
using EfCreateTable = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateTableOperation;
using EfDropCheckConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.DropCheckConstraintOperation;
using EfDropColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.DropColumnOperation;
using EfDropForeignKey = Microsoft.EntityFrameworkCore.Migrations.Operations.DropForeignKeyOperation;
using EfDropIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.DropIndexOperation;
using EfDropTable = Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation;
using EfDropUniqueConstraint = Microsoft.EntityFrameworkCore.Migrations.Operations.DropUniqueConstraintOperation;
using EfDeleteData = Microsoft.EntityFrameworkCore.Migrations.Operations.DeleteDataOperation;
using EfEnsureSchema = Microsoft.EntityFrameworkCore.Migrations.Operations.EnsureSchemaOperation;
using EfDropSchema = Microsoft.EntityFrameworkCore.Migrations.Operations.DropSchemaOperation;
using EfCreateSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.CreateSequenceOperation;
using EfAlterSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.AlterSequenceOperation;
using EfDropSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.DropSequenceOperation;
using EfRestartSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.RestartSequenceOperation;
using EfRenameSequence = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameSequenceOperation;
using EfAlterDatabase = Microsoft.EntityFrameworkCore.Migrations.Operations.AlterDatabaseOperation;
using EfInsertData = Microsoft.EntityFrameworkCore.Migrations.Operations.InsertDataOperation;
using EfUpdateData = Microsoft.EntityFrameworkCore.Migrations.Operations.UpdateDataOperation;
using EfRenameColumn = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameColumnOperation;
using EfRenameIndex = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameIndexOperation;
using EfRenameTable = Microsoft.EntityFrameworkCore.Migrations.Operations.RenameTableOperation;
using EfSql = Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation;

namespace PgRoll.EntityFrameworkCore.Tests;

public class ConverterTests
{
    // ── CreateTable ──────────────────────────────────────────────────────────

    [Fact]
    public void Convert_CreateTable_Basic()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateTable
            {
                Name = "users",
                Columns =
                {
                    new EfAddColumn { Name = "id",    ClrType = typeof(int),    Table = "users", IsNullable = false },
                    new EfAddColumn { Name = "name",  ClrType = typeof(string), Table = "users", IsNullable = true  },
                    new EfAddColumn { Name = "email", ClrType = typeof(string), Table = "users", IsNullable = false }
                },
                PrimaryKey = new EfAddPrimaryKey { Columns = ["id"] }
            }
        ]);

        result.Migration.Name.Should().Be("m1");
        result.Migration.Operations.Should().HaveCount(1);
        result.Skipped.Should().BeEmpty();

        var ct = result.Migration.Operations[0].Should().BeOfType<CreateTableOperation>().Subject;
        ct.Table.Should().Be("users");
        ct.Columns.Should().HaveCount(3);

        var idCol = ct.Columns.Single(c => c.Name == "id");
        idCol.Type.Should().Be("integer");
        idCol.Nullable.Should().BeFalse();
        idCol.PrimaryKey.Should().BeTrue();

        ct.Columns.Single(c => c.Name == "name").PrimaryKey.Should().BeFalse();
        ct.Columns.Single(c => c.Name == "email").PrimaryKey.Should().BeFalse();
    }

    [Fact]
    public void Convert_CreateTable_WithUniqueConstraint()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateTable
            {
                Name = "products",
                Columns =
                {
                    new EfAddColumn { Name = "id",  ClrType = typeof(int),    Table = "products", IsNullable = false },
                    new EfAddColumn { Name = "sku", ClrType = typeof(string), Table = "products", IsNullable = false }
                },
                UniqueConstraints =
                {
                    new EfAddUniqueConstraint { Name = "uq_sku", Table = "products", Columns = ["sku"] }
                }
            }
        ]);

        result.Migration.Operations.Should().HaveCount(2);
        result.Migration.Operations[0].Should().BeOfType<CreateTableOperation>();

        var cc = result.Migration.Operations[1].Should().BeOfType<CreateConstraintOperation>().Subject;
        cc.ConstraintType.Should().Be("unique");
        cc.Name.Should().Be("uq_sku");
        cc.Columns.Should().BeEquivalentTo(["sku"]);
    }

    [Fact]
    public void Convert_CreateTable_WithForeignKey()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateTable
            {
                Name = "orders",
                Columns =
                {
                    new EfAddColumn { Name = "id",      ClrType = typeof(int), Table = "orders", IsNullable = false },
                    new EfAddColumn { Name = "user_id", ClrType = typeof(int), Table = "orders", IsNullable = false }
                },
                ForeignKeys =
                {
                    new EfAddForeignKey
                    {
                        Name = "fk_orders_users",
                        Table = "orders",
                        Columns = ["user_id"],
                        PrincipalTable = "users",
                        PrincipalColumns = ["id"]
                    }
                }
            }
        ]);

        result.Migration.Operations.Should().HaveCount(2);
        var cc = result.Migration.Operations[1].Should().BeOfType<CreateConstraintOperation>().Subject;
        cc.ConstraintType.Should().Be("foreign_key");
        cc.Name.Should().Be("fk_orders_users");
        cc.ReferencesTable.Should().Be("users");
        cc.ReferencesColumns.Should().BeEquivalentTo(["id"]);
    }

    // ── DropTable ─────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_DropTable()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropTable { Name = "legacy" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var dt = result.Migration.Operations[0].Should().BeOfType<DropTableOperation>().Subject;
        dt.Table.Should().Be("legacy");
    }

    // ── RenameTable ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_RenameTable()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRenameTable { Name = "old_name", NewName = "new_name" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var rt = result.Migration.Operations[0].Should().BeOfType<RenameTableOperation>().Subject;
        rt.From.Should().Be("old_name");
        rt.To.Should().Be("new_name");
    }

    // ── AddColumn ─────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_AddColumn_String()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddColumn
            {
                Table = "users", Name = "bio",
                ClrType = typeof(string), IsNullable = true
            }
        ]);

        var ac = result.Migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        ac.Table.Should().Be("users");
        ac.Column.Name.Should().Be("bio");
        ac.Column.Type.Should().Be("text");
        ac.Column.Nullable.Should().BeTrue();
        ac.Up.Should().BeNull();
    }

    [Fact]
    public void Convert_AddColumn_NotNullInt()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddColumn
            {
                Table = "orders", Name = "quantity",
                ClrType = typeof(int), IsNullable = false
            }
        ]);

        var ac = result.Migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        ac.Column.Type.Should().Be("integer");
        ac.Column.Nullable.Should().BeFalse();
    }

    [Fact]
    public void Convert_AddColumn_WithDefault()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddColumn
            {
                Table = "settings", Name = "active",
                ClrType = typeof(bool), IsNullable = false,
                DefaultValueSql = "true"
            }
        ]);

        var ac = result.Migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        ac.Column.Default.Should().Be("true");
    }

    // ── DropColumn ────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_DropColumn()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropColumn { Table = "users", Name = "old_field" }
        ]);

        var dc = result.Migration.Operations[0].Should().BeOfType<DropColumnOperation>().Subject;
        dc.Table.Should().Be("users");
        dc.Column.Should().Be("old_field");
    }

    // ── RenameColumn ──────────────────────────────────────────────────────────

    [Fact]
    public void Convert_RenameColumn()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRenameColumn { Table = "users", Name = "fname", NewName = "first_name" }
        ]);

        var rc = result.Migration.Operations[0].Should().BeOfType<RenameColumnOperation>().Subject;
        rc.Table.Should().Be("users");
        rc.From.Should().Be("fname");
        rc.To.Should().Be("first_name");
    }

    // ── AlterColumn ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_AlterColumn_DataType()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAlterColumn
            {
                Table = "products", Name = "price",
                ClrType = typeof(decimal), ColumnType = "numeric(10,2)",
                IsNullable = true,
                OldColumn = new EfAddColumn { Name = "price", ClrType = typeof(float), ColumnType = "real", Table = "products", IsNullable = true }
            }
        ]);

        var alc = result.Migration.Operations[0].Should().BeOfType<AlterColumnOperation>().Subject;
        alc.Table.Should().Be("products");
        alc.Column.Should().Be("price");
        alc.DataType.Should().Be("numeric(10,2)");
        alc.NotNull.Should().BeNull();
    }

    [Fact]
    public void Convert_AlterColumn_NotNull()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAlterColumn
            {
                Table = "users", Name = "email",
                ClrType = typeof(string), ColumnType = "text",
                IsNullable = false,
                OldColumn = new EfAddColumn { Name = "email", ClrType = typeof(string), ColumnType = "text", Table = "users", IsNullable = true }
            }
        ]);

        var alc = result.Migration.Operations[0].Should().BeOfType<AlterColumnOperation>().Subject;
        alc.NotNull.Should().BeTrue();
        alc.DataType.Should().BeNull();
    }

    [Fact]
    public void Convert_AlterColumn_Default()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAlterColumn
            {
                Table = "items", Name = "status",
                ClrType = typeof(string), ColumnType = "text",
                IsNullable = true, DefaultValueSql = "'active'",
                OldColumn = new EfAddColumn { Name = "status", ClrType = typeof(string), ColumnType = "text", Table = "items", IsNullable = true, DefaultValueSql = null }
            }
        ]);

        var alc = result.Migration.Operations[0].Should().BeOfType<AlterColumnOperation>().Subject;
        alc.Default.Should().Be("'active'");
    }

    // ── CreateIndex ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_CreateIndex()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateIndex
            {
                Name = "ix_users_email", Table = "users",
                Columns = ["email"], IsUnique = false
            }
        ]);

        var ci = result.Migration.Operations[0].Should().BeOfType<CreateIndexOperation>().Subject;
        ci.Name.Should().Be("ix_users_email");
        ci.Table.Should().Be("users");
        ci.Columns.Should().BeEquivalentTo(["email"]);
        ci.Unique.Should().BeFalse();
    }

    [Fact]
    public void Convert_CreateUniqueIndex()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateIndex
            {
                Name = "ix_users_email_unique", Table = "users",
                Columns = ["email"], IsUnique = true
            }
        ]);

        var ci = result.Migration.Operations[0].Should().BeOfType<CreateIndexOperation>().Subject;
        ci.Unique.Should().BeTrue();
    }

    // ── DropIndex ─────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_DropIndex()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropIndex { Name = "ix_old" }
        ]);

        var di = result.Migration.Operations[0].Should().BeOfType<DropIndexOperation>().Subject;
        di.Name.Should().Be("ix_old");
    }

    // ── Constraints ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_AddCheckConstraint()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddCheckConstraint
            {
                Table = "orders", Name = "ck_amount_positive",
                Sql = "amount > 0"
            }
        ]);

        var cc = result.Migration.Operations[0].Should().BeOfType<CreateConstraintOperation>().Subject;
        cc.ConstraintType.Should().Be("check");
        cc.Name.Should().Be("ck_amount_positive");
        cc.Check.Should().Be("amount > 0");
        cc.Table.Should().Be("orders");
    }

    [Fact]
    public void Convert_DropCheckConstraint()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropCheckConstraint { Table = "orders", Name = "ck_amount_positive" }
        ]);

        var dc = result.Migration.Operations[0].Should().BeOfType<DropConstraintOperation>().Subject;
        dc.Table.Should().Be("orders");
        dc.Name.Should().Be("ck_amount_positive");
    }

    [Fact]
    public void Convert_AddUniqueConstraint()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddUniqueConstraint
            {
                Table = "users", Name = "uq_email",
                Columns = ["email"]
            }
        ]);

        var cc = result.Migration.Operations[0].Should().BeOfType<CreateConstraintOperation>().Subject;
        cc.ConstraintType.Should().Be("unique");
        cc.Name.Should().Be("uq_email");
        cc.Columns.Should().BeEquivalentTo(["email"]);
    }

    [Fact]
    public void Convert_DropUniqueConstraint()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropUniqueConstraint { Table = "users", Name = "uq_email" }
        ]);

        var dc = result.Migration.Operations[0].Should().BeOfType<DropConstraintOperation>().Subject;
        dc.Table.Should().Be("users");
        dc.Name.Should().Be("uq_email");
    }

    [Fact]
    public void Convert_AddForeignKey()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddForeignKey
            {
                Table = "orders", Name = "fk_orders_users",
                Columns = ["user_id"],
                PrincipalTable = "users",
                PrincipalColumns = ["id"]
            }
        ]);

        var cc = result.Migration.Operations[0].Should().BeOfType<CreateConstraintOperation>().Subject;
        cc.ConstraintType.Should().Be("foreign_key");
        cc.Name.Should().Be("fk_orders_users");
        cc.Columns.Should().BeEquivalentTo(["user_id"]);
        cc.ReferencesTable.Should().Be("users");
        cc.ReferencesColumns.Should().BeEquivalentTo(["id"]);
    }

    [Fact]
    public void Convert_DropForeignKey()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropForeignKey { Table = "orders", Name = "fk_orders_users" }
        ]);

        var dc = result.Migration.Operations[0].Should().BeOfType<DropConstraintOperation>().Subject;
        dc.Table.Should().Be("orders");
        dc.Name.Should().Be("fk_orders_users");
    }

    // ── Schema ops ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_EnsureSchema_MapsToCreateSchemaOperation()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfEnsureSchema { Name = "audit" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var cs = result.Migration.Operations[0].Should().BeOfType<CreateSchemaOperation>().Subject;
        cs.Schema.Should().Be("audit");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_DropSchema_MapsToDropSchemaOperation()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropSchema { Name = "audit" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var ds = result.Migration.Operations[0].Should().BeOfType<DropSchemaOperation>().Subject;
        ds.Schema.Should().Be("audit");
        result.Skipped.Should().BeEmpty();
    }

    // ── Sequences ─────────────────────────────────────────────────────────────

    [Fact]
    public void Convert_CreateSequence_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateSequence
            {
                Name = "order_seq", Schema = null,
                ClrType = typeof(long),
                StartValue = 1000, IncrementBy = 1,
                MinValue = 1, MaxValue = null, IsCyclic = false
            }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("CREATE SEQUENCE");
        raw.Sql.Should().Contain("\"order_seq\"");
        raw.Sql.Should().Contain("START WITH 1000");
        raw.Sql.Should().Contain("INCREMENT BY 1");
        raw.Sql.Should().Contain("MINVALUE 1");
        raw.Sql.Should().Contain("NO MAXVALUE");
        raw.Sql.Should().Contain("NO CYCLE");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_CreateSequence_WithSchema_QualifiesName()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateSequence
            {
                Name = "seq", Schema = "billing",
                ClrType = typeof(int),
                StartValue = 1, IncrementBy = 1,
                IsCyclic = false
            }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("\"billing\".\"seq\"");
        raw.Sql.Should().Contain("AS integer");
    }

    [Fact]
    public void Convert_AlterSequence_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAlterSequence
            {
                Name = "order_seq", Schema = null,
                IncrementBy = 5, MinValue = 1, MaxValue = 9999, IsCyclic = true
            }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("ALTER SEQUENCE");
        raw.Sql.Should().Contain("INCREMENT BY 5");
        raw.Sql.Should().Contain("MAXVALUE 9999");
        raw.Sql.Should().Contain("CYCLE");
    }

    [Fact]
    public void Convert_DropSequence_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropSequence { Name = "old_seq", Schema = null }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("DROP SEQUENCE IF EXISTS");
        raw.Sql.Should().Contain("\"old_seq\"");
    }

    [Fact]
    public void Convert_RestartSequence_WithValue_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRestartSequence { Name = "order_seq", Schema = null, StartValue = 500 }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("RESTART WITH 500");
    }

    [Fact]
    public void Convert_RestartSequence_NoValue_MapsToRestart()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRestartSequence { Name = "order_seq", Schema = null }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("RESTART;");
        raw.Sql.Should().NotContain("RESTART WITH");
    }

    [Fact]
    public void Convert_RenameSequence_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRenameSequence { Name = "old_seq", Schema = null, NewName = "new_seq", NewSchema = null }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("RENAME TO");
        raw.Sql.Should().Contain("new_seq");
    }

    // ── Primary key ───────────────────────────────────────────────────────────

    [Fact]
    public void Convert_AddPrimaryKey_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddPrimaryKey
            {
                Table = "orders", Schema = null,
                Name = "pk_orders", Columns = ["id"]
            }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("ALTER TABLE \"orders\" ADD CONSTRAINT \"pk_orders\" PRIMARY KEY (\"id\");");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_AddPrimaryKey_Composite_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddPrimaryKey
            {
                Table = "order_items", Schema = null,
                Name = "pk_order_items", Columns = ["order_id", "item_id"]
            }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("\"order_id\", \"item_id\"");
    }

    [Fact]
    public void Convert_DropPrimaryKey_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDropPrimaryKey { Table = "orders", Schema = null, Name = "pk_orders" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("ALTER TABLE \"orders\" DROP CONSTRAINT \"pk_orders\";");
        result.Skipped.Should().BeEmpty();
    }

    // ── SqlOperation → raw_sql ────────────────────────────────────────────────

    [Fact]
    public void Convert_SqlOperation_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfSql { Sql = "SELECT 1" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Be("SELECT 1");
        raw.RollbackSql.Should().BeNull();
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_SqlOperation_CreateFunction_MapsToRawSql()
    {
        const string createFn = """
            CREATE OR REPLACE FUNCTION update_updated_at()
            RETURNS TRIGGER LANGUAGE plpgsql AS $$
            BEGIN
              NEW.updated_at = now();
              RETURN NEW;
            END;
            $$;
            """;

        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfSql { Sql = createFn }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("CREATE OR REPLACE FUNCTION");
        raw.Sql.Should().Contain("RETURNS TRIGGER");
    }

    [Fact]
    public void Convert_SqlOperation_MixedWithDdl_CorrectOrder()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateTable
            {
                Name = "logs",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), Table = "logs", IsNullable = false } }
            },
            new EfSql { Sql = "CREATE INDEX CONCURRENTLY ix_logs_id ON logs(id)" }
        ]);

        result.Migration.Operations.Should().HaveCount(2);
        result.Migration.Operations[0].Should().BeOfType<CreateTableOperation>();
        result.Migration.Operations[1].Should().BeOfType<RawSqlOperation>()
            .Which.Sql.Should().Contain("CREATE INDEX CONCURRENTLY");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_SqlOperation_ViaMigrationBuilder()
    {
        var result = EfCoreMigrationConverter.Convert("m1", builder =>
        {
            builder.Sql("""
                CREATE OR REPLACE FUNCTION notify_insert()
                RETURNS TRIGGER LANGUAGE plpgsql AS $$
                BEGIN
                  PERFORM pg_notify('insert', row_to_json(NEW)::text);
                  RETURN NEW;
                END;
                $$
                """);
        });

        result.Migration.Operations.Should().HaveCount(1);
        result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>()
            .Which.Sql.Should().Contain("pg_notify");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_SqlOperation_SerializesToRawSqlJson()
    {
        var result = EfCoreMigrationConverter.Convert("create_fn",
        [
            new EfSql { Sql = "CREATE FUNCTION f() RETURNS void LANGUAGE sql AS $$ SELECT 1 $$" }
        ]);

        var json = result.Migration.Serialize();
        json.Should().Contain("\"type\":\"raw_sql\"");
        json.Should().Contain("\"sql\":");
        json.Should().Contain("CREATE FUNCTION");
    }

    // ── Skip behaviour ────────────────────────────────────────────────────────

    [Fact]
    public void Convert_SkipsUnsupported()
    {
        // AlterDatabaseOperation (database-level settings) has no pgroll equivalent
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAlterDatabase(),
            new EfCreateTable
            {
                Name = "t",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), Table = "t", IsNullable = false } }
            }
        ]);

        result.Skipped.Should().ContainSingle().Which.Should().Be("AlterDatabaseOperation");
        result.Migration.Operations.Should().HaveCount(1);
    }

    [Fact]
    public void Convert_RenameIndex_MapsToRawSql()
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfRenameIndex { Table = "users", Name = "ix_old", NewName = "ix_new" }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("ALTER INDEX");
        raw.Sql.Should().Contain("ix_old");
        raw.Sql.Should().Contain("ix_new");
        result.Skipped.Should().BeEmpty();
    }

    // ── Data seeding: InsertDataOperation ────────────────────────────────────

    [Fact]
    public void Convert_InsertData_SingleRow_GeneratesInsertSql()
    {
        var values = new object[1, 2] { { 1, "Alice" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "users", Schema = null, Columns = ["id", "name"], Values = values }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("INSERT INTO \"users\" (\"id\", \"name\") VALUES");
        raw.Sql.Should().Contain("1,");
        raw.Sql.Should().Contain("'Alice'");
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public void Convert_InsertData_MultipleRows_SingleStatement()
    {
        var values = new object[2, 2] { { 1, "Alice" }, { 2, "Bob" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "users", Schema = null, Columns = ["id", "name"], Values = values }
        ]);

        // All rows in a single raw_sql, not two separate operations
        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("'Alice'");
        raw.Sql.Should().Contain("'Bob'");
    }

    [Fact]
    public void Convert_InsertData_WithSchema_QualifiesTable()
    {
        var values = new object[1, 1] { { 42 } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "settings", Schema = "config", Columns = ["id"], Values = values }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("\"config\".\"settings\"");
    }

    [Fact]
    public void Convert_InsertData_NullValues_EmittedAsNull()
    {
        var values = new object[1, 2] { { 1, DBNull.Value } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "t", Schema = null, Columns = ["id", "note"], Values = values }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("NULL");
    }

    [Fact]
    public void Convert_InsertData_StringWithQuotes_EscapedCorrectly()
    {
        var values = new object[1, 1] { { "it's a test" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "t", Schema = null, Columns = ["note"], Values = values }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("'it''s a test'");
    }

    [Fact]
    public void Convert_InsertData_EmptyRows_ProducesNoOperation()
    {
        // EF Core can emit an InsertDataOperation with 0 rows during migrations — should be silently ignored
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfInsertData { Table = "t", Schema = null, Columns = [], Values = new object[0, 0] }
        ]);

        result.Migration.Operations.Should().BeEmpty();
        result.Skipped.Should().BeEmpty();
    }

    // ── Data seeding: UpdateDataOperation ────────────────────────────────────

    [Fact]
    public void Convert_UpdateData_GeneratesUpdateSql()
    {
        var keyValues = new object[1, 1] { { 1 } };
        var values = new object[1, 1] { { "Alice Updated" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfUpdateData
            {
                Table = "users", Schema = null,
                KeyColumns = ["id"], KeyValues = keyValues,
                Columns = ["name"], Values = values
            }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("UPDATE \"users\" SET \"name\" = 'Alice Updated' WHERE \"id\" = 1;");
    }

    [Fact]
    public void Convert_UpdateData_MultipleRows_MultipleStatements()
    {
        var keyValues = new object[2, 1] { { 1 }, { 2 } };
        var values = new object[2, 1] { { "A" }, { "B" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfUpdateData
            {
                Table = "t", Schema = null,
                KeyColumns = ["id"], KeyValues = keyValues,
                Columns = ["name"], Values = values
            }
        ]);

        // One raw_sql block with two UPDATE statements
        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("'A'");
        raw.Sql.Should().Contain("'B'");
        raw.Sql.Should().Contain("WHERE \"id\" = 1");
        raw.Sql.Should().Contain("WHERE \"id\" = 2");
    }

    // ── Data seeding: DeleteDataOperation ────────────────────────────────────

    [Fact]
    public void Convert_DeleteData_GeneratesDeleteSql()
    {
        var keyValues = new object[1, 1] { { 99 } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDeleteData { Table = "users", Schema = null, KeyColumns = ["id"], KeyValues = keyValues }
        ]);

        result.Migration.Operations.Should().HaveCount(1);
        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("DELETE FROM \"users\" WHERE \"id\" = 99;");
    }

    [Fact]
    public void Convert_DeleteData_CompositeKey_GeneratesAndClause()
    {
        var keyValues = new object[1, 2] { { "US", "CA" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfDeleteData { Table = "regions", Schema = null, KeyColumns = ["country", "state"], KeyValues = keyValues }
        ]);

        var raw = result.Migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        raw.Sql.Should().Contain("WHERE \"country\" = 'US' AND \"state\" = 'CA'");
    }

    // ── Data seeding: mixed with DDL ──────────────────────────────────────────

    [Fact]
    public void Convert_DataSeedingWithDdl_PreservesOperationOrder()
    {
        var values = new object[1, 2] { { 1, "admin" } };
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfCreateTable
            {
                Name = "roles",
                Columns = { new EfAddColumn { Name = "id", ClrType = typeof(int), Table = "roles", IsNullable = false } }
            },
            new EfInsertData { Table = "roles", Schema = null, Columns = ["id", "name"], Values = values }
        ]);

        result.Migration.Operations.Should().HaveCount(2);
        result.Migration.Operations[0].Should().BeOfType<CreateTableOperation>();
        result.Migration.Operations[1].Should().BeOfType<RawSqlOperation>()
            .Which.Sql.Should().Contain("INSERT INTO");
    }

    [Fact]
    public void Convert_DataSeedingJson_SerializesCorrectly()
    {
        var values = new object[1, 2] { { 1, "admin" } };
        var result = EfCoreMigrationConverter.Convert("seed_roles",
        [
            new EfInsertData { Table = "roles", Schema = null, Columns = ["id", "name"], Values = values }
        ]);

        var json = result.Migration.Serialize();
        json.Should().Contain("\"type\":\"raw_sql\"");
        json.Should().Contain("\"sql\":");
        json.Should().Contain("INSERT INTO");
    }

    // ── DataSeedingSqlGenerator unit tests ────────────────────────────────────

    [Fact]
    public void Generator_FormatValue_Bool_EmitsLiteral()
    {
        PgRoll.Core.Helpers.DataSeedingSqlGenerator
            .FormatValue(true).Should().Be("true");
        PgRoll.Core.Helpers.DataSeedingSqlGenerator
            .FormatValue(false).Should().Be("false");
    }

    [Fact]
    public void Generator_FormatValue_Guid_Quoted()
    {
        var g = Guid.Parse("12345678-1234-1234-1234-123456789012");
        PgRoll.Core.Helpers.DataSeedingSqlGenerator
            .FormatValue(g).Should().Be($"'{g}'");
    }

    [Fact]
    public void Generator_FormatValue_Null_EmitsNull()
    {
        PgRoll.Core.Helpers.DataSeedingSqlGenerator
            .FormatValue(null).Should().Be("NULL");
        PgRoll.Core.Helpers.DataSeedingSqlGenerator
            .FormatValue(DBNull.Value).Should().Be("NULL");
    }

    // ── TypeMapper ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(typeof(string), null, "text")]
    [InlineData(typeof(int), null, "integer")]
    [InlineData(typeof(long), null, "bigint")]
    [InlineData(typeof(bool), null, "boolean")]
    [InlineData(typeof(Guid), null, "uuid")]
    [InlineData(typeof(decimal), null, "numeric")]
    [InlineData(typeof(double), null, "double precision")]
    [InlineData(typeof(float), null, "real")]
    [InlineData(typeof(byte[]), null, "bytea")]
    [InlineData(typeof(DateTime), null, "timestamp with time zone")]
    [InlineData(typeof(DateTimeOffset), null, "timestamp with time zone")]
    [InlineData(typeof(int), "integer", "integer")]
    [InlineData(typeof(string), "varchar(100)", "varchar(100)")]
    public void Convert_TypeMapper_ClrTypes(Type clrType, string? columnType, string expected)
    {
        var result = EfCoreMigrationConverter.Convert("m1",
        [
            new EfAddColumn
            {
                Table = "t", Name = "col",
                ClrType = clrType, ColumnType = columnType, IsNullable = true
            }
        ]);

        var ac = result.Migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        ac.Column.Type.Should().Be(expected);
    }

    // ── MigrationBuilder extension ────────────────────────────────────────────

    [Fact]
    public void MigrationBuilderExtension_Works()
    {
        var builder = new MigrationBuilder(activeProvider: null);
        builder.CreateTable("widgets", t => new
        {
            id   = t.Column<int>(nullable: false),
            name = t.Column<string>(nullable: true)
        });

        var migration = builder.ToPgRollMigration("create_widgets");

        migration.Name.Should().Be("create_widgets");
        migration.Operations.Should().HaveCount(1);
        migration.Operations[0].Should().BeOfType<CreateTableOperation>()
            .Which.Table.Should().Be("widgets");
    }

    // ── JSON serialization round-trip ─────────────────────────────────────────

    [Fact]
    public void ConversionResult_SerializesToJson()
    {
        var result = EfCoreMigrationConverter.Convert("create_users",
        [
            new EfCreateTable
            {
                Name = "users",
                Columns =
                {
                    new EfAddColumn { Name = "id",   ClrType = typeof(int),    Table = "users", IsNullable = false },
                    new EfAddColumn { Name = "name", ClrType = typeof(string), Table = "users", IsNullable = true  }
                },
                PrimaryKey = new EfAddPrimaryKey { Columns = ["id"] }
            }
        ]);

        var json = result.Migration.Serialize();
        json.Should().Contain("\"name\":\"create_users\"");
        json.Should().Contain("\"type\":\"create_table\"");
        json.Should().Contain("\"table\":\"users\"");

        // Verify round-trip
        var deserialized = PgRollMigration.Deserialize(json);
        deserialized.Name.Should().Be("create_users");
        deserialized.Operations.Should().HaveCount(1);
        deserialized.Operations[0].Should().BeOfType<CreateTableOperation>();
    }
}
