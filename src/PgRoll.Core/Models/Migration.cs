using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PgRoll.Core.Operations;
using YamlDotNet.Serialization;

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

    /// <summary>Deserializes a migration from a JSON string.</summary>
    public static Migration Deserialize(string json)
    {
        var result = JsonSerializer.Deserialize<Migration>(json, JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize migration.");
        return result;
    }

    /// <summary>Deserializes a migration from a YAML string by converting to JSON first.</summary>
    public static Migration DeserializeYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder().Build();
        var obj = deserializer.Deserialize(yaml);
        var jsonNode = ToJsonNode(obj)
            ?? throw new InvalidOperationException("Failed to parse YAML: document is empty.");
        return Deserialize(jsonNode.ToJsonString());
    }

    /// <summary>
    /// Reads a migration file and deserializes it, auto-detecting format by extension.
    /// Supports <c>.json</c>, <c>.yaml</c>, and <c>.yml</c>.
    /// </summary>
    public static async Task<Migration> LoadAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var content = await File.ReadAllTextAsync(filePath, ct);
        return ext is ".yaml" or ".yml" ? DeserializeYaml(content) : Deserialize(content);
    }

    public string Serialize() =>
        JsonSerializer.Serialize(this, JsonOptions);

    // ── YAML → JsonNode conversion ────────────────────────────────────────────

    // YamlDotNet's untyped Deserialize returns scalars as strings even for YAML booleans
    // and numbers. We infer the intended JSON type from the string content.
    private static JsonNode? ToJsonNode(object? obj) => obj switch
    {
        null => null,
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        float f => JsonValue.Create(f),
        double d => JsonValue.Create(d),
        string s => InferJsonValue(s),
        Dictionary<object, object> dict => ToJsonObject(dict),
        List<object> list => ToJsonArray(list),
        _ => JsonValue.Create(obj.ToString())
    };

    /// <summary>
    /// Converts a YAML scalar string to the most appropriate JSON value.
    /// YAML 1.1 booleans ("true"/"false", case-insensitive) become JSON booleans;
    /// integers and floats become JSON numbers; everything else stays a string.
    /// </summary>
    private static JsonNode? InferJsonValue(string s)
    {
        if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (string.Equals(s, "null", StringComparison.OrdinalIgnoreCase)) return null;
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l))
            return JsonValue.Create(l);
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return JsonValue.Create(d);
        return JsonValue.Create(s);
    }

    private static JsonObject ToJsonObject(Dictionary<object, object> dict)
    {
        var node = new JsonObject();
        foreach (var (k, v) in dict)
            node[k.ToString()!] = ToJsonNode(v);
        return node;
    }

    private static JsonArray ToJsonArray(List<object> list)
    {
        var node = new JsonArray();
        foreach (var item in list)
            node.Add(ToJsonNode(item));
        return node;
    }
}
