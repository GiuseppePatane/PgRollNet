using FluentAssertions;
using Npgsql;
using PgRoll.Core.Models;
using PgRoll.Core.Operations;
using PgRoll.PostgreSQL.Tests.Infrastructure;

namespace PgRoll.PostgreSQL.Tests;

/// <summary>
/// Integration tests for the 11 new operations:
/// raw_sql, set_not_null, drop_not_null, set_default, drop_default,
/// create_schema, drop_schema, create_enum, drop_enum, create_view, drop_view.
/// Also covers ColumnDefinition.Unique and ColumnDefinition.References.
/// </summary>
[Collection("Postgres")]
public class NewOperationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private NpgsqlDataSource _ds = null!;
    private readonly string _dbName = $"pgroll_newops_{Guid.NewGuid():N}";
    private PgMigrationExecutor _executor = null!;

    public async Task InitializeAsync()
    {
        _ds = await DatabaseFactory.CreateIsolatedDatabaseAsync(postgres.ConnectionString, _dbName);
        _executor = new PgMigrationExecutor(_ds);
        await _executor.InitializeAsync();
    }

    public async Task DisposeAsync()
    {
        await _ds.DisposeAsync();
        await DatabaseFactory.DropDatabaseAsync(postgres.ConnectionString, _dbName);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ExecSqlAsync(string sql)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> TableExistsAsync(string tableName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.tables WHERE table_schema='public' AND table_name=$1", conn);
        cmd.Parameters.AddWithValue(tableName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> SchemaExistsAsync(string schemaName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.schemata WHERE schema_name=$1", conn);
        cmd.Parameters.AddWithValue(schemaName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> EnumTypeExistsAsync(string typeName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM pg_type t JOIN pg_namespace n ON n.oid=t.typnamespace WHERE n.nspname='public' AND t.typname=$1 AND t.typtype='e'",
            conn);
        cmd.Parameters.AddWithValue(typeName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> ViewExistsAsync(string viewName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM information_schema.views WHERE table_schema='public' AND table_name=$1", conn);
        cmd.Parameters.AddWithValue(viewName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private async Task<bool> ColumnIsNotNullableAsync(string table, string column)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT is_nullable FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2",
            conn);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(column);
        var result = await cmd.ExecuteScalarAsync() as string;
        return result == "NO";
    }

    private async Task<string?> GetColumnDefaultAsync(string table, string column)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT column_default FROM information_schema.columns WHERE table_schema='public' AND table_name=$1 AND column_name=$2",
            conn);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(column);
        return await cmd.ExecuteScalarAsync() as string;
    }

    private async Task<bool> ConstraintExistsAsync(string table, string constraintName)
    {
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname='public' AND t.relname=$1 AND c.conname=$2
            """, conn);
        cmd.Parameters.AddWithValue(table);
        cmd.Parameters.AddWithValue(constraintName);
        return await cmd.ExecuteScalarAsync() is not null;
    }

    private Migration MakeMigration(string name, IMigrationOperation op) =>
        Migration.Deserialize($$"""{"name":"{{name}}","operations":[{{System.Text.Json.JsonSerializer.Serialize(op, op.GetType())}}]}""");

    // ── raw_sql ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RawSql_ExecutesSql()
    {
        var migration = Migration.Deserialize("""
            {"name":"m1","operations":[{"type":"raw_sql","sql":"CREATE TABLE raw_test(id serial PRIMARY KEY)"}]}
            """);
        await _executor.StartAsync(migration);
        (await TableExistsAsync("raw_test")).Should().BeTrue();
    }

    [Fact]
    public async Task RawSql_Complete_IsNoop()
    {
        var migration = Migration.Deserialize("""
            {"name":"m2","operations":[{"type":"raw_sql","sql":"CREATE TABLE raw_complete(id serial PRIMARY KEY)"}]}
            """);
        await _executor.StartAsync(migration);
        var act = async () => await _executor.CompleteAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RawSql_Rollback_ExecutesRollbackSql()
    {
        var migration = Migration.Deserialize("""
            {"name":"m3","operations":[{"type":"raw_sql","sql":"CREATE TABLE raw_rb(id serial)","rollback_sql":"DROP TABLE IF EXISTS raw_rb"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await TableExistsAsync("raw_rb")).Should().BeFalse();
    }

    [Fact]
    public async Task RawSql_NoRollbackSql_RollbackIsNoop()
    {
        var migration = Migration.Deserialize("""
            {"name":"m4","operations":[{"type":"raw_sql","sql":"CREATE TABLE raw_noop(id serial)"}]}
            """);
        await _executor.StartAsync(migration);
        var act = async () => await _executor.RollbackAsync();
        await act.Should().NotThrowAsync();
    }

    // ── set_not_null ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SetNotNull_AddsConstraint()
    {
        await ExecSqlAsync("CREATE TABLE snn_t (id serial PRIMARY KEY, name text)");
        await ExecSqlAsync("INSERT INTO snn_t (name) VALUES ('x')");
        var migration = Migration.Deserialize("""
            {"name":"m5","operations":[{"type":"set_not_null","table":"snn_t","column":"name"}]}
            """);
        await _executor.StartAsync(migration);
        (await ColumnIsNotNullableAsync("snn_t", "name")).Should().BeTrue();
    }

    [Fact]
    public async Task SetNotNull_Rollback_RestoresNullable()
    {
        await ExecSqlAsync("CREATE TABLE snn_rb (id serial PRIMARY KEY, age int)");
        await ExecSqlAsync("INSERT INTO snn_rb (age) VALUES (10)");
        var migration = Migration.Deserialize("""
            {"name":"m6","operations":[{"type":"set_not_null","table":"snn_rb","column":"age"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await ColumnIsNotNullableAsync("snn_rb", "age")).Should().BeFalse();
    }

    // ── drop_not_null ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DropNotNull_RemovesConstraint()
    {
        await ExecSqlAsync("CREATE TABLE dnn_t (id serial PRIMARY KEY, name text NOT NULL)");
        var migration = Migration.Deserialize("""
            {"name":"m7","operations":[{"type":"drop_not_null","table":"dnn_t","column":"name"}]}
            """);
        await _executor.StartAsync(migration);
        (await ColumnIsNotNullableAsync("dnn_t", "name")).Should().BeFalse();
    }

    [Fact]
    public async Task DropNotNull_Rollback_RestoresNotNull()
    {
        await ExecSqlAsync("CREATE TABLE dnn_rb (id serial PRIMARY KEY, code text NOT NULL)");
        var migration = Migration.Deserialize("""
            {"name":"m8","operations":[{"type":"drop_not_null","table":"dnn_rb","column":"code"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await ColumnIsNotNullableAsync("dnn_rb", "code")).Should().BeTrue();
    }

    // ── set_default ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SetDefault_SetsValue()
    {
        await ExecSqlAsync("CREATE TABLE sd_t (id serial PRIMARY KEY, score int)");
        var migration = Migration.Deserialize("""
            {"name":"m9","operations":[{"type":"set_default","table":"sd_t","column":"score","value":"0"}]}
            """);
        await _executor.StartAsync(migration);
        var def = await GetColumnDefaultAsync("sd_t", "score");
        def.Should().Be("0");
    }

    [Fact]
    public async Task SetDefault_Complete_IsNoop()
    {
        await ExecSqlAsync("CREATE TABLE sd_noop (id serial PRIMARY KEY, val int)");
        var migration = Migration.Deserialize("""
            {"name":"m10","operations":[{"type":"set_default","table":"sd_noop","column":"val","value":"42"}]}
            """);
        await _executor.StartAsync(migration);
        var act = async () => await _executor.CompleteAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetDefault_Rollback_DropsDefault()
    {
        await ExecSqlAsync("CREATE TABLE sd_rb (id serial PRIMARY KEY, qty int)");
        var migration = Migration.Deserialize("""
            {"name":"m11","operations":[{"type":"set_default","table":"sd_rb","column":"qty","value":"1"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        var def = await GetColumnDefaultAsync("sd_rb", "qty");
        def.Should().BeNull();
    }

    // ── drop_default ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DropDefault_RemovesValue()
    {
        await ExecSqlAsync("CREATE TABLE dd_t (id serial PRIMARY KEY, status text DEFAULT 'active')");
        var migration = Migration.Deserialize("""
            {"name":"m12","operations":[{"type":"drop_default","table":"dd_t","column":"status"}]}
            """);
        await _executor.StartAsync(migration);
        var def = await GetColumnDefaultAsync("dd_t", "status");
        def.Should().BeNull();
    }

    [Fact]
    public async Task DropDefault_Rollback_IsNoop()
    {
        await ExecSqlAsync("CREATE TABLE dd_rb (id serial PRIMARY KEY, flag text DEFAULT 'y')");
        var migration = Migration.Deserialize("""
            {"name":"m13","operations":[{"type":"drop_default","table":"dd_rb","column":"flag"}]}
            """);
        await _executor.StartAsync(migration);
        var act = async () => await _executor.RollbackAsync();
        await act.Should().NotThrowAsync();
        // Default is not restored (intentional no-op)
        var def = await GetColumnDefaultAsync("dd_rb", "flag");
        def.Should().BeNull();
    }

    // ── create_schema ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateSchema_CreatesSchema()
    {
        var migration = Migration.Deserialize("""
            {"name":"m14","operations":[{"type":"create_schema","schema":"myapp"}]}
            """);
        await _executor.StartAsync(migration);
        (await SchemaExistsAsync("myapp")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateSchema_Rollback_DropsSchema()
    {
        var migration = Migration.Deserialize("""
            {"name":"m15","operations":[{"type":"create_schema","schema":"myapp2"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await SchemaExistsAsync("myapp2")).Should().BeFalse();
    }

    // ── drop_schema ───────────────────────────────────────────────────────────

    [Fact]
    public async Task DropSchema_Lifecycle_SoftDelete()
    {
        await ExecSqlAsync("CREATE SCHEMA tosee");
        var migration = Migration.Deserialize("""
            {"name":"m16","operations":[{"type":"drop_schema","schema":"tosee"}]}
            """);
        await _executor.StartAsync(migration);
        // Original name should be gone, soft-deleted name should exist
        (await SchemaExistsAsync("tosee")).Should().BeFalse();
        (await SchemaExistsAsync("_pgroll_del_tosee")).Should().BeTrue();
    }

    [Fact]
    public async Task DropSchema_Complete_DropsSchema()
    {
        await ExecSqlAsync("CREATE SCHEMA todrop");
        var migration = Migration.Deserialize("""
            {"name":"m17","operations":[{"type":"drop_schema","schema":"todrop"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();
        (await SchemaExistsAsync("_pgroll_del_todrop")).Should().BeFalse();
        (await SchemaExistsAsync("todrop")).Should().BeFalse();
    }

    [Fact]
    public async Task DropSchema_Rollback_RestoresSchema()
    {
        await ExecSqlAsync("CREATE SCHEMA tokeep");
        var migration = Migration.Deserialize("""
            {"name":"m18","operations":[{"type":"drop_schema","schema":"tokeep"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await SchemaExistsAsync("tokeep")).Should().BeTrue();
        (await SchemaExistsAsync("_pgroll_del_tokeep")).Should().BeFalse();
    }

    // ── create_enum ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateEnum_CreatesType()
    {
        var migration = Migration.Deserialize("""
            {"name":"m19","operations":[{"type":"create_enum","name":"order_status","values":["pending","shipped","delivered"]}]}
            """);
        await _executor.StartAsync(migration);
        (await EnumTypeExistsAsync("order_status")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateEnum_Rollback_DropsType()
    {
        var migration = Migration.Deserialize("""
            {"name":"m20","operations":[{"type":"create_enum","name":"color_enum","values":["red","green"]}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await EnumTypeExistsAsync("color_enum")).Should().BeFalse();
    }

    // ── drop_enum ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropEnum_Lifecycle_SoftDelete()
    {
        await ExecSqlAsync("CREATE TYPE public.mood AS ENUM ('happy', 'sad')");
        var migration = Migration.Deserialize("""
            {"name":"m21","operations":[{"type":"drop_enum","name":"mood"}]}
            """);
        await _executor.StartAsync(migration);
        (await EnumTypeExistsAsync("mood")).Should().BeFalse();
        (await EnumTypeExistsAsync("_pgroll_del_mood")).Should().BeTrue();
    }

    [Fact]
    public async Task DropEnum_Complete_DropsType()
    {
        await ExecSqlAsync("CREATE TYPE public.season AS ENUM ('spring','summer','autumn','winter')");
        var migration = Migration.Deserialize("""
            {"name":"m22","operations":[{"type":"drop_enum","name":"season"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();
        (await EnumTypeExistsAsync("_pgroll_del_season")).Should().BeFalse();
    }

    [Fact]
    public async Task DropEnum_Rollback_TypeStillExists()
    {
        await ExecSqlAsync("CREATE TYPE public.direction AS ENUM ('north','south','east','west')");
        var migration = Migration.Deserialize("""
            {"name":"m23","operations":[{"type":"drop_enum","name":"direction"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await EnumTypeExistsAsync("direction")).Should().BeTrue();
        (await EnumTypeExistsAsync("_pgroll_del_direction")).Should().BeFalse();
    }

    // ── create_view ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateView_CreatesView()
    {
        await ExecSqlAsync("CREATE TABLE cv_base (id serial PRIMARY KEY, name text)");
        var migration = Migration.Deserialize("""
            {"name":"m24","operations":[{"type":"create_view","name":"cv_view","definition":"SELECT id, name FROM cv_base"}]}
            """);
        await _executor.StartAsync(migration);
        (await ViewExistsAsync("cv_view")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateView_Rollback_DropsView()
    {
        await ExecSqlAsync("CREATE TABLE cv_rb_base (id serial PRIMARY KEY, val int)");
        var migration = Migration.Deserialize("""
            {"name":"m25","operations":[{"type":"create_view","name":"cv_rb_view","definition":"SELECT id FROM cv_rb_base"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await ViewExistsAsync("cv_rb_view")).Should().BeFalse();
    }

    // ── drop_view ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DropView_Lifecycle_SoftDelete()
    {
        await ExecSqlAsync("CREATE TABLE dv_base (id int)");
        await ExecSqlAsync("CREATE VIEW public.dv_view AS SELECT id FROM dv_base");
        var migration = Migration.Deserialize("""
            {"name":"m26","operations":[{"type":"drop_view","name":"dv_view"}]}
            """);
        await _executor.StartAsync(migration);
        (await ViewExistsAsync("dv_view")).Should().BeFalse();
        (await ViewExistsAsync("_pgroll_del_dv_view")).Should().BeTrue();
    }

    [Fact]
    public async Task DropView_Complete_DropsView()
    {
        await ExecSqlAsync("CREATE TABLE dvc_base (id int)");
        await ExecSqlAsync("CREATE VIEW public.dvc_view AS SELECT id FROM dvc_base");
        var migration = Migration.Deserialize("""
            {"name":"m27","operations":[{"type":"drop_view","name":"dvc_view"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.CompleteAsync();
        (await ViewExistsAsync("_pgroll_del_dvc_view")).Should().BeFalse();
        (await ViewExistsAsync("dvc_view")).Should().BeFalse();
    }

    [Fact]
    public async Task DropView_Rollback_ViewStillExists()
    {
        await ExecSqlAsync("CREATE TABLE dvr_base (id int)");
        await ExecSqlAsync("CREATE VIEW public.dvr_view AS SELECT id FROM dvr_base");
        var migration = Migration.Deserialize("""
            {"name":"m28","operations":[{"type":"drop_view","name":"dvr_view"}]}
            """);
        await _executor.StartAsync(migration);
        await _executor.RollbackAsync();
        (await ViewExistsAsync("dvr_view")).Should().BeTrue();
        (await ViewExistsAsync("_pgroll_del_dvr_view")).Should().BeFalse();
    }

    // ── ColumnDefinition.Unique + References ──────────────────────────────────

    [Fact]
    public async Task CreateTable_WithUnique_AddsConstraint()
    {
        var migration = Migration.Deserialize("""
            {"name":"m29","operations":[{"type":"create_table","table":"uniq_t","columns":[
                {"name":"id","type":"serial","primary_key":true},
                {"name":"email","type":"text","unique":true}
            ]}]}
            """);
        await _executor.StartAsync(migration);
        // UNIQUE constraint should exist on the column
        (await ConstraintExistsAsync("uniq_t", "uniq_t_email_key")).Should().BeTrue();
    }

    [Fact]
    public async Task CreateTable_WithReferences_AddsForeignKey()
    {
        await ExecSqlAsync("CREATE TABLE ref_parent (id serial PRIMARY KEY)");
        var migration = Migration.Deserialize("""
            {"name":"m30","operations":[{"type":"create_table","table":"ref_child","columns":[
                {"name":"id","type":"serial","primary_key":true},
                {"name":"parent_id","type":"integer","references":"ref_parent(id)"}
            ]}]}
            """);
        await _executor.StartAsync(migration);
        // FK constraint should exist
        await using var conn = await _ds.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            """
            SELECT 1 FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            WHERE n.nspname='public' AND t.relname='ref_child' AND c.contype='f'
            """, conn);
        (await cmd.ExecuteScalarAsync()).Should().NotBeNull();
    }

    // ── Online Validate() failures ────────────────────────────────────────────

    [Fact]
    public async Task SetNotNull_MissingTable_Fails()
    {
        var migration = Migration.Deserialize("""
            {"name":"m31","operations":[{"type":"set_not_null","table":"no_such_table","column":"col"}]}
            """);
        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<Exception>().WithMessage("*no_such_table*");
    }

    [Fact]
    public async Task SetNotNull_MissingColumn_Fails()
    {
        await ExecSqlAsync("CREATE TABLE snn_miss (id serial PRIMARY KEY)");
        var migration = Migration.Deserialize("""
            {"name":"m32","operations":[{"type":"set_not_null","table":"snn_miss","column":"no_col"}]}
            """);
        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<Exception>().WithMessage("*no_col*");
    }

    [Fact]
    public async Task SetDefault_MissingColumn_Fails()
    {
        await ExecSqlAsync("CREATE TABLE sdf_miss (id serial PRIMARY KEY)");
        var migration = Migration.Deserialize("""
            {"name":"m33","operations":[{"type":"set_default","table":"sdf_miss","column":"no_col","value":"0"}]}
            """);
        var act = async () => await _executor.StartAsync(migration);
        await act.Should().ThrowAsync<Exception>().WithMessage("*no_col*");
    }
}
