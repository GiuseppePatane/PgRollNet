using FluentAssertions;
using PgRoll.Core.Operations;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Tests;

public class AlterColumnValidationTests
{
    private static SchemaSnapshot BuildSchema(params (string name, string[] columns)[] tables)
    {
        var tableInfos = tables.Select(t => new TableInfo(
            Schema: "public",
            Name: t.name,
            Columns: t.columns.Select((c, i) => new ColumnInfo(c, "text", true, null, false, i + 1)).ToList(),
            Indexes: [],
            Constraints: []
        ));
        return new SchemaSnapshot(tableInfos);
    }

    [Fact]
    public void AlterColumn_TableAndColumnExist_IsValid()
    {
        var schema = BuildSchema(("users", ["id", "name"]));
        var op = new AlterColumnOperation { Table = "users", Column = "name", DataType = "varchar(100)" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AlterColumn_TableDoesNotExist_IsInvalid()
    {
        var op = new AlterColumnOperation { Table = "missing", Column = "name" };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    [Fact]
    public void AlterColumn_ColumnDoesNotExist_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new AlterColumnOperation { Table = "users", Column = "nonexistent" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void AlterColumn_DataTypeChangeWithNotNull_RequiresUp()
    {
        var schema = BuildSchema(("users", ["id", "name"]));
        var op = new AlterColumnOperation { Table = "users", Column = "name", DataType = "int", NotNull = true };
        var result = op.Validate(schema);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("up");
    }

    [Fact]
    public void AlterColumn_DataTypeChangeWithNotNullAndUp_IsValid()
    {
        var schema = BuildSchema(("users", ["id", "name"]));
        var op = new AlterColumnOperation { Table = "users", Column = "name", DataType = "int", NotNull = true, Up = "0" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AlterColumn_NewNameAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id", "name", "email"]));
        var op = new AlterColumnOperation { Table = "users", Column = "name", Name = "email" };
        var result = op.Validate(schema);
        result.IsValid.Should().BeFalse();
        result.Error.Should().Contain("email");
    }

    [Fact]
    public void AlterColumn_RenameToNewName_IsValid()
    {
        var schema = BuildSchema(("users", ["id", "name"]));
        var op = new AlterColumnOperation { Table = "users", Column = "name", Name = "full_name" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AlterColumn_RequiresConcurrentConnection_AlwaysTrue()
    {
        var op = new AlterColumnOperation { Table = "t", Column = "c" };
        op.RequiresConcurrentConnection.Should().BeTrue();
    }
}
