using FluentAssertions;
using PgRoll.Core.Operations;
using PgRoll.Core.Schema;

namespace PgRoll.Core.Tests;

public class ValidationTests
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

    private static SchemaSnapshot BuildSchemaWithIndexes(string tableName, string[] columns, string[] indexes)
    {
        var table = new TableInfo("public", tableName,
            columns.Select((c, i) => new ColumnInfo(c, "text", true, null, false, i + 1)).ToList(),
            indexes, []);
        return new SchemaSnapshot([table], indexes);
    }

    // CreateTableOperation
    [Fact]
    public void CreateTable_TableDoesNotExist_IsValid()
    {
        var op = new CreateTableOperation { Table = "users", Columns = [new ColumnDefinition { Name = "id", Type = "serial" }] };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateTable_TableAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new CreateTableOperation { Table = "users", Columns = [new ColumnDefinition { Name = "id", Type = "serial" }] };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateTable_EmptyColumns_IsInvalid()
    {
        var op = new CreateTableOperation { Table = "users", Columns = [] };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    // DropTableOperation
    [Fact]
    public void DropTable_TableExists_IsValid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new DropTableOperation { Table = "users" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void DropTable_TableDoesNotExist_IsInvalid()
    {
        var op = new DropTableOperation { Table = "users" };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    // RenameTableOperation
    [Fact]
    public void RenameTable_SourceExistsTargetDoesNot_IsValid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new RenameTableOperation { From = "users", To = "accounts" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RenameTable_SourceDoesNotExist_IsInvalid()
    {
        var op = new RenameTableOperation { From = "users", To = "accounts" };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    [Fact]
    public void RenameTable_TargetAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id"]), ("accounts", ["id"]));
        var op = new RenameTableOperation { From = "users", To = "accounts" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // AddColumnOperation
    [Fact]
    public void AddColumn_TableExistsColumnDoesNot_IsValid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new AddColumnOperation { Table = "users", Column = new ColumnDefinition { Name = "email", Type = "text" } };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void AddColumn_TableDoesNotExist_IsInvalid()
    {
        var op = new AddColumnOperation { Table = "users", Column = new ColumnDefinition { Name = "email", Type = "text" } };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }

    [Fact]
    public void AddColumn_ColumnAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id", "email"]));
        var op = new AddColumnOperation { Table = "users", Column = new ColumnDefinition { Name = "email", Type = "text" } };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // DropColumnOperation
    [Fact]
    public void DropColumn_TableAndColumnExist_IsValid()
    {
        var schema = BuildSchema(("users", ["id", "email"]));
        var op = new DropColumnOperation { Table = "users", Column = "email" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void DropColumn_ColumnDoesNotExist_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id"]));
        var op = new DropColumnOperation { Table = "users", Column = "email" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // RenameColumnOperation
    [Fact]
    public void RenameColumn_SourceExistsTargetDoesNot_IsValid()
    {
        var schema = BuildSchema(("users", ["id", "email"]));
        var op = new RenameColumnOperation { Table = "users", From = "email", To = "mail" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void RenameColumn_TargetAlreadyExists_IsInvalid()
    {
        var schema = BuildSchema(("users", ["id", "email", "mail"]));
        var op = new RenameColumnOperation { Table = "users", From = "email", To = "mail" };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // CreateIndexOperation
    [Fact]
    public void CreateIndex_ValidTableAndColumns_IsValid()
    {
        var schema = BuildSchemaWithIndexes("users", ["id", "email"], []);
        var op = new CreateIndexOperation { Name = "idx_email", Table = "users", Columns = ["email"] };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void CreateIndex_IndexAlreadyExists_IsInvalid()
    {
        var schema = BuildSchemaWithIndexes("users", ["id", "email"], ["idx_email"]);
        var op = new CreateIndexOperation { Name = "idx_email", Table = "users", Columns = ["email"] };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    [Fact]
    public void CreateIndex_ColumnDoesNotExist_IsInvalid()
    {
        var schema = BuildSchemaWithIndexes("users", ["id"], []);
        var op = new CreateIndexOperation { Name = "idx_email", Table = "users", Columns = ["email"] };
        op.Validate(schema).IsValid.Should().BeFalse();
    }

    // DropIndexOperation
    [Fact]
    public void DropIndex_IndexExists_IsValid()
    {
        var schema = BuildSchemaWithIndexes("users", ["id"], ["idx_email"]);
        var op = new DropIndexOperation { Name = "idx_email" };
        op.Validate(schema).IsValid.Should().BeTrue();
    }

    [Fact]
    public void DropIndex_IndexDoesNotExist_IsInvalid()
    {
        var op = new DropIndexOperation { Name = "idx_email" };
        op.Validate(SchemaSnapshot.Empty).IsValid.Should().BeFalse();
    }
}
