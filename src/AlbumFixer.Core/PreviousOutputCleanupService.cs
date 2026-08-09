using System.Text.Json;

namespace AlbumFixer.Core;

public sealed record PreviousOutputFile(string RelativePath, string FullPath, string? Sha256);
public sealed record PreviousOutputPlan(
    string AlbumRoot,
    string ReportPath,
    string ReportStatus,
    IReadOnlyList<PreviousOutputFile> Files);
public sealed record PreviousOutputCleanupResult(
    int DeletedFiles,
    string ArchivedReportPath,
    IReadOnlyList<string> DeletedRelativePaths);
public sealed record VerifiedOutputPlan(
    string AlbumRoot,
    string ReportPath,
    IReadOnlyList<PreviousOutputFile> Files);

public static class PreviousOutputCleanupService
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".dsf", ".dff"
    };

    public static PreviousOutputPlan? Discover(string albumRoot)
    {
        var root = Path.GetFullPath(albumRoot);
        var reportPath = Path.Combine(root, "conversion-report.json");
        if (!File.Exists(reportPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            var workflow = Text(report, "workflow_mode");
            if (!string.Equals(workflow, "flac_cue_split", StringComparison.OrdinalIgnoreCase)) return null;
            var verification = Property(report, "verification", out var value) ? value : default;
            var status = verification.ValueKind == JsonValueKind.Object ? Text(verification, "status") ?? "pending" : "pending";
            if (status.Equals("passed", StringComparison.OrdinalIgnoreCase)) return null;

            var hashes = CommitHashes(report);
            var tracksRoot = HostStagingService.SafeCombine(root, "Tracks");
            var files = EnumerateFileValues(report)
                .Where(IsLegacyTrackPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(relative =>
                {
                    var normalized = NormalizeRelative(relative);
                    var fullPath = HostStagingService.SafeCombine(root, normalized);
                    hashes.TryGetValue(normalized, out var sha256);
                    return new PreviousOutputFile(normalized, fullPath, sha256);
                })
                .Where(file => IsWithin(tracksRoot, file.FullPath))
                .Where(file => File.Exists(file.FullPath))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.Length == 0 ? null : new(root, reportPath, status, files);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static VerifiedOutputPlan? DiscoverVerified(string albumRoot)
    {
        var root = Path.GetFullPath(albumRoot);
        var reportPath = Path.Combine(root, "conversion-report.json");
        if (!File.Exists(reportPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            if (!string.Equals(Text(report, "workflow_mode"), "flac_cue_split", StringComparison.OrdinalIgnoreCase)) return null;
            var verification = Property(report, "verification", out var value) ? value : default;
            if (verification.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(verification, "status"), "passed", StringComparison.OrdinalIgnoreCase)) return null;

            var hashes = CommitHashes(report);
            var files = EnumerateReportedOutputs(report)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(relative =>
                {
                    var normalized = NormalizeRelative(relative);
                    var fullPath = HostStagingService.SafeCombine(root, normalized);
                    hashes.TryGetValue(normalized, out var sha256);
                    return new PreviousOutputFile(normalized, fullPath, sha256);
                })
                .Where(file => File.Exists(file.FullPath))
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.Length == 0 ? null : new(root, reportPath, files);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static IReadOnlyList<PreviousOutputFile> DirectFiles(VerifiedOutputPlan? plan) =>
        plan?.Files.Where(file => IsAtAlbumRoot(plan.AlbumRoot, file.FullPath)).ToArray() ?? [];

    public static async Task VerifyDirectFilesAsync(VerifiedOutputPlan? plan, CancellationToken token = default)
    {
        foreach (var file in DirectFiles(plan))
        {
            token.ThrowIfCancellationRequested();
            if (file.Sha256 is null)
                throw new IOException($"Verified prior output has no recorded SHA-256 and cannot be replaced: {file.RelativePath}");
            var attributes = File.GetAttributes(file.FullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Verified prior output is a reparse point and cannot be replaced safely: {file.RelativePath}");
            var actual = await HostStagingService.Sha256Async(file.FullPath, token);
            if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Verified prior output changed after its report was written and was retained: {file.RelativePath}");
        }
    }

    public static bool IsInnerTracksFile(string albumRoot, string path)
    {
        var relative = HostStagingService.SafeRelative(albumRoot, path);
        return relative.StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               AudioExtensions.Contains(Path.GetExtension(relative));
    }

    public static async Task<PreviousOutputCleanupResult?> CleanupAsync(
        string albumRoot,
        CancellationToken token = default)
    {
        var plan = Discover(albumRoot);
        if (plan is null) return null;

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(file.FullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Previous output is a reparse point and cannot be removed safely: {file.RelativePath}");
            if (file.Sha256 is not null)
            {
                var actual = await HostStagingService.Sha256Async(file.FullPath, token);
                if (!actual.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"Previous output changed after its report was written and was retained: {file.RelativePath}");
            }
        }

        foreach (var file in plan.Files)
        {
            token.ThrowIfCancellationRequested();
            File.Delete(file.FullPath);
            if (File.Exists(file.FullPath))
                throw new IOException($"Previous output still exists after deletion: {file.RelativePath}");
        }

        DeleteEmptyLegacyDirectories(plan);
        var archive = Path.Combine(plan.AlbumRoot,
            $"conversion-report.previous-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.Move(plan.ReportPath, archive, overwrite: false);
        return new(plan.Files.Count, archive, plan.Files.Select(file => file.RelativePath).ToArray());
    }

    private static void DeleteEmptyLegacyDirectories(PreviousOutputPlan plan)
    {
        var tracksRoot = HostStagingService.SafeCombine(plan.AlbumRoot, "Tracks");
        foreach (var directory in plan.Files
                     .Select(file => Path.GetDirectoryName(file.FullPath))
                     .Where(directory => directory is not null)
                     .Cast<string>()
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(directory => directory.Length))
        {
            for (var current = directory;
                 IsWithin(tracksRoot, current) && Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any();
                 current = Path.GetDirectoryName(current) ?? string.Empty)
            {
                Directory.Delete(current, recursive: false);
                if (current.Equals(tracksRoot, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
    }

    private static Dictionary<string, string> CommitHashes(JsonElement report)
    {
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Property(report, "commit", out var commit) || commit.ValueKind != JsonValueKind.Object ||
            !Property(commit, "files", out var files) || files.ValueKind != JsonValueKind.Array) return hashes;
        foreach (var item in files.EnumerateArray())
        {
            var file = Text(item, "file");
            var hash = Text(item, "sha256");
            if (file is null || hash is null) continue;
            hashes[NormalizeRelative(file)] = hash;
        }
        return hashes;
    }

    private static IEnumerable<string> EnumerateReportedOutputs(JsonElement report)
    {
        if (Property(report, "discs", out var discs) && discs.ValueKind == JsonValueKind.Array)
        {
            foreach (var disc in discs.EnumerateArray())
            {
                if (!Property(disc, "tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
                foreach (var track in tracks.EnumerateArray())
                {
                    var file = track.ValueKind == JsonValueKind.String
                        ? track.GetString()
                        : Text(track, "file");
                    if (IsOutputPath(file)) yield return file!;
                }
            }
        }

        if (Property(report, "cover", out var cover) && cover.ValueKind == JsonValueKind.Object)
        {
            var file = Text(cover, "file");
            if (IsOutputPath(file)) yield return file!;
        }
    }

    private static IEnumerable<string> EnumerateFileValues(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("file", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } value)
                    yield return value;
                foreach (var child in EnumerateFileValues(property.Value)) yield return child;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var child in EnumerateFileValues(item)) yield return child;
        }
    }

    private static bool IsLegacyTrackPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        var normalized = NormalizeRelative(path);
        return normalized.StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               AudioExtensions.Contains(Path.GetExtension(normalized));
    }

    private static bool IsOutputPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        var normalized = NormalizeRelative(path);
        return AudioExtensions.Contains(Path.GetExtension(normalized)) ||
               normalized.Equals("cover.jpg", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsAtAlbumRoot(string albumRoot, string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path))?.Equals(Path.GetFullPath(albumRoot), StringComparison.OrdinalIgnoreCase) == true;

    private static string? Text(JsonElement element, string name) =>
        Property(element, name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString()
            : null;

    private static bool Property(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
        value = default;
        return false;
    }
}
