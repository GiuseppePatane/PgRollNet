using System.Text.Json;
using System.Text.Json.Serialization;
using PgRoll.Core.Operations;

namespace PgRoll.Core.Models;

/// <summary>
/// Handles polymorphic deserialization of IMigrationOperation using the "type" discriminator field.
/// </summary>
public sealed class MigrationOperationConverter : JsonConverter<IMigrationOperation>
{
    public override IMigrationOperation Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeProp))
            throw new JsonException("Migration operation missing 'type' discriminator.");

        var typeName = typeProp.GetString()
            ?? throw new JsonException("Migration operation 'type' is null.");

        var raw = root.GetRawText();

        return typeName switch
        {
            "create_table" => JsonSerializer.Deserialize<CreateTableOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize create_table."),
            "drop_table" => JsonSerializer.Deserialize<DropTableOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize drop_table."),
            "rename_table" => JsonSerializer.Deserialize<RenameTableOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize rename_table."),
            "add_column" => JsonSerializer.Deserialize<AddColumnOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize add_column."),
            "drop_column" => JsonSerializer.Deserialize<DropColumnOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize drop_column."),
            "rename_column" => JsonSerializer.Deserialize<RenameColumnOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize rename_column."),
            "create_index" => JsonSerializer.Deserialize<CreateIndexOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize create_index."),
            "drop_index" => JsonSerializer.Deserialize<DropIndexOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize drop_index."),
            "alter_column" => JsonSerializer.Deserialize<AlterColumnOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize alter_column."),
            "create_constraint" => JsonSerializer.Deserialize<CreateConstraintOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize create_constraint."),
            "drop_constraint" => JsonSerializer.Deserialize<DropConstraintOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize drop_constraint."),
            "rename_constraint" => JsonSerializer.Deserialize<RenameConstraintOperation>(raw, options)
                ?? throw new JsonException("Failed to deserialize rename_constraint."),
            _ => throw new JsonException($"Unknown operation type '{typeName}'.")
        };
    }

    public override void Write(Utf8JsonWriter writer, IMigrationOperation value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}

