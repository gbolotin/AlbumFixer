namespace AlbumFixer.Core;

public sealed record WorkflowCleanupResult(
    IReadOnlyList<string> RemovedPaths,
    IReadOnlyList<string> RemainingPaths)
{
    public bool Completed => RemainingPaths.Count == 0;
}

public static class WorkflowCleanupService
{
    public const string DestinationStagePrefix = ".album-fixer-stage-";

    public static string DestinationStagePath(string albumRoot, string jobDirectory)
    {
        var jobId = Path.GetFileName(jobDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(jobId))
            throw new InvalidOperationException("The Album Fixer job identifier is empty.");
        var stageIdentity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(jobDirectory))))[..12].ToLowerInvariant();
        return HostStagingService.SafeCombine(albumRoot, $"{DestinationStagePrefix}{jobId}-{stageIdentity}");
    }

    public static async Task<WorkflowCleanupResult> CleanupDestinationStagesAsync(string albumRoot)
    {
        var removed = new List<string>();
        var remaining = new List<string>();
        var root = Path.GetFullPath(albumRoot);
        if (!Directory.Exists(root)) return new(removed, remaining);

        IEnumerable<string> candidates;
        try
        {
            candidates = Directory.EnumerateFileSystemEntries(root, $"{DestinationStagePrefix}*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return new(removed, [root]);
        }

        foreach (var candidate in candidates)
        {
            var exactPath = HostStagingService.SafeCombine(root, Path.GetFileName(candidate));
            if (!Path.GetFileName(exactPath).StartsWith(DestinationStagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                remaining.Add(exactPath);
                continue;
            }

            if (await DeleteOwnedPathWithRetriesAsync(exactPath)) removed.Add(exactPath);
            else remaining.Add(exactPath);
        }

        return new(removed, remaining);
    }

    public static async Task<bool> CleanupLocalJobAsync(string jobDirectory, string tempRoot)
    {
        HostStagingService.ValidateJobDirectory(jobDirectory, tempRoot);
        var root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var job = Path.GetFullPath(jobDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (job.Equals(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete the Album Fixer Temp root itself.");
        var cleaned = await DeleteOwnedPathWithRetriesAsync(job);
        if (cleaned)
        {
            try
            {
                if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                    Directory.Delete(root, recursive: false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Another Album Fixer job may have entered the shared Temp root.
            }
        }
        return cleaned;
    }

    private static async Task<bool> DeleteOwnedPathWithRetriesAsync(string path)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                if (!Directory.Exists(path) && !File.Exists(path)) return true;
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    return false;
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else File.Delete(path);
                if (!Directory.Exists(path) && !File.Exists(path)) return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Processing tools may release their last handle just after the workflow exits.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)));
        }

        return !Directory.Exists(path) && !File.Exists(path);
    }
}
