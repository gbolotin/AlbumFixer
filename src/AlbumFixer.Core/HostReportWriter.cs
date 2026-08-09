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
        int? exitCode,
        string? threadId,
        CancellationToken token = default)
    {
        var normalizedStatus = status.Equals("canceled", StringComparison.OrdinalIgnoreCase) ? "canceled" : "failed";
        var now = DateTimeOffset.UtcNow;
        var sourceCacheUsed = HostStagingService.RequiresSourceCache(scan.AlbumRoot);
        var report = new
        {
            schema_version = "1.0",
            album = scan.AlbumName,
            edition = "Unresolved — run stopped before release identification completed",
            format = scan.HasFlac ? "flac" : scan.HasDsd ? "dsd" : "unknown",
            source_type = SourceType(scan.Mode),
            workflow_mode = scan.Mode.ToString(),
            album_root = scan.AlbumRoot,
            generated_by = "Album Fixer host fallback",
            generated_at_utc = now,
            sources = scan.Media.Select(item => new
            {
                path = item.RelativePath,
                type = item.Kind,
                size = item.Size,
                sha256 = (string?)null,
                hash_status = "not_computed",
                note = item.Note
            }),
            tools = preflight.Tools,
            job = new
            {
                identifier = Path.GetFileName(jobDirectory.TrimEnd(Path.DirectorySeparatorChar)),
                owner = scan.AlbumRoot,
                local_staging_used = true,
                source_cache_used = sourceCacheUsed,
                source_input_mode = sourceCacheUsed ? "verified_temp_cache" : "local_fixed_disk_in_place",
                copy_in_status = sourceCacheUsed ? "incomplete_or_unverified" : "not_required_local_fixed_disk",
                staging_path = jobDirectory,
                thread_id = threadId,
                codex_exit_code = exitCode,
                staging_preserved = Directory.Exists(jobDirectory)
            },
            pipeline = new
            {
                status = normalizedStatus,
                stopped_phase = stoppedPhase.ToString(),
                percent = Math.Clamp(percent, 0, 100),
                detail,
                updated_at_utc = now
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
                originals_retained = true,
                action = "Review the preserved staging job and retry only after the reported blocker is resolved"
            }
        };

        var json = JsonSerializer.Serialize(report, JsonOptions);
        Directory.CreateDirectory(jobDirectory);
        var localPath = Path.Combine(jobDirectory, "conversion-report.json");
        await AtomicWriteAsync(localPath, json, overwrite: true, token);

        var albumPath = Path.GetFullPath(Path.Combine(scan.AlbumRoot, "conversion-report.json"));
        try
        {
            if (File.Exists(albumPath)) return albumPath;
            await AtomicWriteAsync(albumPath, json, overwrite: false, token);
            return albumPath;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
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

    private static string SourceType(WorkflowMode mode) => mode switch
    {
        WorkflowMode.FlacCueSplit => "flac_cue",
        WorkflowMode.DsdExtraction => "dsd_image",
        WorkflowMode.ExistingTrackRepair => "existing_track_repair",
        WorkflowMode.MultipleAlbums => "multiple_albums",
        WorkflowMode.NeedsInspection => "needs_inspection",
        WorkflowMode.Completed => "completed",
        _ => "unsupported"
    };
}
