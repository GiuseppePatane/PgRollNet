using FluentAssertions;
using PgRoll.Core.Operations;

namespace PgRoll.Core.Tests;

/// <summary>
/// Tests for IMigrationOperation.ValidateStructure() — offline field-level validation,
/// no database connection required.
/// </summary>
public class ValidateStructureTests
{
    // ── create_table ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateTable_Valid()
    {
        var op = new CreateTableOperation
        {
            Table = "users",
            Columns = [new ColumnDefinition { Name = "id", Type = "serial" }]
        };
        op.ValidateStructure().IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateTable_EmptyTable_Invalid()
    {
        var op = new CreateTableOperation { Table = "", Columns = [new ColumnDefinition { Name = "id", Type = "serial" }] };
        var r = op.ValidateStructure();
        r.IsValid.Should().BeFalse();
        r.Error.Should().Contain("Table name");
    }

    [Fact]
    public void CreateTable_NoColumns_Invalid()
    {
        var op = new CreateTableOperation { Table = "users", Columns = [] };
        op.ValidateStructure().IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateTable_ColumnMissingType_Invalid()
    {
        var op = new CreateTableOperation
        {
            Table = "users",
            Columns = [new ColumnDefinition { Name = "id", Type = "" }]
        };
        op.ValidateStructure().IsValid.Should().BeFalse();
    }

    // ── drop_table ────────────────────────────────────────────────────────────

    [Fact]
    public void DropTable_Valid() =>
        new DropTableOperation { Table = "users" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void DropTable_EmptyTable_Invalid() =>
        new DropTableOperation { Table = " " }.ValidateStructure().IsValid.Should().BeFalse();

    // ── rename_table ──────────────────────────────────────────────────────────

    [Fact]
    public void RenameTable_Valid() =>
        new RenameTableOperation { From = "old", To = "new" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void RenameTable_EmptyFrom_Invalid() =>
        new RenameTableOperation { From = "", To = "new" }.ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void RenameTable_EmptyTo_Invalid() =>
        new RenameTableOperation { From = "old", To = "" }.ValidateStructure().IsValid.Should().BeFalse();

    // ── add_column ────────────────────────────────────────────────────────────

    [Fact]
    public void AddColumn_Valid() =>
        new AddColumnOperation
        {
            Table = "users",
            Column = new ColumnDefinition { Name = "email", Type = "text" }
        }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void AddColumn_EmptyTable_Invalid() =>
        new AddColumnOperation
        {
            Table = "",
            Column = new ColumnDefinition { Name = "email", Type = "text" }
        }.ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void AddColumn_EmptyColumnType_Invalid() =>
        new AddColumnOperation
        {
            Table = "users",
            Column = new ColumnDefinition { Name = "email", Type = "" }
        }.ValidateStructure().IsValid.Should().BeFalse();

    // ── drop_column ───────────────────────────────────────────────────────────

    [Fact]
    public void DropColumn_Valid() =>
        new DropColumnOperation { Table = "users", Column = "email" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void DropColumn_EmptyColumn_Invalid() =>
        new DropColumnOperation { Table = "users", Column = "" }.ValidateStructure().IsValid.Should().BeFalse();

    // ── rename_column ─────────────────────────────────────────────────────────

    [Fact]
    public void RenameColumn_Valid() =>
        new RenameColumnOperation { Table = "t", From = "old", To = "new" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void RenameColumn_EmptyTo_Invalid() =>
        new RenameColumnOperation { Table = "t", From = "old", To = "" }.ValidateStructure().IsValid.Should().BeFalse();

    // ── alter_column ──────────────────────────────────────────────────────────

    [Fact]
    public void AlterColumn_Valid() =>
        new AlterColumnOperation { Table = "t", Column = "c", DataType = "bigint" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void AlterColumn_DataTypeWithNotNullNoUp_Invalid()
    {
        var op = new AlterColumnOperation { Table = "t", Column = "c", DataType = "bigint", NotNull = true };
        var r = op.ValidateStructure();
        r.IsValid.Should().BeFalse();
        r.Error.Should().Contain("up");
    }

    [Fact]
    public void AlterColumn_DataTypeWithNotNullAndUp_Valid() =>
        new AlterColumnOperation { Table = "t", Column = "c", DataType = "bigint", NotNull = true, Up = "c::bigint" }
            .ValidateStructure().IsValid.Should().BeTrue();

    // ── create_index ──────────────────────────────────────────────────────────

    [Fact]
    public void CreateIndex_Valid() =>
        new CreateIndexOperation { Name = "idx", Table = "t", Columns = ["col"] }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void CreateIndex_EmptyName_Invalid() =>
        new CreateIndexOperation { Name = "", Table = "t", Columns = ["col"] }.ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void CreateIndex_NoColumns_Invalid() =>
        new CreateIndexOperation { Name = "idx", Table = "t", Columns = [] }.ValidateStructure().IsValid.Should().BeFalse();

    // ── drop_index ────────────────────────────────────────────────────────────

    [Fact]
    public void DropIndex_Valid() =>
        new DropIndexOperation { Name = "idx" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void DropIndex_EmptyName_Invalid() =>
        new DropIndexOperation { Name = "" }.ValidateStructure().IsValid.Should().BeFalse();

    // ── create_constraint ─────────────────────────────────────────────────────

    [Fact]
    public void CreateConstraint_Check_Valid() =>
        new CreateConstraintOperation { Table = "t", Name = "ck", ConstraintType = "check", Check = "price > 0" }
            .ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void CreateConstraint_Check_MissingExpression_Invalid() =>
        new CreateConstraintOperation { Table = "t", Name = "ck", ConstraintType = "check" }
            .ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void CreateConstraint_Unique_Valid() =>
        new CreateConstraintOperation { Table = "t", Name = "uq", ConstraintType = "unique", Columns = ["email"] }
            .ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void CreateConstraint_Unique_MissingColumns_Invalid() =>
        new CreateConstraintOperation { Table = "t", Name = "uq", ConstraintType = "unique" }
            .ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void CreateConstraint_ForeignKey_Valid() =>
        new CreateConstraintOperation
        {
            Table = "orders", Name = "fk", ConstraintType = "foreign_key",
            Columns = ["user_id"], ReferencesTable = "users", ReferencesColumns = ["id"]
        }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void CreateConstraint_ForeignKey_MissingReferencesTable_Invalid() =>
        new CreateConstraintOperation { Table = "t", Name = "fk", ConstraintType = "foreign_key", Columns = ["id"] }
            .ValidateStructure().IsValid.Should().BeFalse();

    [Fact]
    public void CreateConstraint_UnknownType_Invalid() =>
        new CreateConstraintOperation { Table = "t", Name = "x", ConstraintType = "unknown" }
            .ValidateStructure().IsValid.Should().BeFalse();

    // ── drop_constraint ───────────────────────────────────────────────────────

    [Fact]
    public void DropConstraint_Valid() =>
        new DropConstraintOperation { Table = "t", Name = "ck" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void DropConstraint_EmptyName_Invalid() =>
        new DropConstraintOperation { Table = "t", Name = "" }.ValidateStructure().IsValid.Should().BeFalse();

    // ── rename_constraint ─────────────────────────────────────────────────────

    [Fact]
    public void RenameConstraint_Valid() =>
        new RenameConstraintOperation { Table = "t", From = "old_ck", To = "new_ck" }.ValidateStructure().IsValid.Should().BeTrue();

    [Fact]
    public void RenameConstraint_EmptyFrom_Invalid() =>
        new RenameConstraintOperation { Table = "t", From = "", To = "new_ck" }.ValidateStructure().IsValid.Should().BeFalse();
}
