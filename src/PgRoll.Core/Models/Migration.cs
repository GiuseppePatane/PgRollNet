using System.Text.Json;
using System.Text.Json.Serialization;
using PgRoll.Core.Operations;

namespace PgRoll.Core.Models;

public sealed class Migration
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new MigrationOperationConverter() }
    };

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("operations")]
    public required IReadOnlyList<IMigrationOperation> Operations { get; init; }

    public static Migration Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<Migration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize migration.");
        return result;
    }

    public string Serialize() =>
        JsonSerializer.Serialize(this, JsonOptions);
}
