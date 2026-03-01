using FluentAssertions;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;

namespace PgRoll.Core.Tests;

public class MigrationYamlTests
{
    [Fact]
    public void DeserializeYaml_CreateTable_ReturnsCorrectMigration()
    {
        const string yaml = """
            name: create_users
            operations:
              - type: create_table
                table: users
                columns:
                  - name: id
                    type: serial
                    primary_key: true
                    nullable: false
                  - name: email
                    type: text
                    nullable: false
            """;

        var migration = Migration.DeserializeYaml(yaml);

        migration.Name.Should().Be("create_users");
        migration.Operations.Should().HaveCount(1);
        var op = migration.Operations[0].Should().BeOfType<CreateTableOperation>().Subject;
        op.Table.Should().Be("users");
        op.Columns.Should().HaveCount(2);
        op.Columns[0].Name.Should().Be("id");
        op.Columns[0].PrimaryKey.Should().BeTrue();
        op.Columns[0].Nullable.Should().BeFalse();
        op.Columns[1].Name.Should().Be("email");
        op.Columns[1].Type.Should().Be("text");
    }

    [Fact]
    public void DeserializeYaml_AddColumn_Simple()
    {
        const string yaml = """
            name: add_age_column
            operations:
              - type: add_column
                table: users
                column:
                  name: age
                  type: integer
                  nullable: true
            """;

        var migration = Migration.DeserializeYaml(yaml);

        migration.Name.Should().Be("add_age_column");
        var op = migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        op.Table.Should().Be("users");
        op.Column.Name.Should().Be("age");
        op.Column.Type.Should().Be("integer");
        op.Column.Nullable.Should().BeTrue();
        op.Up.Should().BeNull();
    }

    [Fact]
    public void DeserializeYaml_AddColumn_WithUpExpression()
    {
        const string yaml = """
            name: add_full_name
            operations:
              - type: add_column
                table: users
                up: "UPPER(first_name || ' ' || last_name)"
                column:
                  name: full_name
                  type: text
                  nullable: false
            """;

        var migration = Migration.DeserializeYaml(yaml);

        var op = migration.Operations[0].Should().BeOfType<AddColumnOperation>().Subject;
        op.Column.Name.Should().Be("full_name");
        op.Up.Should().Be("UPPER(first_name || ' ' || last_name)");
        op.Column.Nullable.Should().BeFalse();
    }

    [Fact]
    public void DeserializeYaml_MultipleOperations()
    {
        const string yaml = """
            name: multi_op
            operations:
              - type: create_table
                table: orders
                columns:
                  - name: id
                    type: serial
                    primary_key: true
                    nullable: false
              - type: create_index
                name: idx_orders_id
                table: orders
                columns:
                  - id
                unique: false
            """;

        var migration = Migration.DeserializeYaml(yaml);

        migration.Operations.Should().HaveCount(2);
        migration.Operations[0].Should().BeOfType<CreateTableOperation>();
        migration.Operations[1].Should().BeOfType<CreateIndexOperation>();
    }

    [Fact]
    public void DeserializeYaml_RawSql()
    {
        const string yaml = """
            name: add_extension
            operations:
              - type: raw_sql
                sql: "CREATE EXTENSION IF NOT EXISTS pgcrypto"
                rollback_sql: "DROP EXTENSION IF EXISTS pgcrypto"
            """;

        var migration = Migration.DeserializeYaml(yaml);

        var op = migration.Operations[0].Should().BeOfType<RawSqlOperation>().Subject;
        op.Sql.Should().Be("CREATE EXTENSION IF NOT EXISTS pgcrypto");
        op.RollbackSql.Should().Be("DROP EXTENSION IF EXISTS pgcrypto");
    }

    [Fact]
    public void DeserializeYaml_RenameTable()
    {
        const string yaml = """
            name: rename_users
            operations:
              - type: rename_table
                from: users
                to: accounts
            """;

        var migration = Migration.DeserializeYaml(yaml);

        var op = migration.Operations[0].Should().BeOfType<RenameTableOperation>().Subject;
        op.From.Should().Be("users");
        op.To.Should().Be("accounts");
    }

    [Fact]
    public void DeserializeYaml_AlterColumn()
    {
        const string yaml = """
            name: alter_username
            operations:
              - type: alter_column
                table: users
                column: name
                up: "UPPER(name)"
                down: "LOWER(full_name)"
                not_null: true
            """;

        var migration = Migration.DeserializeYaml(yaml);

        var op = migration.Operations[0].Should().BeOfType<AlterColumnOperation>().Subject;
        op.Table.Should().Be("users");
        op.Column.Should().Be("name");
        op.Up.Should().Be("UPPER(name)");
        op.Down.Should().Be("LOWER(full_name)");
        op.NotNull.Should().BeTrue();
    }

    [Fact]
    public void DeserializeYaml_ProducesEquivalentResultToJson()
    {
        const string json = """
            {
              "name": "create_products",
              "operations": [
                {
                  "type": "create_table",
                  "table": "products",
                  "columns": [
                    { "name": "id", "type": "serial", "primary_key": true, "nullable": false },
                    { "name": "price", "type": "numeric", "nullable": false }
                  ]
                }
              ]
            }
            """;

        const string yaml = """
            name: create_products
            operations:
              - type: create_table
                table: products
                columns:
                  - name: id
                    type: serial
                    primary_key: true
                    nullable: false
                  - name: price
                    type: numeric
                    nullable: false
            """;

        var fromJson = Migration.Deserialize(json);
        var fromYaml = Migration.DeserializeYaml(yaml);

        fromYaml.Name.Should().Be(fromJson.Name);
        fromYaml.Operations.Should().HaveCount(fromJson.Operations.Count);

        var jsonOp = (CreateTableOperation)fromJson.Operations[0];
        var yamlOp = (CreateTableOperation)fromYaml.Operations[0];
        yamlOp.Table.Should().Be(jsonOp.Table);
        yamlOp.Columns.Should().HaveCount(jsonOp.Columns.Count);
        for (var i = 0; i < jsonOp.Columns.Count; i++)
        {
            yamlOp.Columns[i].Name.Should().Be(jsonOp.Columns[i].Name);
            yamlOp.Columns[i].Type.Should().Be(jsonOp.Columns[i].Type);
            yamlOp.Columns[i].PrimaryKey.Should().Be(jsonOp.Columns[i].PrimaryKey);
            yamlOp.Columns[i].Nullable.Should().Be(jsonOp.Columns[i].Nullable);
        }
    }

    [Fact]
    public async Task LoadAsync_DetectsYamlByExtension()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.yaml");
        try
        {
            const string yaml = """
                name: temp_migration
                operations:
                  - type: raw_sql
                    sql: "SELECT 1"
                """;

            await File.WriteAllTextAsync(tmp, yaml);

            var migration = await Migration.LoadAsync(tmp);
            migration.Name.Should().Be("temp_migration");
            migration.Operations[0].Should().BeOfType<RawSqlOperation>();
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
