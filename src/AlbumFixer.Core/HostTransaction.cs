using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlbumFixer.Core;

public enum CompletionIssueKind
{
    None,
    RequiredMetadata,
    CoverArtwork,
    RequiredMetadataAndCoverArtwork
}

public static class CompletionIssuePresentation
{
    public static CompletionIssueKind FromStatus(string? status) => status?.ToLowerInvariant() switch
    {
        "required_metadata_missing" => CompletionIssueKind.RequiredMetadata,
        "cover_artwork_missing" => CompletionIssueKind.CoverArtwork,
        "required_metadata_and_cover_missing" => CompletionIssueKind.RequiredMetadataAndCoverArtwork,
        _ => CompletionIssueKind.None
    };

    public static string Status(CompletionIssueKind kind) => kind switch
    {
        CompletionIssueKind.RequiredMetadata => "required_metadata_missing",
        CompletionIssueKind.CoverArtwork => "cover_artwork_missing",
        CompletionIssueKind.RequiredMetadataAndCoverArtwork => "required_metadata_and_cover_missing",
        _ => "passed"
    };

    public static string Label(CompletionIssueKind kind) => kind switch
    {
        CompletionIssueKind.RequiredMetadata => "Required metadata missing",
        CompletionIssueKind.CoverArtwork => "Cover artwork missing",
        CompletionIssueKind.RequiredMetadataAndCoverArtwork => "Metadata & cover missing",
        _ => "Complete"
    };

    public static string Description(CompletionIssueKind kind) => kind switch
    {
        CompletionIssueKind.RequiredMetadata => "required metadata is missing",
        CompletionIssueKind.CoverArtwork => "cover artwork is missing",
        CompletionIssueKind.RequiredMetadataAndCoverArtwork => "required metadata and cover artwork are missing",
        _ => "all required completion evidence is present"
    };

    public static bool IsIncompleteStatus(string status) =>
        status.Equals("incomplete", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("required_metadata_missing", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("cover_artwork_missing", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("required_metadata_and_cover_missing", StringComparison.OrdinalIgnoreCase);
}

public sealed record StagedSource(string RelativePath, long Size);
public sealed record StagedJob(
    string JobDirectory,
    string AlbumRoot,
    string FfmpegPath,
    string FfprobePath,
    string ManifestPath,
    IReadOnlyList<StagedSource> Sources,
    PreviousOutputCleanupResult? PreviousOutputCleanup = null,
    VerifiedOutputPlan? PreviousVerifiedOutput = null,
    string SacdExtractPath = "",
    BatchPipelineLimits? PipelineLimits = null,
    BatchPipelineTelemetry? PipelineTelemetry = null,
    string? SourceAlbumRoot = null,
    bool SourceCacheUsed = true)
{
    public string InputAlbumRoot => SourceAlbumRoot ?? AlbumRoot;
}
public sealed record HostCommitResult(
    string ReportPath,
    int Tracks,
    bool SourcesDeleted,
    bool Incomplete = false,
    CompletionIssueKind IncompleteKind = CompletionIssueKind.None);

public sealed class HostStagingService
{
    private const int BufferSize = 1024 * 1024;

    public async Task<StagedJob> StageAsync(
        ScanResult scan,
        PreflightResult preflight,
        string jobDirectory,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token = default)
    {
        ValidateJobDirectory(jobDirectory, preflight.TempRoot);
        var abandonedCleanup = await WorkflowCleanupService.CleanupDestinationStagesAsync(scan.AlbumRoot);
        if (!abandonedCleanup.Completed)
            throw new IOException($"Could not remove abandoned Album Fixer destination staging: {string.Join(", ", abandonedCleanup.RemainingPaths)}");
        var albumRoot = Path.Combine(jobDirectory, "album");
        var toolsRoot = Path.Combine(jobDirectory, "tools");
        var sourceCacheUsed = RequiresSourceCache(scan.AlbumRoot);
        var inputAlbumRoot = sourceCacheUsed ? albumRoot : scan.AlbumRoot;
        Directory.CreateDirectory(albumRoot);
        Directory.CreateDirectory(toolsRoot);

        progress.Report(Snapshot(JobPhase.Inventoried, 1, "Album inventory is complete. The original is unchanged."));
        var previousOutputCleanup = PreviousOutputCleanupService.Cleanup(scan.AlbumRoot, token);
        if (previousOutputCleanup is not null)
            progress.Report(Snapshot(JobPhase.Inventoried, 1,
                $"Removed {previousOutputCleanup.DeletedFiles} report-proven track{(previousOutputCleanup.DeletedFiles == 1 ? "" : "s")} from an incomplete earlier run and archived its report."));
        var previousVerifiedOutput = PreviousOutputCleanupService.DiscoverVerified(scan.AlbumRoot)
            ?? PreviousOutputCleanupService.DiscoverArchivedDsdArtifacts(scan.AlbumRoot);
        PreviousOutputCleanupService.VerifyDirectFileSizes(previousVerifiedOutput, token);
        var directPreviousFiles = PreviousOutputCleanupService.DirectFiles(previousVerifiedOutput);
        var directPreviousAudio = PreviousOutputCleanupService.DirectFiles(previousVerifiedOutput)
            .Where(file => Path.GetExtension(file.RelativePath).Equals(".flac", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (directPreviousAudio.Count > 0)
            progress.Report(Snapshot(JobPhase.Inventoried, 1,
                $"Verified {directPreviousAudio.Count} prior root track{(directPreviousAudio.Count == 1 ? "" : "s")} for replacement after the new output passes."));
        var directPreviousArtifacts = directPreviousFiles.Count - directPreviousAudio.Count;
        if (directPreviousArtifacts > 0)
            progress.Report(Snapshot(JobPhase.Inventoried, 1,
                $"Verified {directPreviousArtifacts} archived-report-proven SACD provenance artifact{(directPreviousArtifacts == 1 ? "" : "s")} for transactional replacement after the new extraction passes."));

        var sourceFiles = scan.Media
            .Where(IsSource)
            .Select(item => item.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourceFiles.Length == 0) throw new InvalidOperationException("No exact source file was identified for verified staging.");

        var sourceSizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sourceFiles)
        {
            token.ThrowIfCancellationRequested();
            sourceSizes[source] = new FileInfo(source).Length;
        }

        if (sourceCacheUsed)
        {
            var albumFiles = EnumerateTree(scan.AlbumRoot)
                .Where(path => !Path.GetRelativePath(scan.AlbumRoot, path).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase))
                .Where(path => !Path.GetFileName(path).Equals(AlbumTransactionLock.FileName, StringComparison.OrdinalIgnoreCase))
                .Where(path => !directPreviousAudio.Contains(path))
                .ToArray();
            var totalBytes = Math.Max(1L, albumFiles.Sum(path => new FileInfo(path).Length));
            long copiedBytes = 0;
            var lastCopyPercent = -1;
            progress.Report(Snapshot(JobPhase.CopyingIn, 2, "Copying the network album into the Windows Temp source cache."));
            foreach (var source in albumFiles)
            {
                token.ThrowIfCancellationRequested();
                var relative = SafeRelative(scan.AlbumRoot, source);
                var destination = SafeCombine(albumRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await CopyFileAsync(source, destination, bytes =>
                {
                    copiedBytes += bytes;
                    var percent = 2 + (int)Math.Min(13, copiedBytes * 13 / totalBytes);
                    if (percent != lastCopyPercent)
                    {
                        lastCopyPercent = percent;
                        progress.Report(Snapshot(JobPhase.CopyingIn, percent, $"Caching locally: {relative}"));
                    }
                }, token);
            }
        }
        else
        {
            progress.Report(Snapshot(JobPhase.CopyingIn, 15,
                "The source is on a fixed local disk; no source files were copied into the Windows Temp cache."));
        }

        var stagedSources = new List<StagedSource>();
        foreach (var source in sourceFiles)
        {
            token.ThrowIfCancellationRequested();
            var relative = SafeRelative(scan.AlbumRoot, source);
            var size = sourceSizes[source];
            if (sourceCacheUsed)
            {
                var localPath = SafeCombine(albumRoot, relative);
                if (new FileInfo(localPath).Length != size)
                    throw new IOException($"The local copy of '{relative}' does not match the original file size. The original was retained.");
            }
            stagedSources.Add(new(relative, size));
        }
        progress.Report(Snapshot(JobPhase.SourceCopyVerified, 17, sourceCacheUsed
            ? "The Windows Temp source cache matches the original file size."
            : "The fixed-disk source will be read in place; its original file size was recorded."));

        var ffprobe = RequireTool(preflight, "ffprobe");
        var ffmpeg = scan.HasFlac || scan.HasDsd ? RequireTool(preflight, "ffmpeg") : string.Empty;
        var sacdExtract = scan.Mode == WorkflowMode.DsdExtraction ? RequireTool(preflight, "sacd_extract") : string.Empty;
        var stagedFfmpeg = ffmpeg.Length == 0 ? string.Empty : Path.Combine(toolsRoot, "ffmpeg.exe");
        var stagedFfprobe = Path.Combine(toolsRoot, "ffprobe.exe");
        var stagedSacdExtract = sacdExtract.Length == 0 ? string.Empty : Path.Combine(toolsRoot, "sacd_extract.exe");
        if (ffmpeg.Length > 0) await CopyFileAsync(ffmpeg, stagedFfmpeg, null, token);
        await CopyFileAsync(ffprobe, stagedFfprobe, null, token);
        if (sacdExtract.Length > 0)
        {
            await CopyFileAsync(sacdExtract, stagedSacdExtract, null, token);
            await File.WriteAllTextAsync(Path.Combine(toolsRoot, "sacd_extract.cfg"),
                "artist=0\nperformer=0\npauses=0\nnopad=0\nconcatenate=0\nlogging=0\nid3tag=0\n",
                new UTF8Encoding(false), token);
        }

        var manifestPath = Path.Combine(jobDirectory, "host-manifest.json");
        var manifest = new
        {
            schema_version = "1.0",
            job_id = Path.GetFileName(jobDirectory),
            original_album_root = scan.AlbumRoot,
            staged_album_root = albumRoot,
            source_album_root = inputAlbumRoot,
            source_cache_used = sourceCacheUsed,
            created_at_utc = DateTimeOffset.UtcNow,
            previous_output_cleanup = previousOutputCleanup is null ? null : new
            {
                deleted_files = previousOutputCleanup.DeletedRelativePaths,
                archived_report = previousOutputCleanup.ArchivedReportPath,
                status = "completed"
            },
            previous_output_replacement = previousVerifiedOutput is null ? null : new
            {
                retained_inner_files = previousVerifiedOutput.Files.Count - directPreviousFiles.Count,
                replaceable_root_files = directPreviousFiles.Select(file => file.RelativePath).OrderBy(path => path).ToArray(),
                status = "pending_final_verification"
            },
            sources = stagedSources.Select(source => new
            {
                path = source.RelativePath,
                size = source.Size,
                staged_size = sourceCacheUsed ? source.Size : (long?)null,
                copy_in_status = sourceCacheUsed ? "size_verified" : "not_required_local_fixed_disk"
            })
        };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false), token);

        return new(jobDirectory, albumRoot, stagedFfmpeg, stagedFfprobe, manifestPath, stagedSources,
            previousOutputCleanup, previousVerifiedOutput, stagedSacdExtract,
            SourceAlbumRoot: inputAlbumRoot, SourceCacheUsed: sourceCacheUsed);
    }

    public static bool RequiresSourceCache(string albumRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(albumRoot);
            if (fullPath.StartsWith(@"\\", StringComparison.OrdinalIgnoreCase)) return true;
            var root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root) || new DriveInfo(root).DriveType != DriveType.Fixed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    public static void ValidateJobDirectory(string jobDirectory, string tempRoot)
    {
        var root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var job = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!job.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The local job directory is outside the approved Album Fixer Temp root.");
    }

    internal static string SafeCombine(string root, string relative)
    {
        var rootPrefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(rootPrefix, relative));
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unsafe path outside the transaction root: {relative}");
        return path;
    }

    internal static string SafeRelative(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        if (Path.IsPathRooted(relative) || relative.Equals("..", StringComparison.Ordinal) || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Path is outside the transaction root: {path}");
        return relative;
    }

    private static IEnumerable<string> EnumerateTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var info = new DirectoryInfo(child);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse points are not allowed in an album transaction: {child}");
                if (!info.Name.StartsWith(".album-fixer-stage-", StringComparison.OrdinalIgnoreCase)) pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"Reparse-point files are not allowed in an album transaction: {file}");
                yield return file;
            }
        }
    }

    internal static async Task CopyFileAsync(string source, string destination, Action<int>? copied, CancellationToken token)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await input.ReadAsync(buffer, token)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            copied?.Invoke(read);
        }
        await output.FlushAsync(token);
    }

    public static bool IsSource(MediaItem item) =>
        item.Kind.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase) ||
        item.Kind is "DST stream" or "Raw DSD";

    private static string RequireTool(PreflightResult preflight, string name) =>
        preflight.Tools.TryGetValue(name, out var path) && path is not null && File.Exists(path)
            ? path
            : throw new FileNotFoundException($"The required {name} tool is unavailable.");

    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) =>
        new(phase, percent, "running", detail, DateTimeOffset.UtcNow);
}

public sealed class HostCommitService
{
    public Task<HostCommitResult> CommitAsync(
        ScanResult scan,
        StagedJob staged,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token = default) => CommitAsync(scan, staged, progress, deleteOriginals: true, token);

    public async Task<HostCommitResult> CommitAsync(
        ScanResult scan,
        StagedJob staged,
        IProgress<ProgressSnapshot> progress,
        bool deleteOriginals,
        CancellationToken token = default)
    {
        try
        {
            var result = await CommitCoreAsync(scan, staged, progress, deleteOriginals, token);
            var cleanup = await WorkflowCleanupService.CleanupDestinationStagesAsync(scan.AlbumRoot);
            if (!cleanup.Completed)
                throw new IOException($"Could not remove Album Fixer destination staging: {string.Join(", ", cleanup.RemainingPaths)}");
            return result;
        }
        catch (Exception error)
        {
            if (error is not RepairRollbackException)
                await WorkflowCleanupService.CleanupDestinationStagesAsync(scan.AlbumRoot);
            throw;
        }
    }

    private static async Task<HostCommitResult> CommitCoreAsync(
        ScanResult scan,
        StagedJob staged,
        IProgress<ProgressSnapshot> progress,
        bool deleteOriginals,
        CancellationToken token)
    {
        if (scan.Mode is not WorkflowMode.FlacCueSplit and not WorkflowMode.DsdExtraction and not WorkflowMode.ExistingTrackRepair)
            throw new NotSupportedException("Verified host write-back is available for FLAC + CUE, SACD ISO, and standalone existing-FLAC/DSF/DFF repair workflows only. Every original was retained.");
        var isDsd = scan.Mode == WorkflowMode.DsdExtraction;
        var isRepair = scan.Mode == WorkflowMode.ExistingTrackRepair;
        if (!staged.SourceCacheUsed || isRepair)
            VerifyOriginalSourceSizesUnchanged(scan, staged, token);

        var localReportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        if (!File.Exists(localReportPath)) throw new FileNotFoundException("The local processor did not create the required conversion report.", localReportPath);

        var report = JsonNode.Parse(await File.ReadAllTextAsync(localReportPath, token)) as JsonObject
            ?? throw new JsonException("The local conversion report is not a JSON object.");
        var repairFormat = isRepair ? report["format"]?.GetValue<string>()?.ToLowerInvariant() : null;
        if (isRepair && repairFormat is not ("flac" or "dsf" or "dff"))
            throw new JsonException("The existing-track repair report has an unsupported format.");
        var repairExtension = $".{repairFormat ?? "flac"}";
        var repairFormatLabel = (repairFormat ?? "flac").ToUpperInvariant();
        StagedSource? retainedRepairIsoSource = null;
        var outputs = isDsd
            ? NormalizeAndCollectDsdOutputs(report, staged.AlbumRoot)
            : NormalizeAndCollectOutputs(report, staged.AlbumRoot);
        if (outputs.Count == 0) throw new InvalidOperationException("The conversion report contains no playback tracks to commit.");
        PreviousOutputCleanupService.VerifyDirectFileSizes(staged.PreviousVerifiedOutput, token);
        var replacementFiles = PreviousOutputCleanupService.DirectFiles(staged.PreviousVerifiedOutput);
        var replacementPaths = replacementFiles
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputPaths = outputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> repairDeduplicatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (isRepair)
        {
            var repairSources = scan.Media
                .Where(item => item.Kind is "Existing FLAC" or "Existing DSF" or "Existing DFF")
                .Select(item => item.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            repairDeduplicatedPaths = await ValidateRepairDeduplicationAsync(
                report, scan.AlbumRoot, repairSources, outputPaths, token);
            var omittedSources = repairSources.Where(path => !outputPaths.Contains(path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var retainedDsdIso = repairFormat is "dsf" or "dff" && scan.ImageCount == 1 &&
                                 scan.Media.Count(item => item.Kind == "SACD / DSD image") == 1;
            if (retainedDsdIso)
            {
                var retainedIsoPath = scan.Media.Single(item => item.Kind == "SACD / DSD image").RelativePath;
                retainedRepairIsoSource = staged.Sources.SingleOrDefault(source =>
                    source.RelativePath.Equals(retainedIsoPath, StringComparison.OrdinalIgnoreCase))
                    ?? throw new IOException("The retained SACD ISO is missing from the immutable staging manifest. Every original was retained.");
            }
            if (scan.ImageCount != 0 && !retainedDsdIso || outputPaths.Count < 2 ||
                !outputPaths.IsSubsetOf(repairSources) || !omittedSources.SetEquals(repairDeduplicatedPaths))
                throw new IOException("The repair output set does not exactly match the same-format standalone tracks admitted by inventory. Every original was retained.");
            replacementPaths.UnionWith(repairSources);
        }
        var unmatchedReplacements = replacementPaths
            .Where(path => !outputPaths.Contains(path) && !repairDeduplicatedPaths.Contains(path))
            .ToArray();
        if (unmatchedReplacements.Length > 0)
            throw new IOException($"The new split no longer produces every report-proven root output: {string.Join(", ", unmatchedReplacements)}. The prior tracks were retained.");
        var localPlayback = await VerifyPlaybackFilesAsync(outputs, staged.AlbumRoot, staged, report, isDsd, isRepair, token);
        var localArtworkIssues = isDsd ? [] : ArtworkIssues(report, localPlayback.ArtworkIssues);
        var localIncompleteIssues = IncompleteIssues(report, localArtworkIssues);
        var incomplete = localIncompleteIssues.Count > 0;
        var localIncompleteKind = CompletionIssue(localIncompleteIssues);
        var retainedRepairIsoDeletionRequested = isRepair && deleteOriginals && retainedRepairIsoSource is not null && !incomplete;
        if (isDsd) ConfirmDsdVerification(report, "local staging", sourceDeletionRequested: true);
        else if (isRepair) SetRepairVerification(report, "local staging", localIncompleteIssues, retainedRepairIsoDeletionRequested);
        else SetQuickVerification(report, "local staging", staged.Sources.Count == 1 && !incomplete, localIncompleteIssues);
        await AtomicWriteAsync(localReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        progress.Report(new(JobPhase.LocalVerificationPassed, 50,
            incomplete ? CompletionIssuePresentation.Status(localIncompleteKind) : "running", isDsd
            ? incomplete
                ? "Independent SACD extraction size and DSD structure checks passed locally; unresolved metadata will be delivered as incomplete work."
                : "Independent SACD extraction size, DSF/DSD, tag, and artwork checks passed locally."
            : isRepair
                ? incomplete
                    ? $"Exact {repairFormatLabel} audio-payload equality and tag checks passed locally; artwork remains incomplete."
                    : $"Exact {repairFormatLabel} audio-payload equality, tag, and artwork checks passed locally."
            : incomplete
                ? "Local FLAC and tag checks passed; front-cover artwork is deferred and the original source will be retained."
                : "Quick local FLAC, tag, and artwork checks passed. Full PCM comparison was skipped.", DateTimeOffset.UtcNow));
        var jobId = Path.GetFileName(staged.JobDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var networkStage = WorkflowCleanupService.DestinationStagePath(scan.AlbumRoot, staged.JobDirectory);
        if (Directory.Exists(networkStage) || File.Exists(networkStage)) throw new IOException($"A destination staging path already exists: {networkStage}");
        Directory.CreateDirectory(networkStage);

        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        progress.Report(Snapshot(JobPhase.CopyingBack, 58, "Copying verified tracks and provenance to a private destination-side staging folder."));
        foreach (var relative in outputs)
        {
            token.ThrowIfCancellationRequested();
            var local = HostStagingService.SafeCombine(staged.AlbumRoot, relative);
            if (!File.Exists(local)) throw new FileNotFoundException($"A report-listed output is missing: {relative}", local);
            var final = HostStagingService.SafeCombine(scan.AlbumRoot, relative);
            if ((File.Exists(final) || Directory.Exists(final)) && !replacementPaths.Contains(relative))
                throw new IOException($"The final path already exists and is not a report-proven prior output: {relative}");
            var network = HostStagingService.SafeCombine(networkStage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(network)!);
            await HostStagingService.CopyFileAsync(local, network, null, token);
            var localSize = new FileInfo(local).Length;
            var networkSize = new FileInfo(network).Length;
            if (localSize != networkSize)
                throw new IOException($"Destination-side file size differs for '{relative}'. The original was retained.");
            sizes[relative] = localSize;
        }
        progress.Report(Snapshot(JobPhase.DestinationSizesVerified, 68, "Every destination-side staging file matches its local file size."));

        var existingReport = Path.Combine(scan.AlbumRoot, "conversion-report.json");
        var previousReport = Path.Combine(networkStage, ".previous-conversion-report.json");
        if (File.Exists(existingReport)) File.Copy(existingReport, previousReport, overwrite: false);

        progress.Report(Snapshot(JobPhase.FinalCommit, 76, replacementPaths.Count == 0
            ? "Committing verified files to previously unoccupied final paths."
            : "Replacing report-proven prior root outputs through the rollback staging area."));
        var moved = new List<string>();
        var rolledBack = new List<string>();
        var rollbackRoot = HostStagingService.SafeCombine(networkStage, ".previous-output-rollback");
        JsonObject commit;
        JsonObject job;
        try
        {
            foreach (var replacement in replacementPaths)
            {
                var final = HostStagingService.SafeCombine(scan.AlbumRoot, replacement);
                if (!File.Exists(final)) continue;
                var rollback = HostStagingService.SafeCombine(rollbackRoot, replacement);
                Directory.CreateDirectory(Path.GetDirectoryName(rollback)!);
                File.Move(final, rollback, overwrite: false);
                rolledBack.Add(replacement);
            }
            foreach (var relative in outputs)
            {
                var network = HostStagingService.SafeCombine(networkStage, relative);
                var final = HostStagingService.SafeCombine(scan.AlbumRoot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(final)!);
                File.Move(network, final, overwrite: false);
                moved.Add(relative);
            }

            report["album_root"] = scan.AlbumRoot;
            commit = report["commit"] as JsonObject ?? new JsonObject();
            report["commit"] = commit;
            commit["status"] = "committed";
            commit["network_side_staging"] = networkStage;
            commit["destination_sizes_verified"] = true;
            commit["committed_at_utc"] = DateTimeOffset.UtcNow;
            commit["files"] = new JsonArray(sizes.Select(pair => (JsonNode)new JsonObject
            {
                ["file"] = pair.Key,
                ["size"] = pair.Value
            }).ToArray());
            if (rolledBack.Count > 0)
                commit["replaced_previous_outputs"] = new JsonArray(rolledBack.Select(path => JsonValue.Create(path)).ToArray());
            job = report["job"] as JsonObject ?? new JsonObject();
            report["job"] = job;
            job["identifier"] = jobId;
            job["original_album_root"] = scan.AlbumRoot;
            job["local_staging_path"] = staged.JobDirectory;
            job["host_copy_in_manifest"] = staged.ManifestPath;
            job["source_cache_used"] = staged.SourceCacheUsed;
            job["source_input_mode"] = staged.SourceCacheUsed ? "size_checked_temp_cache" : "local_fixed_disk_in_place";
            job["copy_in_status"] = staged.SourceCacheUsed ? "size_verified" : "not_required_local_fixed_disk";
            job["copy_back_status"] = "size_verified";
            if (staged.PipelineLimits is { } limits)
            {
                var telemetry = staged.PipelineTelemetry ?? new BatchPipelineTelemetry(0, 0, 0, 0);
                report["pipeline"] = new JsonObject
                {
                    ["scheduler"] = "bounded_stage_aware",
                    ["configured"] = new JsonObject
                    {
                        ["maximum_in_flight"] = limits.MaxInFlight,
                        ["copy_in_workers"] = limits.CopyInWorkers,
                        ["processing_workers"] = limits.ProcessingWorkers,
                        ["sacd_processing_workers"] = limits.DsdProcessingWorkers,
                        ["copy_back_workers"] = limits.CopyBackWorkers
                    },
                    ["observed_at_commit"] = new JsonObject
                    {
                        ["copy_in_workers"] = telemetry.MaximumCopyIn,
                        ["processing_workers"] = telemetry.MaximumProcessing,
                        ["sacd_processing_workers"] = telemetry.MaximumDsdProcessing,
                        ["copy_back_workers"] = telemetry.MaximumCopyBack
                    }
                };
            }
            await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        }
        catch
        {
            foreach (var relative in moved.AsEnumerable().Reverse())
            {
                try
                {
                    var final = HostStagingService.SafeCombine(scan.AlbumRoot, relative);
                    var network = HostStagingService.SafeCombine(networkStage, relative);
                    if (File.Exists(final) && !File.Exists(network)) File.Move(final, network, overwrite: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            foreach (var relative in rolledBack.AsEnumerable().Reverse())
            {
                try
                {
                    var final = HostStagingService.SafeCombine(scan.AlbumRoot, relative);
                    var rollback = HostStagingService.SafeCombine(rollbackRoot, relative);
                    if (!File.Exists(final) && File.Exists(rollback)) File.Move(rollback, final, overwrite: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            try
            {
                if (File.Exists(previousReport)) File.Copy(previousReport, existingReport, overwrite: true);
                else if (File.Exists(existingReport)) File.Delete(existingReport);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            throw;
        }

        PlaybackVerification finalPlayback;
        try
        {
            token.ThrowIfCancellationRequested();
            finalPlayback = await VerifyPlaybackFilesAsync(outputs, scan.AlbumRoot, staged, report, isDsd, isRepair, token);
        }
        catch (Exception verificationError) when (isRepair)
        {
            try
            {
                RestoreReplacementRollback(moved, rolledBack, scan.AlbumRoot, networkStage, rollbackRoot, existingReport, previousReport);
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                throw new RepairRollbackException(
                    $"Final repair verification failed and automatic rollback was incomplete. The destination staging folder was preserved for recovery: {networkStage}",
                    new AggregateException(verificationError, rollbackError));
            }
            throw;
        }
        var finalArtworkIssues = isDsd ? [] : ArtworkIssues(report, finalPlayback.ArtworkIssues);
        var finalIncompleteIssues = IncompleteIssues(report, finalArtworkIssues);
        incomplete = finalIncompleteIssues.Count > 0;
        var finalIncompleteKind = CompletionIssue(finalIncompleteIssues);
        var rollbackCleaned = rolledBack.Count == 0 || TryDeleteDirectory(rollbackRoot);
        if (rolledBack.Count > 0)
        {
            commit["previous_output_rollback_cleaned"] = rollbackCleaned;
            await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        }
        var deletesRetainedRepairIso = isRepair && deleteOriginals && !incomplete && retainedRepairIsoSource is not null;
        var deletesSource = deletesRetainedRepairIso ||
                            !isRepair && deleteOriginals && !incomplete && staged.Sources.Count == 1 && (!isDsd || DsdDeletionEligible(report));
        var deletesSacdIso = isDsd || deletesRetainedRepairIso;
        if (isDsd) ConfirmDsdVerification(report, "final album path", deletesSource);
        else if (isRepair) SetRepairVerification(report, "final album path", finalIncompleteIssues, deletesRetainedRepairIso);
        else SetQuickVerification(report, "final album path", deletesSource, finalIncompleteIssues);
        token.ThrowIfCancellationRequested();
        IReadOnlyList<DeletionTarget> deletionTargets = deletesSource
            ? deletesRetainedRepairIso
                ? ResolveRetainedRepairIsoDeletionTarget(scan, retainedRepairIsoSource!, token)
                : ResolveDeletionTargets(scan, staged, isDsd, token)
            : [];
        var reportedDeletionFiles = deletionTargets.Count > 0
            ? deletionTargets.Select(target => target.RelativePath)
            : retainedRepairIsoSource is not null
                ? [retainedRepairIsoSource.RelativePath]
                : staged.Sources.Select(source => source.RelativePath);
        var deletion = new JsonObject
        {
            ["status"] = deletesSource ? "pending" : "retained",
            ["policy"] = deletesRetainedRepairIso
                ? "verified_retained_sacd_iso_deletion_after_existing_dsd_track_repair"
                : isRepair && retainedRepairIsoSource is not null
                ? !deleteOriginals
                    ? "retained_sacd_iso_by_user_request_after_existing_dsd_track_repair"
                    : "retained_sacd_iso_because_existing_dsd_track_repair_is_incomplete"
                : isRepair
                ? "transactional_existing_track_replacement_without_source_deletion"
                : deletesSource
                ? isDsd ? "sacd_independent_extraction_size_and_structure_checks" : "user_requested_size_and_quick_checks_without_pcm_equivalence"
                : deleteOriginals ? "source_retained_without_complete_deletion_authorization" : "source_retained_by_user_request",
            ["authorized_after"] = deletesSource ? deletesSacdIso ? "full_native_dsd_payload_tag_artwork_and_final_path_verification" : "quick_final_path_checks" : null,
            ["files"] = new JsonArray(reportedDeletionFiles.Select(path => JsonValue.Create(path)).ToArray()),
            ["performed"] = false
        };
        if (!deletesSource) deletion["reason"] = isRepair
            ? retainedRepairIsoSource is not null && !deleteOriginals
                ? "The user chose to retain the coexisting SACD ISO. Existing DSD tracks were replaced transactionally after exact native-audio-payload verification."
                : retainedRepairIsoSource is not null && incomplete
                    ? $"The repaired DSD tracks passed native-audio-payload checks, but {CompletionIssuePresentation.Description(finalIncompleteKind)}; the retained SACD ISO was not deleted."
                    : repairDeduplicatedPaths.Count > 0
                ? $"Existing tracks were replaced transactionally after exact compressed-audio payload verification; {repairDeduplicatedPaths.Count} byte-identical duplicate filename entr{(repairDeduplicatedPaths.Count == 1 ? "y was" : "ies were")} removed as part of the verified replacement set."
                : "Existing tracks were replaced transactionally after exact compressed-audio payload verification; source deletion does not apply."
            : !deleteOriginals
            ? "The user chose to retain original sources."
            : isDsd
            ? incomplete
                ? $"The DSF tracks passed extraction-size and structure verification, but {CompletionIssuePresentation.Description(finalIncompleteKind)}; the original SACD ISO was retained."
                : "The SACD report did not prove every independent-extraction size and DSD structure gate."
            : incomplete
                ? $"Completion remains incomplete because {CompletionIssuePresentation.Description(finalIncompleteKind)}; the original FLAC image was retained for later repair."
                : "Automatic deletion confirmation covers one exact FLAC image only.";
        report["deletion"] = deletion;
        await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        progress.Report(new(JobPhase.FinalVerificationPassed, 90,
            incomplete ? CompletionIssuePresentation.Status(finalIncompleteKind) : "running", isDsd
            ? incomplete
                ? "Final DSF/DSD structure, report-path, and file-size checks passed; unresolved metadata remains marked incomplete."
                : "Final DSF/DSD, tag, artwork, report-path, and file-size checks passed."
            : isRepair
                ? incomplete
                    ? $"Final {repairFormatLabel} audio-payload and tag checks passed; artwork remains incomplete."
                    : $"Final {repairFormatLabel} audio-payload, tag, artwork, report-path, and file-size checks passed."
            : incomplete
                ? "Final FLAC, tag, and file-size checks passed; artwork remains deferred."
                : "Quick final FLAC, tag, artwork, and file-size checks passed.", DateTimeOffset.UtcNow));

        var deleted = false;
        if (deletesSource)
        {
            progress.Report(Snapshot(JobPhase.SourceDisposition, 94, deletesSacdIso
                ? "Deleting the exact inventoried SACD ISO after independent extraction and final DSD verification."
                : "Deleting the exact inventoried FLAC image as requested; PCM/MD5 comparison was skipped."));
            try
            {
                foreach (var target in deletionTargets) File.Delete(target.FullPath);
                if (deletionTargets.Any(target => File.Exists(target.FullPath)))
                    throw new IOException("The inventoried source image still exists after deletion.");
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                deletion["status"] = "failed";
                deletion["performed"] = false;
                deletion["error"] = error.Message;
                var verification = report["verification"] as JsonObject ?? new JsonObject();
                report["verification"] = verification;
                verification["status"] = "failed";
                verification["sources_deleted"] = false;
                verification["errors"] = new JsonArray(JsonValue.Create($"Tracks passed quick checks, but source deletion failed: {error.Message}"));
                try { await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None); }
                catch (Exception reportError) when (reportError is IOException or UnauthorizedAccessException) { }
                throw new IOException($"Tracks were committed, but the original {(deletesSacdIso ? "SACD ISO" : "FLAC image")} could not be deleted. Review the report and source path.", error);
            }

            deleted = true;
            deletion["status"] = "completed";
            deletion["performed"] = true;
            deletion["completed_at_utc"] = DateTimeOffset.UtcNow;
            var finalVerification = report["verification"] as JsonObject ?? new JsonObject();
            report["verification"] = finalVerification;
            finalVerification["sources_deleted"] = true;
            progress.Report(Snapshot(JobPhase.SourceDisposition, 96, deletesSacdIso
                ? "The exact inventoried SACD ISO was deleted after every DSD verification gate passed."
                : "The exact inventoried FLAC image was deleted after quick final checks."));
        }
        else
        {
            progress.Report(Snapshot(JobPhase.SourceDisposition, 96,
                isRepair
                    ? repairDeduplicatedPaths.Count > 0
                        ? $"Transactionally replaced {outputs.Count(path => Path.GetExtension(path).Equals(repairExtension, StringComparison.OrdinalIgnoreCase))} existing {repairFormatLabel} tracks and removed {repairDeduplicatedPaths.Count} byte-identical duplicate filename entr{(repairDeduplicatedPaths.Count == 1 ? "y" : "ies")}."
                        : $"Transactionally replaced {outputs.Count(path => Path.GetExtension(path).Equals(repairExtension, StringComparison.OrdinalIgnoreCase))} existing {repairFormatLabel} tracks; source deletion does not apply."
                : !deleteOriginals
                    ? $"All {staged.Sources.Count} original source image{(staged.Sources.Count == 1 ? " was" : "s were")} retained as requested."
                    : $"All {staged.Sources.Count} original source image{(staged.Sources.Count == 1 ? " was" : "s were")} retained; deletion was not authorized."));
        }

        await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        var networkCleaned = TryDeleteDirectory(networkStage);
        var localCleaned = await WorkflowCleanupService.CleanupLocalJobAsync(
            staged.JobDirectory,
            Path.Combine(Path.GetTempPath(), "album-fixer"));
        try
        {
            commit = report["commit"] as JsonObject ?? new JsonObject();
            report["commit"] = commit;
            commit["status"] = incomplete ? "completed_incomplete" : "completed";
            commit["final_path_verification"] = incomplete ? "passed_with_incomplete_metadata_or_artwork" : "passed";
            commit["completed_at_utc"] = DateTimeOffset.UtcNow;
            commit["network_side_staging"] = networkCleaned ? null : networkStage;
            job = report["job"] as JsonObject ?? new JsonObject();
            report["job"] = job;
            job["local_staging_cleaned"] = localCleaned;
            job["network_staging_cleaned"] = networkCleaned;
            await AtomicWriteAsync(existingReport, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Final report enrichment is best effort after the quick final checks pass.
        }
        var disposition = isRepair
            ? deleted
                ? $"{outputs.Count(path => Path.GetExtension(path).Equals(repairExtension, StringComparison.OrdinalIgnoreCase))} existing {repairFormatLabel} tracks were transactionally replaced and the retained SACD ISO was deleted"
                : repairDeduplicatedPaths.Count > 0
                ? $"{outputs.Count(path => Path.GetExtension(path).Equals(repairExtension, StringComparison.OrdinalIgnoreCase))} existing {repairFormatLabel} tracks were transactionally replaced and {repairDeduplicatedPaths.Count} byte-identical duplicate filename entr{(repairDeduplicatedPaths.Count == 1 ? "y was" : "ies were")} removed"
                : $"{outputs.Count(path => Path.GetExtension(path).Equals(repairExtension, StringComparison.OrdinalIgnoreCase))} existing {repairFormatLabel} tracks were transactionally replaced without source deletion"
            : deleted
            ? $"the original {(isDsd ? "SACD ISO" : "FLAC image")} was deleted"
            : $"all {staged.Sources.Count} original source image{(staged.Sources.Count == 1 ? " was" : "s were")} retained";
        var cleanupDetail = incomplete
            ? $"Tracks were delivered, but {CompletionIssuePresentation.Description(finalIncompleteKind)}; structural and file-size checks passed, and {disposition}."
            : localCleaned && networkCleaned
                ? $"Conversion completed; final files and report passed quick checks, and {disposition}."
                : $"Conversion completed and {disposition}; a staging folder may require cleanup.";
        progress.Report(new(JobPhase.CleanupCompleted, 100,
            incomplete ? CompletionIssuePresentation.Status(finalIncompleteKind) : "passed", cleanupDetail, DateTimeOffset.UtcNow));
        return new(existingReport, outputs.Count(path =>
                Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".dsf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase)),
            deleted, incomplete, finalIncompleteKind);
    }

    private static async Task<IReadOnlySet<string>> ValidateRepairDeduplicationAsync(
        JsonObject report,
        string albumRoot,
        IReadOnlySet<string> repairSources,
        IReadOnlySet<string> outputPaths,
        CancellationToken token)
    {
        var removedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (report["deduplicated_tracks"] is not JsonArray duplicates || duplicates.Count == 0)
            return removedPaths;

        foreach (var node in duplicates)
        {
            if (node is not JsonObject duplicate)
                throw new JsonException("A deduplicated_tracks entry is not an object.");
            var removed = NormalizePathValue(duplicate["removed_file"], albumRoot, "deduplicated source");
            var retained = NormalizePathValue(duplicate["retained_file"], albumRoot, "retained duplicate counterpart");
            var expectedHash = duplicate["source_file_sha256"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(expectedHash) || expectedHash.Length != 64 ||
                !expectedHash.All(Uri.IsHexDigit))
                throw new JsonException("A deduplicated_tracks entry has no valid source SHA-256.");
            if (!repairSources.Contains(removed) || !repairSources.Contains(retained) ||
                outputPaths.Contains(removed) || !outputPaths.Contains(retained) ||
                !removedPaths.Add(removed))
                throw new IOException("The repair deduplication map does not match the admitted source and output paths. Every original was retained.");

            var removedHash = await FullFileSha256Async(HostStagingService.SafeCombine(albumRoot, removed), token);
            var retainedHash = await FullFileSha256Async(HostStagingService.SafeCombine(albumRoot, retained), token);
            if (!removedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase) ||
                !retainedHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("A proposed duplicate FLAC changed after inventory; every original was retained.");
        }
        return removedPaths;
    }

    private static async Task<string> FullFileSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static List<string> NormalizeAndCollectOutputs(JsonObject report, string stagedAlbumRoot)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (report["discs"] is not JsonArray discs || discs.Count == 0)
            throw new JsonException("The conversion report must contain at least one discs entry.");
        foreach (var discNode in discs)
        {
            if (discNode is not JsonObject disc) throw new JsonException("A discs entry is not an object.");
            var source = NormalizePathValue(disc["source"], stagedAlbumRoot, "disc source");
            disc["source"] = source;
            sources.Add(source);
            if (disc["tracks"] is not JsonArray tracks || tracks.Count == 0)
                throw new JsonException("A discs entry contains no tracks.");
            for (var index = 0; index < tracks.Count; index++)
            {
                if (tracks[index] is JsonValue value)
                {
                    var relative = NormalizePathValue(value, stagedAlbumRoot, "track");
                    tracks[index] = relative;
                    outputs.Add(relative);
                }
                else if (tracks[index] is JsonObject track)
                {
                    var relative = NormalizePathValue(track["file"], stagedAlbumRoot, "track");
                    track["file"] = relative;
                    outputs.Add(relative);
                }
                else throw new JsonException("A track entry has no file path.");
            }
        }
        if (report["genre"] is not JsonObject genre || string.IsNullOrWhiteSpace(genre["value"]?.GetValue<string>()))
            throw new JsonException("The conversion report has no nonempty genre value or provenance.");
        return outputs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> NormalizeAndCollectDsdOutputs(JsonObject report, string stagedAlbumRoot)
    {
        var outputs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (report["areas"] is not JsonArray areas || areas.Count == 0)
            throw new JsonException("The SACD conversion report must contain at least one areas entry.");
        foreach (var areaNode in areas)
        {
            if (areaNode is not JsonObject area || area["tracks"] is not JsonArray tracks || tracks.Count == 0)
                throw new JsonException("A SACD area contains no tracks.");
            for (var index = 0; index < tracks.Count; index++)
            {
                if (tracks[index] is not JsonObject track) throw new JsonException("A SACD track entry is not an object.");
                var relative = NormalizePathValue(track["file"], stagedAlbumRoot, "SACD track");
                if (!Path.GetExtension(relative).Equals(".dsf", StringComparison.OrdinalIgnoreCase))
                    throw new JsonException($"A SACD playback output is not DSF: {relative}");
                track["file"] = relative;
                outputs.Add(relative);
            }
        }
        if (report["genre"] is not JsonObject genre || string.IsNullOrWhiteSpace(genre["value"]?.GetValue<string>()))
            throw new JsonException("The SACD conversion report has no nonempty genre value or provenance.");
        RequireEmbeddedArtworkSha256(report);
        if (report["artifacts"] is JsonArray artifacts)
        {
            for (var index = 0; index < artifacts.Count; index++)
            {
                var relative = NormalizePathValue(artifacts[index], stagedAlbumRoot, "SACD provenance artifact");
                artifacts[index] = relative;
                outputs.Add(relative);
            }
        }
        return outputs.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string NormalizePathValue(JsonNode? node, string stagedAlbumRoot, string label)
    {
        var value = node?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(value)) throw new JsonException($"The report has an empty {label} path.");
        var relative = Path.IsPathRooted(value) ? HostStagingService.SafeRelative(stagedAlbumRoot, value) : value;
        var full = HostStagingService.SafeCombine(stagedAlbumRoot, relative);
        return HostStagingService.SafeRelative(stagedAlbumRoot, full);
    }

    private static async Task<PlaybackVerification> VerifyPlaybackFilesAsync(
        IEnumerable<string> outputs,
        string albumRoot,
        StagedJob staged,
        JsonObject report,
        bool isDsd,
        bool isRepair,
        CancellationToken token)
    {
        if (!isDsd)
        {
            var result = new PlaybackVerification(await VerifyTrackHeadersAsync(outputs, albumRoot, staged, TryEmbeddedArtworkSha256(report), token));
            if (isRepair) await VerifyRepairAudioPayloadsAsync(report, albumRoot, token);
            return result;
        }

        var expectedArtworkSha256 = RequireEmbeddedArtworkSha256(report);
        if (report["areas"] is not JsonArray areas) throw new JsonException("The SACD report has no areas array.");
        foreach (var areaNode in areas)
        {
            if (areaNode is not JsonObject area || area["tracks"] is not JsonArray tracks)
                throw new JsonException("A SACD area has no track array.");
            foreach (var trackNode in tracks)
            {
                if (trackNode is not JsonObject track) throw new JsonException("A SACD track is not an object.");
                var relative = track["file"]?.GetValue<string>() ?? throw new JsonException("A SACD track path is missing.");
                var expectedPayloadBytes = track["dsd_payload_bytes_after_tags"]?.GetValue<long>()
                    ?? throw new JsonException($"A SACD track has no tagged DSD payload size: {relative}");
                var expectedFileSize = track["file_size"]?.GetValue<long>()
                    ?? throw new JsonException($"A SACD track has no recorded file size: {relative}");
                var path = HostStagingService.SafeCombine(albumRoot, relative);
                await LocalDsdProcessor.VerifyCommittedDsfAsync(
                    staged.FfprobePath,
                    path,
                    expectedFileSize,
                    expectedPayloadBytes,
                    expectedArtworkSha256,
                    token);
            }
        }
        foreach (var relative in outputs.Where(path => !Path.GetExtension(path).Equals(".dsf", StringComparison.OrdinalIgnoreCase)))
            if (!File.Exists(HostStagingService.SafeCombine(albumRoot, relative)))
                throw new FileNotFoundException($"A SACD report artifact is missing: {relative}");
        return new([]);
    }

    private static async Task VerifyRepairAudioPayloadsAsync(JsonObject report, string albumRoot, CancellationToken token)
    {
        if (report["discs"] is not JsonArray discs || discs.Count == 0)
            throw new JsonException("The existing-track repair report has no discs array.");
        foreach (var discNode in discs)
        {
            if (discNode is not JsonObject disc || disc["tracks"] is not JsonArray tracks || tracks.Count == 0)
                throw new JsonException("An existing-track repair disc has no tracks.");
            foreach (var trackNode in tracks)
            {
                if (trackNode is not JsonObject track)
                    throw new JsonException("An existing-track repair track is not an object.");
                var relative = track["file"]?.GetValue<string>()
                    ?? throw new JsonException("An existing-track repair track path is missing.");
                var before = track["audio_payload_sha256_before"]?.GetValue<string>();
                var after = track["audio_payload_sha256_after"]?.GetValue<string>();
                if (before is not { Length: 64 } || after is not { Length: 64 } ||
                    !before.All(Uri.IsHexDigit) || !after.All(Uri.IsHexDigit) ||
                    !before.Equals(after, StringComparison.OrdinalIgnoreCase))
                    throw new JsonException($"The repair report does not prove unchanged compressed audio for '{relative}'.");
                var actual = await TrackAudioPayload.Sha256Async(HostStagingService.SafeCombine(albumRoot, relative), token);
                if (!actual.Equals(before, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The native audio payload no longer matches the pre-repair source: {relative}");
            }
        }
    }

    private static void RestoreReplacementRollback(
        IReadOnlyList<string> moved,
        IReadOnlyList<string> rolledBack,
        string albumRoot,
        string networkStage,
        string rollbackRoot,
        string existingReport,
        string previousReport)
    {
        foreach (var relative in moved.Reverse())
        {
            var final = HostStagingService.SafeCombine(albumRoot, relative);
            var network = HostStagingService.SafeCombine(networkStage, relative);
            if (!File.Exists(final)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(network)!);
            File.Move(final, network, overwrite: false);
        }
        foreach (var relative in rolledBack.Reverse())
        {
            var final = HostStagingService.SafeCombine(albumRoot, relative);
            var rollback = HostStagingService.SafeCombine(rollbackRoot, relative);
            if (File.Exists(final) || !File.Exists(rollback)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            File.Move(rollback, final, overwrite: false);
        }
        if (File.Exists(previousReport)) File.Copy(previousReport, existingReport, overwrite: true);
        else if (File.Exists(existingReport)) File.Delete(existingReport);
    }

    private static async Task<IReadOnlyList<string>> VerifyTrackHeadersAsync(
        IEnumerable<string> outputs,
        string albumRoot,
        StagedJob staged,
        string? expectedArtworkSha256,
        CancellationToken token)
    {
        var artworkIssues = new List<string>();
        foreach (var relative in outputs.Where(path =>
                     Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".dsf", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase)))
        {
            var path = HostStagingService.SafeCombine(albumRoot, relative);
            var isDsfRepair = Path.GetExtension(path).Equals(".dsf", StringComparison.OrdinalIgnoreCase);
            var isDffRepair = Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase);
            var isDsdRepair = isDsfRepair || isDffRepair;
            var info = new ProcessStartInfo(staged.FfprobePath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var value in new[] { "-v", "error", "-show_streams", "-show_format", "-of", "json", path }) info.ArgumentList.Add(value);
            using var process = new Process { StartInfo = info };
            if (!process.Start()) throw new InvalidOperationException("Could not start ffprobe for quick existing-track verification.");
            var outputTask = process.StandardOutput.ReadToEndAsync(token);
            var errorTask = process.StandardError.ReadToEndAsync(token);
            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } throw; }
            var output = await outputTask;
            var error = await errorTask;
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffprobe failed for '{relative}': {error.Trim()}");

            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (!root.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"No readable streams were found in '{relative}'.");
            var hasExpectedAudio = streams.EnumerateArray().Any(stream =>
                Text(stream, "codec_type")?.Equals("audio", StringComparison.OrdinalIgnoreCase) == true &&
                (isDsdRepair
                    ? Text(stream, "codec_name")?.StartsWith("dsd", StringComparison.OrdinalIgnoreCase) == true &&
                      Text(stream, "codec_name")?.Contains("pcm", StringComparison.OrdinalIgnoreCase) != true
                    : Text(stream, "codec_name")?.Equals("flac", StringComparison.OrdinalIgnoreCase) == true));
            var coverStream = streams.EnumerateArray().FirstOrDefault(stream =>
                Text(stream, "codec_type")?.Equals("video", StringComparison.OrdinalIgnoreCase) == true &&
                stream.TryGetProperty("disposition", out var disposition) &&
                disposition.TryGetProperty("attached_pic", out var attached) &&
                attached.TryGetInt32(out var value) && value == 1);
            if (!hasExpectedAudio)
                throw new InvalidOperationException($"A native {(isDsdRepair ? "DSD" : "FLAC")} audio stream is missing from '{relative}'.");
            if (!isDffRepair)
            {
                if (coverStream.ValueKind == JsonValueKind.Undefined)
                    artworkIssues.Add($"Embedded front cover is missing from '{relative}'.");
                else if (!coverStream.TryGetProperty("width", out var widthValue) || !widthValue.TryGetInt32(out var width) ||
                         !coverStream.TryGetProperty("height", out var heightValue) || !heightValue.TryGetInt32(out var height) ||
                         width <= 0 || height <= 0)
                    artworkIssues.Add($"Embedded front-cover dimensions are unreadable in '{relative}'.");
                else if (width > 600 || height > 600 || width != height)
                    artworkIssues.Add($"Embedded front cover in '{relative}' is {width}x{height}; it must be square and no larger than 600x600.");
            }
            if (expectedArtworkSha256 is not null)
            {
                var actualArtworkSha256 = InMemoryArtworkService.ReadFrontCoverSha256(path);
                if (!string.Equals(actualArtworkSha256, expectedArtworkSha256, StringComparison.OrdinalIgnoreCase))
                    artworkIssues.Add($"Embedded front cover in '{relative}' does not match the report-proven in-memory artwork.");
            }
            else if (isDffRepair && DffMetadata.Read(path).Picture is null)
                artworkIssues.Add($"Embedded front cover is missing from '{relative}'.");

            if (isDffRepair)
            {
                var tagged = DffMetadata.Read(path);
                var requiredDff = new Dictionary<string, string?>
                {
                    ["TITLE"] = tagged.Title,
                    ["ALBUM"] = tagged.Album,
                    ["ARTIST"] = tagged.Artist,
                    ["ALBUMARTIST"] = tagged.AlbumArtist,
                    ["GENRE"] = tagged.Genre
                };
                var missingDff = requiredDff.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();
                if (tagged.Track == 0) missingDff.Add("TRACKNUMBER");
                if (tagged.Disc == 0) missingDff.Add("DISCNUMBER");
                if (tagged.Year == 0) missingDff.Add("DATE");
                if (missingDff.Count > 0)
                    throw new InvalidOperationException($"Required tags missing from '{relative}': {string.Join(", ", missingDff)}.");
                if (ClassicalMetadataPolicy.RequiresComposer(
                        tagged.Genre, tagged.Title, ClassicalMetadataPolicy.IsCompilationArtist(tagged.AlbumArtist)) &&
                    string.IsNullOrWhiteSpace(tagged.Composer))
                    throw new InvalidOperationException($"COMPOSER is required for classical/opera track '{relative}'.");
                continue;
            }

            if (isDsfRepair)
            {
                using var tagged = TagLib.File.Create(path);
                var requiredDsf = new Dictionary<string, string?>
                {
                    ["TITLE"] = tagged.Tag.Title,
                    ["ALBUM"] = tagged.Tag.Album,
                    ["ARTIST"] = tagged.Tag.FirstPerformer,
                    ["ALBUMARTIST"] = tagged.Tag.FirstAlbumArtist,
                    ["GENRE"] = tagged.Tag.FirstGenre
                };
                var missingDsf = requiredDsf.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();
                if (tagged.Tag.Track == 0) missingDsf.Add("TRACKNUMBER");
                if (tagged.Tag.Disc == 0) missingDsf.Add("DISCNUMBER");
                if (tagged.Tag.Year == 0) missingDsf.Add("DATE");
                if (missingDsf.Count > 0)
                    throw new InvalidOperationException($"Required tags missing from '{relative}': {string.Join(", ", missingDsf)}.");
                if (ClassicalMetadataPolicy.RequiresComposer(
                        tagged.Tag.FirstGenre, tagged.Tag.Title,
                        ClassicalMetadataPolicy.IsCompilationArtist(tagged.Tag.FirstAlbumArtist)) &&
                    string.IsNullOrWhiteSpace(tagged.Tag.FirstComposer))
                    throw new InvalidOperationException($"COMPOSER is required for classical/opera track '{relative}'.");
                continue;
            }

            if (!root.TryGetProperty("format", out var format) ||
                !format.TryGetProperty("tags", out var tags) ||
                tags.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"Required tags are missing from '{relative}'.");
            var values = tags.EnumerateObject()
                .ToDictionary(tag => tag.Name, tag => tag.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var required = new[]
            {
                new[] { "TITLE" }, new[] { "ALBUM" }, new[] { "ARTIST" },
                new[] { "ALBUMARTIST", "ALBUM_ARTIST" }, new[] { "TRACKNUMBER", "TRACK" },
                new[] { "DISCNUMBER", "DISC" }, new[] { "DATE", "YEAR" }, new[] { "GENRE" }
            };
            var labels = new[] { "TITLE", "ALBUM", "ARTIST", "ALBUMARTIST", "TRACKNUMBER", "DISCNUMBER", "DATE", "GENRE" };
            var missing = required.Select((group, index) => new { group, index })
                .Where(item => !item.group.Any(name => values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)))
                .Select(item => labels[item.index])
                .ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"Required tags missing from '{relative}': {string.Join(", ", missing)}.");
            values.TryGetValue("TITLE", out var title);
            values.TryGetValue("ALBUMARTIST", out var albumArtist);
            if (string.IsNullOrWhiteSpace(albumArtist))
                values.TryGetValue("ALBUM_ARTIST", out albumArtist);
            if (values.TryGetValue("GENRE", out var genre) &&
                ClassicalMetadataPolicy.RequiresComposer(
                    genre, title, ClassicalMetadataPolicy.IsCompilationArtist(albumArtist)) &&
                (!values.TryGetValue("COMPOSER", out var composer) || string.IsNullOrWhiteSpace(composer)))
                throw new InvalidOperationException($"COMPOSER is required for classical/opera track '{relative}'.");
        }
        return artworkIssues;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void VerifyOriginalSourceSizesUnchanged(
        ScanResult scan,
        StagedJob staged,
        CancellationToken token)
    {
        foreach (var source in staged.Sources)
        {
            token.ThrowIfCancellationRequested();
            var fullPath = HostStagingService.SafeCombine(scan.AlbumRoot, source.RelativePath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("An inventoried source disappeared during the run.", fullPath);
            var info = new FileInfo(fullPath);
            if (info.Length != source.Size)
                throw new IOException($"The original source size changed during the run: {source.RelativePath}. It was retained.");
        }
    }

    private static IReadOnlyList<DeletionTarget> ResolveDeletionTargets(
        ScanResult scan,
        StagedJob staged,
        bool isDsd,
        CancellationToken token)
    {
        if (staged.Sources.Count != 1)
            throw new InvalidOperationException($"Automatic source deletion requires exactly one inventoried image; found {staged.Sources.Count}.");

        var source = staged.Sources[0];
        var requiredExtension = isDsd ? ".iso" : ".flac";
        var requiredKind = isDsd ? "SACD / DSD image" : "FLAC image";
        if (!Path.GetExtension(source.RelativePath).Equals(requiredExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Automatic source deletion is limited to an inventoried {requiredExtension} image for this workflow.");
        if (!scan.Media.Any(item =>
                item.Kind.Equals(requiredKind, StringComparison.OrdinalIgnoreCase) &&
                item.RelativePath.Equals(source.RelativePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The deletion target is not the source image identified by the read-only inventory.");

        VerifyOriginalSourceSizesUnchanged(scan, staged, token);
        var fullPath = HostStagingService.SafeCombine(scan.AlbumRoot, source.RelativePath);
        var info = new FileInfo(fullPath);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Album Fixer will not delete a reparse-point source.");
        return [new(source.RelativePath, fullPath)];
    }

    private static IReadOnlyList<DeletionTarget> ResolveRetainedRepairIsoDeletionTarget(
        ScanResult scan,
        StagedSource source,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.GetExtension(source.RelativePath).Equals(".iso", StringComparison.OrdinalIgnoreCase) ||
            scan.Media.Count(item =>
                item.Kind.Equals("SACD / DSD image", StringComparison.OrdinalIgnoreCase) &&
                item.RelativePath.Equals(source.RelativePath, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new InvalidOperationException("The retained repair deletion target is not the single SACD ISO identified by the read-only inventory.");

        var fullPath = HostStagingService.SafeCombine(scan.AlbumRoot, source.RelativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The retained SACD ISO disappeared during the repair.", fullPath);
        var info = new FileInfo(fullPath);
        if (info.Length != source.Size)
            throw new IOException($"The retained SACD ISO size changed during the repair: {source.RelativePath}. It was retained.");
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Album Fixer will not delete a reparse-point SACD ISO.");
        return [new(source.RelativePath, fullPath)];
    }

    private sealed record DeletionTarget(string RelativePath, string FullPath);
    private sealed record PlaybackVerification(IReadOnlyList<string> ArtworkIssues);
    private sealed class RepairRollbackException(string message, Exception innerException) : IOException(message, innerException);

    private static IReadOnlyList<string> ArtworkIssues(JsonObject report, IReadOnlyList<string> embeddedArtworkIssues)
    {
        var issues = embeddedArtworkIssues.ToList();
        if (report["cover"] is not JsonObject cover)
            issues.Add("The report has no embedded-only front-cover descriptor.");
        else
        {
            if (!string.Equals(cover["storage"]?.GetValue<string>(), "embedded_only", StringComparison.OrdinalIgnoreCase))
                issues.Add("The report does not identify front-cover storage as embedded_only.");
            if (!string.Equals(cover["mime_type"]?.GetValue<string>(), PreparedArtwork.MimeType, StringComparison.OrdinalIgnoreCase))
                issues.Add("The report does not identify the embedded front cover as image/jpeg.");
            if (cover["width"]?.GetValue<int>() is not > 0 or > InMemoryArtworkService.MaximumDimension ||
                cover["height"]?.GetValue<int>() is not > 0 or > InMemoryArtworkService.MaximumDimension ||
                cover["width"]?.GetValue<int>() != cover["height"]?.GetValue<int>())
                issues.Add("The report has invalid embedded front-cover dimensions.");
            if (cover["byte_size"]?.GetValue<int>() is not > 0 or > InMemoryArtworkService.MaximumPreparedBytes)
                issues.Add("The report has an invalid embedded front-cover byte size.");
            if (TryEmbeddedArtworkSha256(report) is null)
                issues.Add("The report has no valid embedded front-cover SHA-256.");
        }
        return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? TryEmbeddedArtworkSha256(JsonObject report)
    {
        if (report["cover"] is not JsonObject cover ||
            !string.Equals(cover["storage"]?.GetValue<string>(), "embedded_only", StringComparison.OrdinalIgnoreCase) ||
            cover["sha256"]?.GetValue<string>() is not { Length: 64 } sha256 ||
            !sha256.All(Uri.IsHexDigit)) return null;
        return sha256;
    }

    private static string RequireEmbeddedArtworkSha256(JsonObject report) =>
        TryEmbeddedArtworkSha256(report)
        ?? throw new JsonException("The conversion report has no valid embedded-only front-cover SHA-256 descriptor.");

    private static IReadOnlyList<string> IncompleteIssues(JsonObject report, IReadOnlyList<string> artworkIssues)
    {
        var issues = artworkIssues.ToList();
        if (report["verification"] is JsonObject verification && verification["missing_metadata"] is JsonArray missing)
        {
            issues.AddRange(missing
                .Select(value => value?.GetValue<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value) && !MetadataFieldPolicy.IsOptional(value!))
                .Select(value => value!.Equals("COVER", StringComparison.OrdinalIgnoreCase)
                    ? "Cover artwork remains unresolved."
                    : $"Metadata field remains unresolved: {value}."));
        }
        if (report["genre"] is not JsonObject genre ||
            string.IsNullOrWhiteSpace(genre["value"]?.GetValue<string>()) ||
            genre["value"]?.GetValue<string>().Equals("Unknown", StringComparison.OrdinalIgnoreCase) == true ||
            genre["source_type"]?.GetValue<string>().Equals("unresolved_placeholder", StringComparison.OrdinalIgnoreCase) == true)
            issues.Add("Metadata field remains unresolved: GENRE.");
        return issues.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> OptionalMetadataWarnings(JsonObject report)
    {
        if (report["verification"] is not JsonObject verification || verification["missing_metadata"] is not JsonArray missing)
            return [];
        return missing
            .Select(value => value?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value) && MetadataFieldPolicy.IsOptional(value!))
            .Select(value => $"Optional metadata field remains unresolved: {value}.")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static CompletionIssueKind CompletionIssue(IReadOnlyList<string> issues)
    {
        var metadata = issues.Any(issue => issue.StartsWith("Metadata field remains unresolved:", StringComparison.OrdinalIgnoreCase));
        var artwork = issues.Any(issue => !issue.StartsWith("Metadata field remains unresolved:", StringComparison.OrdinalIgnoreCase));
        return (metadata, artwork) switch
        {
            (true, true) => CompletionIssueKind.RequiredMetadataAndCoverArtwork,
            (true, false) => CompletionIssueKind.RequiredMetadata,
            (false, true) => CompletionIssueKind.CoverArtwork,
            _ => CompletionIssueKind.None
        };
    }

    private static JsonArray VerificationWarnings(JsonObject report, IReadOnlyList<string> incompleteIssues) =>
        new(OptionalMetadataWarnings(report)
            .Concat(incompleteIssues)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(issue => JsonValue.Create(issue))
            .ToArray());

    private static void SetIncompleteKind(JsonObject verification, CompletionIssueKind kind)
    {
        if (kind == CompletionIssueKind.None) verification.Remove("incomplete_kind");
        else verification["incomplete_kind"] = CompletionIssuePresentation.Status(kind);
    }

    private static void SetRepairVerification(
        JsonObject report,
        string stage,
        IReadOnlyList<string> incompleteIssues,
        bool sourceDeletionRequested)
    {
        var incomplete = incompleteIssues.Count > 0;
        var incompleteKind = CompletionIssue(incompleteIssues);
        var isDsf = report["format"]?.GetValue<string>().Equals("dsf", StringComparison.OrdinalIgnoreCase) == true;
        var isDff = report["format"]?.GetValue<string>().Equals("dff", StringComparison.OrdinalIgnoreCase) == true;
        var verification = report["verification"] as JsonObject ?? new JsonObject();
        report["verification"] = verification;
        verification["status"] = incomplete ? "incomplete" : "passed";
        verification["method"] = isDsf
            ? "Exact SHA-256 equality of each native DSF data-chunk payload before tag/art repair, in local staging, and at the final album path; native-DSD, tag/artwork, and file-size copy checks also passed."
            : isDff
            ? "Exact SHA-256 equality of each native DFF DSD-chunk payload before tag/art repair, in local staging, and at the final album path; native-DSD, ID3v2 tag/artwork, and file-size copy checks also passed."
            : "Exact SHA-256 equality of each compressed FLAC audio-frame payload before tag/art repair, in local staging, and at the final album path; ffprobe tag/artwork and file-size copy checks also passed.";
        verification["audio_payload_equivalence"] = "passed";
        verification["pcm_equivalence"] = isDsf || isDff
            ? "native_dsd_data_chunk_bit_exact_without_decode"
            : "encoded_flac_frames_bit_exact_without_decode";
        verification["audio_and_tags"] = "passed";
        verification["required_metadata"] = incompleteKind is CompletionIssueKind.RequiredMetadata or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["artwork"] = incompleteKind is CompletionIssueKind.CoverArtwork or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["verified_stage"] = stage;
        verification["verified_at_utc"] = DateTimeOffset.UtcNow;
        verification["source_deletion_requested"] = sourceDeletionRequested && !incomplete;
        verification["source_deletion_eligible"] = sourceDeletionRequested && !incomplete;
        verification["sources_deleted"] = false;
        verification["errors"] = new JsonArray();
        verification["warnings"] = VerificationWarnings(report, incompleteIssues);
        SetIncompleteKind(verification, incompleteKind);
        report["work_status"] = incomplete ? "incomplete" : "complete";
        if (incomplete)
        {
            report["incomplete_work"] = new JsonObject
            {
                ["reason"] = "Front-cover artwork remains unresolved after existing embedded art, local art, and external lookup were tried in priority order.",
                ["repairable_without_source_image"] = true,
                ["issues"] = new JsonArray(incompleteIssues.Select(issue => JsonValue.Create(issue)).ToArray())
            };
        }
        else report.Remove("incomplete_work");
    }

    private static void SetQuickVerification(JsonObject report, string stage, bool sourceDeletionRequested, IReadOnlyList<string> incompleteIssues)
    {
        var incomplete = incompleteIssues.Count > 0;
        var incompleteKind = CompletionIssue(incompleteIssues);
        var verification = report["verification"] as JsonObject ?? new JsonObject();
        report["verification"] = verification;
        verification["status"] = incomplete ? "incomplete" : "passed";
        verification["method"] = incomplete
            ? "Quick ffprobe FLAC/header/tag checks plus file-size copy checks passed; unresolved metadata or artwork is deferred and decoded PCM byte-count/MD5 comparison was skipped by user."
            : "Quick ffprobe FLAC/header/tag/artwork checks plus file-size copy checks; decoded PCM byte-count and MD5 comparison skipped by user.";
        verification["pcm_equivalence"] = "skipped_by_user";
        verification["audio_and_tags"] = "passed";
        verification["required_metadata"] = incompleteKind is CompletionIssueKind.RequiredMetadata or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["artwork"] = incompleteKind is CompletionIssueKind.CoverArtwork or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["verified_stage"] = stage;
        verification["verified_at_utc"] = DateTimeOffset.UtcNow;
        verification["required_metadata"] = incompleteKind is CompletionIssueKind.RequiredMetadata or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["artwork"] = incompleteKind is CompletionIssueKind.CoverArtwork or CompletionIssueKind.RequiredMetadataAndCoverArtwork ? "incomplete" : "passed";
        verification["source_deletion_requested"] = sourceDeletionRequested && !incomplete;
        verification["source_deletion_eligible"] = sourceDeletionRequested && !incomplete;
        verification["sources_deleted"] = false;
        verification["errors"] = new JsonArray();
        verification["warnings"] = VerificationWarnings(report, incompleteIssues);
        SetIncompleteKind(verification, incompleteKind);
        report["work_status"] = incomplete ? "incomplete" : "complete";
        if (incomplete)
        {
            report["incomplete_work"] = new JsonObject
            {
                ["reason"] = "Metadata or front-cover artwork remains unresolved after best-effort lookup.",
                ["repairable_without_source_image"] = true,
                ["issues"] = new JsonArray(incompleteIssues.Select(issue => JsonValue.Create(issue)).ToArray())
            };
        }
        else report.Remove("incomplete_work");
    }
    private static bool DsdAudioVerificationPassed(JsonObject report) =>
        report["verification"] is JsonObject verification &&
        (verification["status"]?.GetValue<string>().Equals("passed", StringComparison.OrdinalIgnoreCase) == true ||
         verification["status"]?.GetValue<string>().Equals("incomplete", StringComparison.OrdinalIgnoreCase) == true) &&
        verification["independent_extraction"]?.GetValue<string>().Equals("passed", StringComparison.OrdinalIgnoreCase) == true &&
        verification["tag_payload_size_verification"]?.GetValue<string>().Equals("passed", StringComparison.OrdinalIgnoreCase) == true;
    private static bool DsdDeletionEligible(JsonObject report) =>
        DsdAudioVerificationPassed(report) &&
        IncompleteIssues(report, []).Count == 0 &&
        report["verification"] is JsonObject verification &&
        verification["source_deletion_eligible"]?.GetValue<bool>() == true;
    private static void ConfirmDsdVerification(JsonObject report, string stage, bool sourceDeletionRequested)
    {
        if (!DsdAudioVerificationPassed(report))
            throw new InvalidDataException("The SACD report does not prove independent extraction sizes and unchanged tagged DSD payload size.");
        var verification = (JsonObject)report["verification"]!;
        var issues = IncompleteIssues(report, []);
        var incomplete = issues.Count > 0;
        var incompleteKind = CompletionIssue(issues);
        verification["status"] = incomplete ? "incomplete" : "passed";
        verification["verified_stage"] = stage;
        verification["verified_at_utc"] = DateTimeOffset.UtcNow;
        verification["source_deletion_requested"] = sourceDeletionRequested && !incomplete;
        verification["source_deletion_eligible"] = sourceDeletionRequested && !incomplete;
        verification["sources_deleted"] = false;
        verification["errors"] = new JsonArray();
        verification["warnings"] = VerificationWarnings(report, issues);
        SetIncompleteKind(verification, incompleteKind);
        report["work_status"] = incomplete ? "incomplete" : "complete";
        if (incomplete)
        {
            report["incomplete_work"] = new JsonObject
            {
                ["reason"] = "Required metadata remains unresolved. Verified DSF tracks remain usable.",
                ["repairable_without_source_image"] = true,
                ["issues"] = new JsonArray(issues.Select(issue => JsonValue.Create(issue)).ToArray())
            };
        }
        else report.Remove("incomplete_work");
    }
    private static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            return !Directory.Exists(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }
    private static async Task AtomicWriteAsync(string path, string json, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) =>
        new(phase, percent, "running", detail, DateTimeOffset.UtcNow);
}
