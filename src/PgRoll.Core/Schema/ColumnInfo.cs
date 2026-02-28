namespace PgRoll.Core.Schema;

public sealed record ColumnInfo(
    string Name,
    string DataType,
    bool IsNullable,
    string? Default,
    bool IsPrimaryKey,
    int OrdinalPosition
);
