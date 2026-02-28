namespace PgRoll.EntityFrameworkCore;

internal static class EfCoreTypeMapper
{
    internal static string MapColumnType(string? columnType, Type? clrType)
    {
        if (!string.IsNullOrWhiteSpace(columnType))
            return columnType;

        if (clrType is null)
            return "text";

        var underlying = Nullable.GetUnderlyingType(clrType) ?? clrType;

        return underlying switch
        {
            var t when t == typeof(string) => "text",
            var t when t == typeof(int) => "integer",
            var t when t == typeof(long) => "bigint",
            var t when t == typeof(short) => "smallint",
            var t when t == typeof(bool) => "boolean",
            var t when t == typeof(DateTime) => "timestamp with time zone",
            var t when t == typeof(DateTimeOffset) => "timestamp with time zone",
            var t when t == typeof(Guid) => "uuid",
            var t when t == typeof(decimal) => "numeric",
            var t when t == typeof(double) => "double precision",
            var t when t == typeof(float) => "real",
            var t when t == typeof(byte[]) => "bytea",
            var t when t == typeof(DateOnly) => "date",
            var t when t == typeof(TimeOnly) => "time",
            _ => "text"
        };
    }
}
