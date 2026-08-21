using System.Text;
using System.Text.Json;

namespace AlbumFixer.Core;

public static class HostReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static async Task<string> EnsureTerminalReportAsync(
        ScanResult scan,
        PreflightResult preflight,
        string jobDirectory,
        string status,
        JobPhase stoppedPhase,
        int percent,
        string detail,
        CancellationToken token = default,
        IReadOnlyList<string>? diagnostics = null)
    {
        var normalizedStatus = status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ? "canceled" : "failed";
        var now = DateTimeOffset.UtcNow;
        var sourceCacheUsed = HostStagingService.RequiresSourceCache(scan.AlbumRoot);
        var report = new
        {
            schema_version = "2.0",
            album = scan.AlbumName,
            edition = "Unresolved — run stopped before release identification completed",
            format = scan.HasFlac ? "flac" : scan.HasDsd ? "dsd" : "unknown",
            source_type = SourceType(scan),
            workflow_mode = WorkflowId(scan),
            album_root = scan.AlbumRoot,
            generated_by = "Album Fixer host fallback",
            generated_at_utc = now,
            sources = scan.Media.Select(item => new
            {
                path = item.RelativePath,
                type = item.Kind,
                size = item.Size,
                size_status = "inventory_only",
                note = item.Note
            }),
            tools = preflight.Tools,
            job = new
            {
                identifier = Path.GetFileName(jobDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                owner = scan.AlbumRoot,
                local_staging_used = true,
                source_cache_used = sourceCacheUsed,
                source_input_mode = sourceCacheUsed ? "size_checked_temp_cache" : "local_fixed_disk_in_place",
                copy_in_status = sourceCacheUsed ? "incomplete_or_unverified" : "not_required_local_fixed_disk",
                staging_path = jobDirectory,
                staging_preserved = false,
                cleanup_policy = "always_remove_after_terminal_report"
            },
            pipeline = new
            {
                status = normalizedStatus,
                stopped_phase = stoppedPhase.ToString(),
                percent = Math.Clamp(percent, 0, 100),
                detail,
                updated_at_utc = now
            },
            diagnostics = new
            {
                external_lookup_warnings = (diagnostics ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            },
            discs = Array.Empty<object>(),
            verification = new
            {
                status = normalizedStatus,
                method = "Not performed — the run stopped before final verification",
                sources_deleted = false,
                errors = new[] { detail }
            },
            commit = new
            {
                status = "not_completed",
                network_side_staging = (string?)null,
                final_path_verification = "not_performed"
            },
            deletion = new
            {
                requested_after_verification = true,
                performed = false,
                reason = "Failure or cancellation retains every original source"
            },
            recovery = new
            {
                originals_retained = scan.Media.Where(HostStagingService.IsSource).All(item => File.Exists(item.Path)),
                transient_staging_cleanup = "required_after_report_write",
                action = "Review the reported blocker and retry; no transient staging is required for recovery"
            }
        };

        var json = JsonSerializer.Serialize(report, JsonOptions);
        var albumPath = Path.GetFullPath(Path.Combine(scan.AlbumRoot, "conversion-report.json"));
        var targetPath = !PreviousOutputCleanupService.HasTerminalSuccessEvidence(scan.AlbumRoot)
            ? albumPath
            : Path.Combine(scan.AlbumRoot,
                $"conversion-report.{normalizedStatus}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        try
        {
            await AtomicWriteAsync(targetPath, json,
                overwrite: targetPath.Equals(albumPath, StringComparison.OrdinalIgnoreCase), token);
            return targetPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Directory.CreateDirectory(jobDirectory);
            var localPath = Path.Combine(jobDirectory, "conversion-report.json");
            await AtomicWriteAsync(localPath, json, overwrite: true, token);
            return localPath;
        }
    }

    private static async Task AtomicWriteAsync(string path, string json, bool overwrite, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Report path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, json, new UTF8Encoding(false), token);
            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string SourceType(ScanResult scan) => scan.Mode switch
    {
        WorkflowMode.FlacCueSplit => CueAudioImagePolicy.SourceType(
            scan.Media.Where(item => CueAudioImagePolicy.IsImageKind(item.Kind)).Select(item => item.Path)),
        WorkflowMode.DsdExtraction => "dsd_image",
        WorkflowMode.ExistingTrackRepair => "existing_track_repair",
        WorkflowMode.MultipleAlbums => "multiple_albums",
        WorkflowMode.NeedsInspection => "needs_inspection",
        WorkflowMode.Completed => "completed",
        _ => "unsupported"
    };

    private static string WorkflowId(ScanResult scan) => scan.Mode switch
    {
        WorkflowMode.FlacCueSplit => CueAudioImagePolicy.WorkflowId(
            scan.Media.Where(item => CueAudioImagePolicy.IsImageKind(item.Kind)).Select(item => item.Path)),
        WorkflowMode.DsdExtraction => "sacd_iso_extract",
        WorkflowMode.ExistingTrackRepair => "existing_track_repair",
        WorkflowMode.MultipleAlbums => "multiple_albums",
        WorkflowMode.NeedsInspection => "needs_inspection",
        WorkflowMode.Completed => "completed",
        _ => "unsupported"
    };
}
