using Testcontainers.PostgreSql;

namespace PgRoll.PostgreSQL.Tests.Infrastructure;

/// <summary>
/// Shared PostgreSQL container fixture — one container per xUnit collection.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private static readonly string PostgresImage =
        Environment.GetEnvironmentVariable("PGROLL_TEST_POSTGRES_IMAGE") ?? "postgres:17-alpine";

    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder()
            .WithImage(PostgresImage)
            .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    // Marker class — no code needed
}
