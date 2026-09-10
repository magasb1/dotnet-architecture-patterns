using System.Text.Json;

namespace StorageDemo.Core.Documents;

/// <summary>
/// One place that decides how the metadata bag is written down, so LiteDB and PostgreSQL store
/// byte-identical JSON and the contract tests can hold them to the same result.
/// </summary>
public static class DocumentMetadata
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    public static string Serialize(IReadOnlyDictionary<string, string>? metadata)
        => metadata is null || metadata.Count == 0
            ? "{}"
            : JsonSerializer.Serialize(metadata, Options);

    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Options)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            // A row written by an older version, or hand-edited. Losing metadata beats failing a read.
            return new Dictionary<string, string>();
        }
    }
}
