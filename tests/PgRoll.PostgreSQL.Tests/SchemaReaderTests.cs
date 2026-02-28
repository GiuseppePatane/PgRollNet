using FluentAssertions;
using Npgsql;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

[Collection("Postgres")]
public class SchemaReaderTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_schema_{Guid.NewGuid():N}";
    private PgSchemaReader _reader = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _reader = new PgSchemaReader(_ds);

        // Create test tables
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE users (
                id SERIAL PRIMARY KEY,
                email TEXT NOT NULL,
                age INTEGER
            );
            CREATE TABLE posts (
                id SERIAL PRIMARY KEY,
                title TEXT NOT NULL,
                user_id INTEGER REFERENCES users(id)
            );
            CREATE INDEX idx_users_email ON users(email);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    [Fact]
    public async Task ReadSchema_ReturnsAllTables()
    {
        var snapshot = await _reader.ReadSchemaAsync("public");

        snapshot.Tables.Should().ContainKey("users");
        snapshot.Tables.Should().ContainKey("posts");
    }

    [Fact]
    public async Task ReadSchema_ReturnsCorrectColumns()
    {
        var snapshot = await _reader.ReadSchemaAsync("public");

        var usersTable = snapshot.GetTable("users")!;
        usersTable.Columns.Should().HaveCount(3);
        usersTable.HasColumn("id").Should().BeTrue();
        usersTable.HasColumn("email").Should().BeTrue();
        usersTable.HasColumn("age").Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_IdentifiesPrimaryKey()
    {
        var snapshot = await _reader.ReadSchemaAsync("public");

        var idColumn = snapshot.GetTable("users")!.Columns.First(c => c.Name == "id");
        idColumn.IsPrimaryKey.Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_IdentifiesNullability()
    {
        var snapshot = await _reader.ReadSchemaAsync("public");

        var emailCol = snapshot.GetTable("users")!.Columns.First(c => c.Name == "email");
        emailCol.IsNullable.Should().BeFalse();

        var ageCol = snapshot.GetTable("users")!.Columns.First(c => c.Name == "age");
        ageCol.IsNullable.Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_ReturnsIndexes()
    {
        var snapshot = await _reader.ReadSchemaAsync("public");

        snapshot.IndexExists("idx_users_email").Should().BeTrue();
    }

    [Fact]
    public async Task ReadSchema_EmptySchema_ReturnsEmpty()
    {
        var snapshot = await _reader.ReadSchemaAsync("nonexistent_schema");

        snapshot.Tables.Should().BeEmpty();
    }
}
