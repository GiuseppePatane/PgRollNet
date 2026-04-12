using PgRoll.Core.Models;
using PgRoll.Core.Operations;
using PgRoll.Core.State;

namespace PgRoll.Cli;

internal sealed record ChecksumComparison(
    string Name,
    string FilePath,
    string ExpectedChecksum,
    string ActualChecksum);

internal static class MigrationDiagnostics
{
    public static IEnumerable<string> GetWarnings(Migration migration)
    {
        foreach (var op in migration.Operations)
        {
            if (op is RawSqlOperation raw)
            {
                yield return raw.RollbackSql is null
                    ? "raw_sql without rollback_sql requires manual recovery if rollback is needed."
                    : "raw_sql should be reviewed manually because pgroll cannot reason about its side effects.";
            }

            if (op is AlterColumnOperation alter && alter.Down is null)
                yield return $"alter_column on '{alter.Table}.{alter.Column}' has no down expression for rollback writes.";

            if (op is AddColumnOperation add && add.Up is not null && add.Down is null)
                yield return $"add_column on '{add.Table}.{add.Column.Name}' uses expand/contract without a down expression.";
        }
    }

    public static async Task<IReadOnlyList<(FileInfo File, Migration Migration)>> LoadMigrationsAsync(DirectoryInfo dir, CancellationToken ct = default)
    {
        var files = dir.GetFiles("*.json")
            .Concat(dir.GetFiles("*.yaml"))
            .Concat(dir.GetFiles("*.yml"))
            .OrderBy(f => f.Name)
            .ToList();

        var loaded = new List<(FileInfo File, Migration Migration)>();
        foreach (var file in files)
            loaded.Add((file, await Migration.LoadAsync(file.FullName, ct)));

        return loaded;
    }

    public static IReadOnlyList<ChecksumComparison> CompareChecksums(
        IEnumerable<(FileInfo File, Migration Migration)> files,
        IEnumerable<MigrationRecord> history)
    {
        var historyByName = history
            .Where(r => r.MigrationChecksum is not null)
            .ToDictionary(r => r.Name, StringComparer.Ordinal);

        var mismatches = new List<ChecksumComparison>();
        foreach (var (file, migration) in files)
        {
            if (!historyByName.TryGetValue(migration.Name, out var record))
                continue;

            var checksum = MigrationChecksum.ComputeSha256(migration.Serialize());
            if (!string.Equals(checksum, record.MigrationChecksum, StringComparison.Ordinal))
            {
                mismatches.Add(new ChecksumComparison(
                    migration.Name,
                    file.FullName,
                    record.MigrationChecksum!,
                    checksum));
            }
        }

        return mismatches;
    }
}
