namespace PgRoll.Core.Schema;

public sealed record TableInfo(
    string Schema,
    string Name,
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<ConstraintInfo> Constraints
)
{
    public bool HasColumn(string columnName) =>
        Columns.Any(c => c.Name.Equals(columnName, StringComparison.OrdinalIgnoreCase));
}
