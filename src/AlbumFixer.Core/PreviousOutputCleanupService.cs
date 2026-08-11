using System.Text.Json;

namespace AlbumFixer.Core;

public sealed record PreviousOutputFile(string RelativePath, string FullPath, long? Size);
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
public sealed record CompletedOutputPlan(
    string AlbumRoot,
    string ReportPath,
    string WorkflowMode,
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

            var sizes = CommitSizes(report);
            var tracksRoot = HostStagingService.SafeCombine(root, "Tracks");
            var files = EnumerateReportedOutputs(report)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(relative =>
                {
                    var normalized = NormalizeRelative(relative);
                    var fullPath = HostStagingService.SafeCombine(root, normalized);
                    var size = sizes.TryGetValue(normalized, out var recordedSize) ? recordedSize : (long?)null;
                    return new PreviousOutputFile(normalized, fullPath, size);
                })
                .Where(file => IsLegacyTrackPath(file.RelativePath)
                    ? IsWithin(tracksRoot, file.FullPath)
                    : file.Size is not null)
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

            var sizes = CommitSizes(report);
            var files = EnumerateReportedOutputs(report)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(relative =>
                {
                    var normalized = NormalizeRelative(relative);
                    var fullPath = HostStagingService.SafeCombine(root, normalized);
                    var size = sizes.TryGetValue(normalized, out var recordedSize) ? recordedSize : (long?)null;
                    return new PreviousOutputFile(normalized, fullPath, size);
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

    public static CompletedOutputPlan? DiscoverCompleted(string albumRoot)
    {
        var root = Path.GetFullPath(albumRoot);
        var reportPath = Path.Combine(root, "conversion-report.json");
        if (!File.Exists(reportPath)) return null;

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            var workflow = Text(report, "workflow_mode");
            if (workflow is null ||
                !workflow.Equals("flac_cue_split", StringComparison.OrdinalIgnoreCase) &&
                !workflow.Equals("sacd_iso_extract", StringComparison.OrdinalIgnoreCase)) return null;
            var verification = Property(report, "verification", out var value) ? value : default;
            if (verification.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(verification, "status"), "passed", StringComparison.OrdinalIgnoreCase)) return null;
            var sourcesDeleted = Flag(verification, "sources_deleted");
            if (Property(report, "deletion", out var deletion) && deletion.ValueKind == JsonValueKind.Object)
                sourcesDeleted = sourcesDeleted || Flag(deletion, "performed") ||
                    string.Equals(Text(deletion, "status"), "completed", StringComparison.OrdinalIgnoreCase);
            if (!sourcesDeleted) return null;

            var sizes = CommitSizes(report);
            var reportedPaths = EnumerateReportedOutputs(report)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (reportedPaths.Length == 0) return null;
            var files = reportedPaths.Select(relative =>
                {
                    var normalized = NormalizeRelative(relative);
                    var fullPath = HostStagingService.SafeCombine(root, normalized);
                    var size = sizes.TryGetValue(normalized, out var recordedSize) ? recordedSize : (long?)null;
                    return new PreviousOutputFile(normalized, fullPath, size);
                })
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.All(file => File.Exists(file.FullPath))
                ? new(root, reportPath, workflow, files)
                : null;
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

    public static void VerifyDirectFileSizes(VerifiedOutputPlan? plan, CancellationToken token = default)
    {
        foreach (var file in DirectFiles(plan))
        {
            token.ThrowIfCancellationRequested();
            if (file.Size is null)
                throw new IOException($"Verified prior output has no recorded file size and cannot be replaced: {file.RelativePath}");
            var attributes = File.GetAttributes(file.FullPath);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Verified prior output is a reparse point and cannot be replaced safely: {file.RelativePath}");
            if (new FileInfo(file.FullPath).Length != file.Size.Value)
                throw new IOException($"Verified prior output size changed after its report was written and was retained: {file.RelativePath}");
        }
    }

    public static bool IsInnerTracksFile(string albumRoot, string path)
    {
        var relative = HostStagingService.SafeRelative(albumRoot, path);
        return relative.StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               AudioExtensions.Contains(Path.GetExtension(relative));
    }

    public static PreviousOutputCleanupResult? Cleanup(
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
            if (file.Size is not null && new FileInfo(file.FullPath).Length != file.Size.Value)
                throw new IOException($"Previous output size changed after its report was written and was retained: {file.RelativePath}");
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

    private static Dictionary<string, long> CommitSizes(JsonElement report)
    {
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Property(report, "commit", out var commit) || commit.ValueKind != JsonValueKind.Object ||
            !Property(commit, "files", out var files) || files.ValueKind != JsonValueKind.Array) return sizes;
        foreach (var item in files.EnumerateArray())
        {
            var file = Text(item, "file");
            if (file is null || !Property(item, "size", out var size) || !size.TryGetInt64(out var bytes) || bytes < 0) continue;
            sizes[NormalizeRelative(file)] = bytes;
        }
        return sizes;
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

        if (Property(report, "areas", out var areas) && areas.ValueKind == JsonValueKind.Array)
        {
            foreach (var area in areas.EnumerateArray())
            {
                if (!Property(area, "tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array) continue;
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
        return AudioExtensions.Contains(Path.GetExtension(normalized));
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

    private static bool Flag(JsonElement element, string name)
    {
        if (!Property(element, name, out var value)) return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed) && parsed;
    }

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
