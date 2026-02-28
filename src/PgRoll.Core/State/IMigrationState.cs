namespace PgRoll.Core.State;

public interface IMigrationState
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<MigrationRecord?> GetActiveMigrationAsync(string schema, CancellationToken ct = default);
    Task RecordStartedAsync(MigrationRecord record, CancellationToken ct = default);
    Task RecordCompletedAsync(string schema, string name, CancellationToken ct = default);
    Task DeleteRecordAsync(string schema, string name, CancellationToken ct = default);
    Task<IReadOnlyList<MigrationRecord>> GetHistoryAsync(string schema, CancellationToken ct = default);
}
