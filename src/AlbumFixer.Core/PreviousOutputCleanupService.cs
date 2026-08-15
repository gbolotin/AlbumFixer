using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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
    IReadOnlyList<PreviousOutputFile> Files,
    bool SourcesDeleted,
    IReadOnlyList<string> SourcePaths,
    bool RecoveredFromStaleFallback = false,
    string? RecoveryDetail = null);

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
            if (!IsFlacWorkflow(workflow)) return null;
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
            if (!IsFlacWorkflow(Text(report, "workflow_mode"))) return null;
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
            if (!IsCompletedWorkflow(workflow)) return null;
            var verification = Property(report, "verification", out var value) ? value : default;
            if (verification.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(verification, "status"), "passed", StringComparison.OrdinalIgnoreCase)) return null;
            var sourcesDeleted = Flag(verification, "sources_deleted");
            if (Property(report, "deletion", out var deletion) && deletion.ValueKind == JsonValueKind.Object)
                sourcesDeleted = sourcesDeleted || Flag(deletion, "performed") ||
                    string.Equals(Text(deletion, "status"), "completed", StringComparison.OrdinalIgnoreCase);
            var commitCompleted = Property(report, "commit", out var commit) && commit.ValueKind == JsonValueKind.Object &&
                Text(commit, "status") is { } commitStatus &&
                (commitStatus.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                 commitStatus.Equals("completed_incomplete", StringComparison.OrdinalIgnoreCase));
            // Older successful reports may not have a terminal commit status, but an explicitly
            // recorded source deletion after passed verification is conclusive legacy evidence.
            if (!commitCompleted && !sourcesDeleted) return null;

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
            var sourcePaths = EnumerateReportedSources(report)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(relative => HostStagingService.SafeCombine(root, NormalizeRelative(relative)))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return files.All(file => File.Exists(file.FullPath) &&
                                     (file.Size is null || new FileInfo(file.FullPath).Length == file.Size.Value))
                ? new(root, reportPath, workflow!, files, sourcesDeleted, sourcePaths)
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

    public static bool HasTerminalSuccessEvidence(string albumRoot)
    {
        var reportPath = Path.Combine(Path.GetFullPath(albumRoot), "conversion-report.json");
        if (!File.Exists(reportPath)) return false;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            if (!IsCompletedWorkflow(Text(report, "workflow_mode")) ||
                !Property(report, "verification", out var verification) ||
                verification.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(verification, "status"), "passed", StringComparison.OrdinalIgnoreCase)) return false;
            var deleted = Flag(verification, "sources_deleted");
            if (Property(report, "deletion", out var deletion) && deletion.ValueKind == JsonValueKind.Object)
                deleted = deleted || Flag(deletion, "performed") ||
                    string.Equals(Text(deletion, "status"), "completed", StringComparison.OrdinalIgnoreCase);
            var committed = Property(report, "commit", out var commit) && commit.ValueKind == JsonValueKind.Object &&
                Text(commit, "status") is { } status &&
                (status.Equals("completed", StringComparison.OrdinalIgnoreCase) ||
                 status.Equals("completed_incomplete", StringComparison.OrdinalIgnoreCase));
            return committed || deleted;
        }
        catch (JsonException) { return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return false; }
    }

    public static CompletedOutputPlan? DiscoverRecoverableStaleFallback(
        string albumRoot,
        CancellationToken token = default)
    {
        var root = Path.GetFullPath(albumRoot);
        var reportPath = Path.Combine(root, "conversion-report.json");
        if (!File.Exists(reportPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(reportPath));
            var report = document.RootElement;
            if (!string.Equals(Text(report, "generated_by"), "Album Fixer host fallback", StringComparison.Ordinal) ||
                !Property(report, "pipeline", out var pipeline) || pipeline.ValueKind != JsonValueKind.Object ||
                !Property(report, "verification", out var verification) || verification.ValueKind != JsonValueKind.Object ||
                !Property(report, "commit", out var commit) || commit.ValueKind != JsonValueKind.Object ||
                !string.Equals(Text(commit, "status"), "not_completed", StringComparison.OrdinalIgnoreCase) ||
                !Property(report, "deletion", out var deletion) || deletion.ValueKind != JsonValueKind.Object ||
                Flag(deletion, "performed") ||
                !IsFailureStatus(Text(pipeline, "status")) || !IsFailureStatus(Text(verification, "status")) ||
                HasReportedSections(report))
                return null;

            token.ThrowIfCancellationRequested();
            var workflow = Text(report, "workflow_mode");
            if (IsFlacWorkflow(workflow))
                return RecoverFlacFallback(root, reportPath, report, pipeline, token);
            if (IsDsdWorkflow(workflow))
                return RecoverDsdFallback(root, reportPath, report, pipeline);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or
                                      ArgumentException or NotSupportedException or TagLib.CorruptFileException or
                                      Win32Exception or InvalidDataException or CryptographicException)
        { return null; }
    }

    private static CompletedOutputPlan? RecoverFlacFallback(
        string root,
        string reportPath,
        JsonElement report,
        JsonElement pipeline,
        CancellationToken token)
    {
        if (!TryGetFlacFallbackFiles(root, reportPath, report, out var sourcePath, out var tracks, out var cueIndexFrames) ||
            !File.Exists(sourcePath)) return null;

        if (!string.Equals(Text(pipeline, "status"), "canceled", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(pipeline, "stopped_phase"), nameof(JobPhase.CopyingIn), StringComparison.OrdinalIgnoreCase)) return null;
        var ffmpeg = new PreflightService().FindToolsAsync(token).GetAwaiter().GetResult()["ffmpeg"];
        if (ffmpeg is null || !HasExactDecodedPcmEquivalence(ffmpeg, sourcePath, tracks, cueIndexFrames, token)) return null;
        return CompletedPlan(root, reportPath, "flac_cue_split", tracks, false, [sourcePath],
            "Completion was recovered from a later canceled fallback report after recomputing per-track decoded PCM equality at the current CUE boundaries and confirming matching audio formats; tags and embedded artwork also passed.");
    }

    private static bool TryGetFlacFallbackFiles(
        string root,
        string reportPath,
        JsonElement report,
        out string sourcePath,
        out string[] tracks,
        out long[] cueIndexFrames)
    {
        sourcePath = string.Empty;
        tracks = [];
        cueIndexFrames = [];
        var sourceEntries = InventorySources(report, "FLAC image", ".flac");
        if (sourceEntries.Length != 1) return false;
        sourcePath = HostStagingService.SafeCombine(root, NormalizeRelative(sourceEntries[0].Path));

        var cues = Directory.EnumerateFiles(root, "*.cue", SearchOption.TopDirectoryOnly).ToArray();
        if (cues.Length != 1) return false;
        var cueSourceNames = new List<string>();
        var cueTracks = new List<CueRecoveryTrack>();
        CueRecoveryTrack? currentTrack = null;
        foreach (var line in File.ReadLines(cues[0]))
        {
            var fileMatch = Regex.Match(line, "^\\s*FILE\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))\\s+\\S+", RegexOptions.IgnoreCase);
            if (fileMatch.Success)
                cueSourceNames.Add(fileMatch.Groups["q"].Success ? fileMatch.Groups["q"].Value : fileMatch.Groups["u"].Value);
            var trackMatch = Regex.Match(line, "^\\s*TRACK\\s+(?<number>\\d+)\\s+(?<type>\\S+)\\s*$", RegexOptions.IgnoreCase);
            if (trackMatch.Success)
            {
                currentTrack = null;
                if (trackMatch.Groups["type"].Value.Equals("AUDIO", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(trackMatch.Groups["number"].Value, out var number))
                {
                    currentTrack = new(number);
                    cueTracks.Add(currentTrack);
                }
                continue;
            }
            var indexMatch = Regex.Match(line,
                "^\\s*INDEX\\s+01\\s+(?<minute>\\d+):(?<second>\\d+):(?<frame>\\d+)\\s*$",
                RegexOptions.IgnoreCase);
            if (indexMatch.Success && currentTrack is not null)
            {
                if (currentTrack.Index01Frames is not null ||
                    !int.TryParse(indexMatch.Groups["minute"].Value, out var minute) ||
                    !int.TryParse(indexMatch.Groups["second"].Value, out var second) ||
                    !int.TryParse(indexMatch.Groups["frame"].Value, out var frame) ||
                    second >= 60 || frame >= 75) return false;
                currentTrack.Index01Frames = checked(((long)minute * 60 + second) * 75 + frame);
            }
        }
        if (cueSourceNames.Count != 1 || cueTracks.Count < 2 ||
            !cueTracks.Select(track => track.Number).SequenceEqual(Enumerable.Range(1, cueTracks.Count)) ||
            cueTracks.Any(track => track.Index01Frames is null) ||
            !Path.GetFullPath(Path.Combine(root, cueSourceNames[0])).Equals(sourcePath, StringComparison.OrdinalIgnoreCase)) return false;
        cueIndexFrames = cueTracks.Select((track, index) => index == 0 ? 0 : track.Index01Frames!.Value).ToArray();

        var source = sourcePath;
        tracks = Directory.EnumerateFiles(root, "*.flac", SearchOption.AllDirectories)
            .Where(path => !path.Equals(source, StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tracks.Length != cueTracks.Count || tracks.Any(path =>
                !Path.GetDirectoryName(path)!.Equals(root, StringComparison.OrdinalIgnoreCase))) return false;
        var reportWritten = File.GetLastWriteTimeUtc(reportPath);
        for (var index = 0; index < tracks.Length; index++)
        {
            var expectedNumber = index + 1;
            var fileName = Path.GetFileName(tracks[index]);
            var nameMatch = Regex.Match(fileName, "^(?<number>\\d+)\\s+-\\s+.+\\.flac$", RegexOptions.IgnoreCase);
            if (!nameMatch.Success || !int.TryParse(nameMatch.Groups["number"].Value, out var number) ||
                number != expectedNumber || File.GetLastWriteTimeUtc(tracks[index]) > reportWritten.AddSeconds(2) ||
                !HasCompleteQuickFlacEvidence(tracks[index], expectedNumber, tracks.Length)) return false;
        }
        return true;
    }

    private static CompletedOutputPlan? RecoverDsdFallback(
        string root,
        string reportPath,
        JsonElement report,
        JsonElement pipeline)
    {
        if (!string.Equals(Text(pipeline, "status"), "canceled", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Text(pipeline, "stopped_phase"), nameof(JobPhase.Inventoried), StringComparison.OrdinalIgnoreCase)) return null;
        var sourceEntries = InventorySources(report, "SACD / DSD image", ".iso");
        if (sourceEntries.Length != 1) return null;
        var sourcePath = HostStagingService.SafeCombine(root, NormalizeRelative(sourceEntries[0].Path));
        if (File.Exists(sourcePath) || !HasSourceChecksumReference(root, Path.GetFileName(sourcePath))) return null;

        var layoutPath = Path.Combine(root, "sacd_extract-layout.txt");
        if (!File.Exists(layoutPath)) return null;
        var reportWritten = File.GetLastWriteTimeUtc(reportPath);
        if (File.GetLastWriteTimeUtc(layoutPath) > reportWritten.AddSeconds(2)) return null;
        var layoutText = File.ReadAllText(layoutPath);
        var sizeMatch = Regex.Match(layoutText, "Size is:\\s*(?<size>\\d+)\\s+bytes", RegexOptions.IgnoreCase);
        if (!sizeMatch.Success || !long.TryParse(sizeMatch.Groups["size"].Value, out var layoutSize) ||
            layoutSize != sourceEntries[0].Size) return null;
        var areas = ParseSacdRecoveryAreas(layoutText);
        if (areas.Count == 0) return null;

        var outputPaths = new List<string>();
        foreach (var area in areas)
        {
            var areaRoot = Path.Combine(root, area.Folder);
            if (!Directory.Exists(areaRoot)) return null;
            var tracks = Directory.EnumerateFiles(areaRoot, "*.dsf", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (tracks.Length != area.TrackTitles.Count) return null;
            var primaryLog = Path.Combine(root, $"sacd_extract-{area.Folder.ToLowerInvariant()}.log");
            var independentLog = Path.Combine(root, $"sacd_extract-{area.Folder.ToLowerInvariant()}-independent.log");
            if (!HasMatchingSuccessfulExtractionLogs(primaryLog, independentLog, tracks.Length, reportWritten)) return null;
            for (var index = 0; index < tracks.Length; index++)
            {
                var expectedNumber = index + 1;
                var nameMatch = Regex.Match(Path.GetFileName(tracks[index]), "^(?<number>\\d+)\\s+-\\s+.+\\.dsf$", RegexOptions.IgnoreCase);
                if (!nameMatch.Success || !int.TryParse(nameMatch.Groups["number"].Value, out var number) ||
                    number != expectedNumber || File.GetLastWriteTimeUtc(tracks[index]) > reportWritten.AddSeconds(2) ||
                    !HasCompleteQuickDsfEvidence(tracks[index], expectedNumber, tracks.Length, area.TrackTitles[index])) return null;
            }
            outputPaths.AddRange(tracks);
        }
        var allDsf = Directory.EnumerateFiles(root, "*.dsf", SearchOption.AllDirectories).ToArray();
        if (allDsf.Length != outputPaths.Count || allDsf.Any(path => !outputPaths.Contains(path, StringComparer.OrdinalIgnoreCase))) return null;
        return CompletedPlan(root, reportPath, "sacd_iso_extract", outputPaths.ToArray(), true, [sourcePath],
            "Completion was recovered from a later canceled fallback report using the preserved SACD layout, matching successful primary and independent extraction frame logs, the exact area/track set, native DSF properties, required tags, and embedded artwork. The already deleted ISO was not recreated or re-read.");
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

    private static IEnumerable<string> EnumerateReportedSources(JsonElement report)
    {
        if (Property(report, "discs", out var discs) && discs.ValueKind == JsonValueKind.Array)
            foreach (var disc in discs.EnumerateArray())
                if (IsSourcePath(Text(disc, "source"))) yield return Text(disc, "source")!;

        if (Property(report, "source", out var source) && source.ValueKind == JsonValueKind.Object &&
            IsSourcePath(Text(source, "file"))) yield return Text(source, "file")!;

        if (Property(report, "deletion", out var deletion) && deletion.ValueKind == JsonValueKind.Object &&
            Property(deletion, "files", out var deletionFiles) && deletionFiles.ValueKind == JsonValueKind.Array)
            foreach (var file in deletionFiles.EnumerateArray())
                if (file.ValueKind == JsonValueKind.String && IsSourcePath(file.GetString())) yield return file.GetString()!;

        if (Property(report, "sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
            foreach (var item in sources.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && IsSourcePath(Text(item, "path"))) yield return Text(item, "path")!;
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

    private static bool IsSourcePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) return false;
        return Path.GetExtension(NormalizeRelative(path)) is { } extension &&
               (AudioExtensions.Contains(extension) || extension.Equals(".iso", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsFailureStatus(string? status) =>
        status is not null && (status.Equals("failed", StringComparison.OrdinalIgnoreCase) ||
                               status.Equals("canceled", StringComparison.OrdinalIgnoreCase));

    private static bool HasReportedSections(JsonElement report) =>
        HasNonemptyArray(report, "discs") || HasNonemptyArray(report, "areas") || HasNonemptyArray(report, "audio_areas");

    private static bool HasNonemptyArray(JsonElement element, string name) =>
        Property(element, name, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() != 0;

    private static InventorySource[] InventorySources(JsonElement report, string type, string extension)
    {
        if (!Property(report, "sources", out var sources) || sources.ValueKind != JsonValueKind.Array) return [];
        return sources.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           string.Equals(Text(item, "type"), type, StringComparison.OrdinalIgnoreCase) &&
                           Property(item, "size", out var size) && size.TryGetInt64(out var bytes) && bytes > 0)
            .Select(item => new InventorySource(Text(item, "path") ?? string.Empty,
                Property(item, "size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0))
            .Where(item => IsSourcePath(item.Path) && Path.GetExtension(item.Path).Equals(extension, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompletedOutputPlan CompletedPlan(
        string root,
        string reportPath,
        string workflow,
        IReadOnlyList<string> tracks,
        bool sourcesDeleted,
        IReadOnlyList<string> sourcePaths,
        string recoveryDetail)
    {
        var outputs = tracks.Select(path => new PreviousOutputFile(
                HostStagingService.SafeRelative(root, path), path, new FileInfo(path).Length))
            .ToArray();
        return new(root, reportPath, workflow, outputs, sourcesDeleted, sourcePaths,
            RecoveredFromStaleFallback: true, RecoveryDetail: recoveryDetail);
    }

    private static bool HasExactDecodedPcmEquivalence(
        string ffmpeg,
        string source,
        IReadOnlyList<string> tracks,
        IReadOnlyList<long> cueIndexFrames,
        CancellationToken token)
    {
        if (tracks.Count == 0 || cueIndexFrames.Count != tracks.Count || cueIndexFrames[0] != 0) return false;
        var sourceFormat = PcmFormat(source);
        if (tracks.Any(track => PcmFormat(track) != sourceFormat)) return false;

        var bytesPerSampleFrame = checked(sourceFormat.Channels * 4L);
        var starts = new long[cueIndexFrames.Count];
        for (var index = 1; index < cueIndexFrames.Count; index++)
        {
            var numerator = checked(cueIndexFrames[index] * sourceFormat.SampleRate);
            if (numerator % 75 != 0) return false;
            starts[index] = checked(numerator / 75 * bytesPerSampleFrame);
            if (starts[index] <= starts[index - 1]) return false;
        }

        var sourceHashes = Enumerable.Range(0, tracks.Count)
            .Select(_ => IncrementalHash.CreateHash(HashAlgorithmName.MD5))
            .ToArray();
        try
        {
            long sourceOffset = 0;
            var sourceTrack = 0;
            var sourceBytes = ReadDecodedPcm(ffmpeg, source, (buffer, count) =>
            {
                var bufferOffset = 0;
                while (bufferOffset < count)
                {
                    while (sourceTrack + 1 < starts.Length && sourceOffset == starts[sourceTrack + 1]) sourceTrack++;
                    var boundary = sourceTrack + 1 < starts.Length ? starts[sourceTrack + 1] : long.MaxValue;
                    var take = (int)Math.Min(count - bufferOffset, boundary - sourceOffset);
                    if (take <= 0) throw new InvalidDataException("A CUE boundary does not align with the decoded FLAC stream.");
                    sourceHashes[sourceTrack].AppendData(buffer, bufferOffset, take);
                    bufferOffset += take;
                    sourceOffset = checked(sourceOffset + take);
                }
            }, token);
            if (sourceBytes <= starts[^1] || sourceBytes % bytesPerSampleFrame != 0) return false;

            for (var index = 0; index < tracks.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                using var trackHash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                var trackBytes = AppendDecodedPcm(ffmpeg, tracks[index], trackHash, token);
                var expectedBytes = index + 1 < starts.Length
                    ? starts[index + 1] - starts[index]
                    : sourceBytes - starts[index];
                if (trackBytes != expectedBytes ||
                    !CryptographicOperations.FixedTimeEquals(sourceHashes[index].GetHashAndReset(), trackHash.GetHashAndReset()))
                    return false;
            }
            return true;
        }
        finally
        {
            foreach (var hash in sourceHashes) hash.Dispose();
        }
    }

    private static PcmAudioFormat PcmFormat(string path)
    {
        using var file = TagLib.File.Create(path);
        if (!file.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio) ||
            file.Properties.AudioSampleRate <= 0 || file.Properties.AudioChannels <= 0)
            throw new InvalidDataException($"The FLAC file has no valid decoded audio format: {path}");
        return new(file.Properties.AudioSampleRate, file.Properties.AudioChannels);
    }

    private static long AppendDecodedPcm(
        string ffmpeg,
        string path,
        IncrementalHash hash,
        CancellationToken token) =>
        ReadDecodedPcm(ffmpeg, path, (buffer, count) => hash.AppendData(buffer, 0, count), token);

    private static long ReadDecodedPcm(
        string ffmpeg,
        string path,
        Action<byte[], int> consume,
        CancellationToken token)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[]
                 {
                     "-v", "error", "-nostdin", "-i", path, "-map", "0:a:0", "-vn", "-sn", "-dn",
                     "-c:a", "pcm_s32le", "-f", "s32le", "pipe:1"
                 }) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) errors.AppendLine(args.Data);
        };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new IOException($"Could not start ffmpeg for {path}.");
            process.BeginErrorReadLine();
            var buffer = new byte[1024 * 1024];
            long bytes = 0;
            int read;
            while ((read = process.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                consume(buffer, read);
                bytes = checked(bytes + read);
            }
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidDataException($"ffmpeg could not decode '{path}': {errors}");
            if (bytes == 0) throw new InvalidDataException($"Decoded audio is empty: {path}");
            return bytes;
        }
        finally
        {
            if (started && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
        }
    }

    private static IReadOnlyList<SacdRecoveryArea> ParseSacdRecoveryAreas(string layout)
    {
        var countMatch = Regex.Match(layout, "Area count:\\s*(?<count>\\d+)", RegexOptions.IgnoreCase);
        if (!countMatch.Success || !int.TryParse(countMatch.Groups["count"].Value, out var expectedCount) || expectedCount < 1) return [];
        var matches = Regex.Matches(layout,
            "Area Information\\s*\\[\\d+\\]\\s*:(?<body>.*?)(?=Area Information\\s*\\[\\d+\\]\\s*:|\\z)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (matches.Count != expectedCount) return [];
        var areas = new List<SacdRecoveryArea>(matches.Count);
        foreach (Match match in matches)
        {
            var body = match.Groups["body"].Value;
            var trackCountMatch = Regex.Match(body, "Track Count:\\s*(?<count>\\d+)", RegexOptions.IgnoreCase);
            var channelMatch = Regex.Match(body, "Speaker config:\\s*(?<channels>\\d+)\\s+Channel", RegexOptions.IgnoreCase);
            if (!trackCountMatch.Success || !int.TryParse(trackCountMatch.Groups["count"].Value, out var trackCount) || trackCount < 1 ||
                !channelMatch.Success || !int.TryParse(channelMatch.Groups["channels"].Value, out var channels)) return [];
            var folder = channels == 2 ? "Stereo" : channels > 2 ? "Multichannel" : null;
            if (folder is null || areas.Any(area => area.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))) return [];
            var titles = Regex.Matches(body, "^\\s*Title\\[(?<number>\\d+)\\]:\\s*(?<title>.+?)\\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.Multiline)
                .Cast<Match>()
                .Select(item => (Number: int.Parse(item.Groups["number"].Value), Title: item.Groups["title"].Value.Trim()))
                .ToArray();
            var firstNumber = titles.Length > 0 ? titles[0].Number : -1;
            if (titles.Length != trackCount || firstNumber is not 0 and not 1 ||
                !titles.Select(item => item.Number).SequenceEqual(Enumerable.Range(firstNumber, trackCount)) ||
                titles.Any(item => string.IsNullOrWhiteSpace(item.Title))) return [];
            areas.Add(new(folder, titles.Select(item => item.Title).ToArray()));
        }
        return areas;
    }

    private static bool HasMatchingSuccessfulExtractionLogs(
        string primaryPath,
        string independentPath,
        int trackCount,
        DateTime reportWritten)
    {
        if (!File.Exists(primaryPath) || !File.Exists(independentPath) ||
            File.GetLastWriteTimeUtc(primaryPath) > reportWritten.AddSeconds(2) ||
            File.GetLastWriteTimeUtc(independentPath) > reportWritten.AddSeconds(2)) return false;
        var primary = File.ReadAllText(primaryPath);
        var independent = File.ReadAllText(independentPath);
        if (!SuccessfulExtractionLog(primary) || !SuccessfulExtractionLog(independent)) return false;
        var primaryFrames = ExtractionFrames(primary);
        var independentFrames = ExtractionFrames(independent);
        return primaryFrames.Count == trackCount && primaryFrames.SequenceEqual(independentFrames);
    }

    private static bool SuccessfulExtractionLog(string log) =>
        log.Contains("We are done exporting DSF", StringComparison.OrdinalIgnoreCase) &&
        log.Contains("Program terminates", StringComparison.OrdinalIgnoreCase) &&
        !Regex.IsMatch(log, "\\b(error|failed|aborted|canceled)\\b", RegexOptions.IgnoreCase);

    private static IReadOnlyList<(long Processed, long Duration)> ExtractionFrames(string log) =>
        Regex.Matches(log, "Processed\\s+(?<processed>\\d+)\\s+audioframes\\.\\s+Duration specified:\\s*(?<duration>\\d+)",
                RegexOptions.IgnoreCase)
            .Cast<Match>()
            .Select(match => (long.Parse(match.Groups["processed"].Value), long.Parse(match.Groups["duration"].Value)))
            .Where(item => item.Item1 > 0 && item.Item1 == item.Item2)
            .ToArray();

    private static bool HasSourceChecksumReference(string root, string sourceName) =>
        Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => Path.GetExtension(path) is { } extension &&
                           (extension.Equals(".md5", StringComparison.OrdinalIgnoreCase) || extension.Equals(".sfv", StringComparison.OrdinalIgnoreCase)))
            .Any(path => File.ReadAllText(path).Contains(sourceName, StringComparison.OrdinalIgnoreCase));

    private static bool HasCompleteQuickFlacEvidence(string path, int trackNumber, int trackCount)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        return file.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio) &&
               file.Properties.AudioSampleRate > 0 && file.Properties.AudioChannels > 0 &&
               !string.IsNullOrWhiteSpace(tag.Title) && !string.IsNullOrWhiteSpace(tag.Album) &&
               tag.Performers.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.AlbumArtists.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.Track == trackNumber && (tag.TrackCount == 0 || tag.TrackCount == trackCount) &&
               tag.Disc > 0 && tag.Year > 0 && tag.Genres.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.Pictures.Any(picture => picture.Data.Count > 0);
    }

    private static bool HasCompleteQuickDsfEvidence(
        string path,
        int trackNumber,
        int trackCount,
        string expectedTitle)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;
        return file.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio) &&
               file.Properties.Description.Contains("DSF", StringComparison.OrdinalIgnoreCase) &&
               file.Properties.AudioSampleRate > 0 && file.Properties.AudioChannels > 0 &&
               string.Equals(tag.Title?.Trim(), expectedTitle.Trim(), StringComparison.Ordinal) &&
               !string.IsNullOrWhiteSpace(tag.Album) &&
               tag.Performers.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.AlbumArtists.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.Track == trackNumber && tag.TrackCount == trackCount &&
               tag.Disc > 0 && tag.DiscCount > 0 && tag.Year > 0 &&
               tag.Genres.Any(value => !string.IsNullOrWhiteSpace(value)) &&
               tag.Pictures.Any(picture => picture.Data.Count > 0);
    }

    private sealed record InventorySource(string Path, long Size);
    private sealed record SacdRecoveryArea(string Folder, IReadOnlyList<string> TrackTitles);
    private sealed record PcmAudioFormat(int SampleRate, int Channels);
    private sealed class CueRecoveryTrack(int number)
    {
        public int Number { get; } = number;
        public long? Index01Frames { get; set; }
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

    private static bool IsFlacWorkflow(string? workflow) =>
        workflow is not null &&
        (workflow.Equals("flac_cue_split", StringComparison.OrdinalIgnoreCase) ||
         workflow.Equals(nameof(WorkflowMode.FlacCueSplit), StringComparison.OrdinalIgnoreCase));

    private static bool IsDsdWorkflow(string? workflow) =>
        workflow is not null &&
        (workflow.Equals("sacd_iso_extract", StringComparison.OrdinalIgnoreCase) ||
         workflow.Equals(nameof(WorkflowMode.DsdExtraction), StringComparison.OrdinalIgnoreCase));

    private static bool IsCompletedWorkflow(string? workflow) =>
        IsFlacWorkflow(workflow) || IsDsdWorkflow(workflow);

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
