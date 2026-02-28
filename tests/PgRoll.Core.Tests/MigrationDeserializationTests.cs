using FluentAssertions;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;

namespace PgRoll.Core.Tests;

public class MigrationDeserializationTests
{
    [Fact]
    public void Deserialize_CreateTable_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "create_users",
              "operations": [
                {
                  "type": "create_table",
                  "table": "users",
                  "columns": [
                    { "name": "id", "type": "serial", "primary_key": true, "nullable": false },
                    { "name": "email", "type": "text", "nullable": false }
                  ]
                }
              ]
            }
            """;

        var migration = Migration.Deserialize(json);

        migration.Name.Should().Be("create_users");
        migration.Operations.Should().HaveCount(1);
        var op = migration.Operations[0].Should().BeOfType<CreateTableOperation>().Subject;
        op.Table.Should().Be("users");
        op.Columns.Should().HaveCount(2);
        op.Columns[0].Name.Should().Be("id");
        op.Columns[0].PrimaryKey.Should().BeTrue();
        op.Columns[1].Name.Should().Be("email");
    }

    [Fact]
    public void Deserialize_DropTable_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "drop_users",
              "operations": [{ "type": "drop_table", "table": "users" }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<DropTableOperation>().Subject;
        op.Table.Should().Be("users");
    }

    [Fact]
    public void Deserialize_RenameTable_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "rename_users",
              "operations": [{ "type": "rename_table", "from": "users", "to": "accounts" }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<RenameTableOperation>().Subject;
        op.From.Should().Be("users");
        op.To.Should().Be("accounts");
    }

    [Fact]
    public void Deserialize_AddColumn_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "add_age",
              "operations": [{
                "type": "add_column",
                "table": "users",
                "column": { "name": "age", "type": "integer", "nullable": true, "default": "0" }
              }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        op.Table.Should().Be("users");
        op.Column.Name.Should().Be("age");
        op.Column.Type.Should().Be("integer");
        op.Column.Default.Should().Be("0");
    }

    [Fact]
    public void Deserialize_DropColumn_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "drop_age",
              "operations": [{ "type": "drop_column", "table": "users", "column": "age" }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<DropColumnOperation>().Subject;
        op.Table.Should().Be("users");
        op.Column.Should().Be("age");
    }

    [Fact]
    public void Deserialize_RenameColumn_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "rename_age",
              "operations": [{ "type": "rename_column", "table": "users", "from": "age", "to": "years" }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<RenameColumnOperation>().Subject;
        op.Table.Should().Be("users");
        op.From.Should().Be("age");
        op.To.Should().Be("years");
    }

    [Fact]
    public void Deserialize_CreateIndex_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "add_index",
              "operations": [{
                "type": "create_index",
                "name": "idx_users_email",
                "table": "users",
                "columns": ["email"],
                "unique": true
              }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<CreateIndexOperation>().Subject;
        op.Name.Should().Be("idx_users_email");
        op.Table.Should().Be("users");
        op.Columns.Should().ContainSingle().Which.Should().Be("email");
        op.Unique.Should().BeTrue();
        op.RequiresConcurrentConnection.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_DropIndex_ReturnsCorrectOperation()
    {
        const string json = """
            {
              "name": "drop_index",
              "operations": [{ "type": "drop_index", "name": "idx_users_email" }]
            }
            """;

        var migration = Migration.Deserialize(json);

        var op = migration.Operations[0].Should().BeOfType<DropIndexOperation>().Subject;
        op.Name.Should().Be("idx_users_email");
        op.RequiresConcurrentConnection.Should().BeTrue();
    }

    [Fact]
    public void Deserialize_UnknownType_ThrowsJsonException()
    {
        const string json = """
            {
              "name": "bad",
              "operations": [{ "type": "unknown_op" }]
            }
            """;

        var act = () => Migration.Deserialize(json);

        act.Should().Throw<System.Text.Json.JsonException>()
            .WithMessage("*Unknown operation type*");
    }

    [Fact]
    public void Serialize_RoundTrips_Correctly()
    {
        const string json = """{"name":"create_users","operations":[{"type":"create_table","table":"users","columns":[{"name":"id","type":"serial","nullable":false,"primary_key":true}]}]}""";

        var migration = Migration.Deserialize(json);
        var serialized = migration.Serialize();
        var reparsed = Migration.Deserialize(serialized);

        reparsed.Name.Should().Be(migration.Name);
        reparsed.Operations.Should().HaveCount(migration.Operations.Count);
    }
}
