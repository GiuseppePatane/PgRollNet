namespace PgRoll.Core.Schema;

public sealed class SchemaSnapshot
{
    private readonly Dictionary<string, TableInfo> _tables;
    private readonly HashSet<string> _indexes;

    public SchemaSnapshot(IEnumerable<TableInfo> tables, IEnumerable<string>? indexNames = null)
    {
        _tables = tables.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _indexes = new HashSet<string>(indexNames ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public static SchemaSnapshot Empty { get; } = new SchemaSnapshot(Array.Empty<TableInfo>());

    public IReadOnlyDictionary<string, TableInfo> Tables => _tables;

    public bool TableExists(string name) => _tables.ContainsKey(name);

    public TableInfo? GetTable(string name) => _tables.GetValueOrDefault(name);

    public bool IndexExists(string name) => _indexes.Contains(name);

    public bool ColumnExists(string tableName, string columnName)
    {
        var table = GetTable(tableName);
        return table?.HasColumn(columnName) ?? false;
    }

    public bool ConstraintExists(string tableName, string constraintName)
    {
        var table = GetTable(tableName);
        return table?.Constraints.Any(c => c.Name.Equals(constraintName, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    public ConstraintInfo? GetConstraint(string tableName, string constraintName)
    {
        var table = GetTable(tableName);
        return table?.Constraints.FirstOrDefault(c => c.Name.Equals(constraintName, StringComparison.OrdinalIgnoreCase));
    }
}
