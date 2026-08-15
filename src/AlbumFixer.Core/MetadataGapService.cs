using System.Text.Json;

namespace AlbumFixer.Core;

public sealed record MetadataGapResult(
    bool SplitCompleted,
    bool RequiresResearch,
    IReadOnlyList<string> MissingFields,
    IReadOnlyList<string> LocalEvidence);

public static class MetadataFieldPolicy
{
    private static readonly HashSet<string> OptionalFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "LABEL",
        "BARCODE",
        "RELEASECOUNTRY"
    };

    public static bool IsOptional(string field) => OptionalFields.Contains(field.Trim());

    public static IReadOnlyList<string> RequiredMissing(IEnumerable<string> fields) => fields
        .Where(field => !string.IsNullOrWhiteSpace(field) && !IsOptional(field))
        .Select(field => field.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static IReadOnlyList<string> OptionalMissing(IEnumerable<string> fields) => fields
        .Where(field => !string.IsNullOrWhiteSpace(field) && IsOptional(field))
        .Select(field => field.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public static class MetadataGapService
{
    public const string FileName = "metadata-gaps.json";

    public static string GetPath(string jobDirectory) => Path.Combine(jobDirectory, FileName);

    public static async Task<MetadataGapResult> LoadAsync(string jobDirectory, CancellationToken token = default)
    {
        var path = GetPath(jobDirectory);
        if (!File.Exists(path))
            throw new FileNotFoundException("The split worker did not create the required metadata-gap handoff. Research will not be started speculatively.", path);

        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: token);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("The metadata-gap handoff is not a JSON object.");

        var splitCompleted = ReadBoolean(root, "split_completed");
        if (!splitCompleted)
            throw new InvalidOperationException("The metadata-gap handoff does not confirm that splitting completed. Metadata enrichment will not start.");

        var missing = ReadStrings(root, "missing_fields");
        var requested = ReadBoolean(root, "requires_research");
        var requiresResearch = requested || missing.Count > 0;
        if (requiresResearch && missing.Count == 0)
            throw new JsonException("Metadata research was requested without naming any missing fields.");

        return new(splitCompleted, requiresResearch, missing, ReadStrings(root, "local_evidence", required: false));
    }

    private static bool ReadBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new JsonException($"The metadata-gap handoff has no boolean '{name}' value.");
        return value.GetBoolean();
    }

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string name, bool required = true)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            if (!required) return [];
            throw new JsonException($"The metadata-gap handoff has no '{name}' array.");
        }
        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException($"The metadata-gap handoff '{name}' value is not an array.");

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()?.Trim() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
