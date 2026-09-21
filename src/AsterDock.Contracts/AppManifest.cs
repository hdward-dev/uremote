using System.Text.Json.Serialization;

namespace AsterDock.Contracts;

public sealed class AppManifest
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("entryAssembly")]
    public required string EntryAssembly { get; init; }

    [JsonPropertyName("entryType")]
    public required string EntryType { get; init; }

    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "app";

    [JsonPropertyName("category")]
    public string Category { get; init; } = "工具";

    [JsonPropertyName("order")]
    public int Order { get; init; }
}
