using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

public enum WorkflowMode { FlacCueSplit, DsdExtraction, ExistingTrackRepair, MultipleAlbums, NeedsInspection, Completed, Unsupported }
public enum CheckState { Passed, Warning, Failed }
public enum JobPhase
{
    Ready = 0, Inventoried = 1, CopyingIn = 2, SourceCopyVerified = 3, Processing = 4,
    Tagging = 5, LocalVerificationPassed = 6, CopyingBack = 7, DestinationSizesVerified = 8,
    FinalCommit = 9, FinalVerificationPassed = 10, SourceDisposition = 11, CleanupCompleted = 12,
    Failed = 90, Canceled = 91
}

public sealed record MediaItem(string Path, string RelativePath, string Kind, long Size, string Note);
public sealed record ScanResult(
    string AlbumRoot, string AlbumName, WorkflowMode Mode, IReadOnlyList<MediaItem> Media,
    IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, long SourceBytes,
    int ImageCount, int TrackCount, int CueCount, bool HasFlac, bool HasDsd)
{
    public string WorkflowLabel => Mode switch
    {
        WorkflowMode.FlacCueSplit => "FLAC + CUE image split",
        WorkflowMode.DsdExtraction => "DSD / SACD extraction",
        WorkflowMode.ExistingTrackRepair => "Existing-track metadata repair",
        WorkflowMode.MultipleAlbums => "Multiple albums detected",
        WorkflowMode.NeedsInspection => "Needs inspection",
        WorkflowMode.Completed => "Already completed — no pending work",
        _ => "No supported source found"
    };
    public bool RequiresProcessing => Mode != WorkflowMode.Completed;
}

public sealed record PreflightCheck(string Name, CheckState State, string Detail, bool BlocksRun = false);
public sealed record PreflightResult(
    IReadOnlyList<PreflightCheck> Checks, string TempRoot, long RequiredBytes, long AvailableBytes,
    IReadOnlyDictionary<string, string?> Tools)
{
    public bool CanStart => Checks.All(item => !item.BlocksRun || item.State == CheckState.Passed);
}
public sealed record AlbumPreflightResult(int Index, ScanResult Scan, PreflightResult Preflight)
{
    public bool CanStart => Preflight.CanStart;
    public IReadOnlyList<PreflightCheck> Blockers => Preflight.Checks
        .Where(check => check.BlocksRun && check.State != CheckState.Passed)
        .ToArray();
    public string Detail
    {
        get
        {
            if (!CanStart) return string.Join("; ", Blockers.Select(check => $"{check.Name}: {check.Detail}"));
            var cleanup = Preflight.Checks.FirstOrDefault(check => check.Name == "Previous run cleanup");
            return cleanup is null ? Scan.WorkflowLabel : $"{Scan.WorkflowLabel}; {cleanup.Detail}";
        }
    }
}

public sealed record ProgressSnapshot(JobPhase Phase, int Percent, string Status, string Detail, DateTimeOffset UpdatedAt);
public sealed record ReportSummary(string Status, string Headline, string Detail, int Tracks, int Sections, bool Deleted, IReadOnlyList<string> Errors, string Json);

public static class SizeText
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];
    public static string Format(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {Units[unit]}";
    }
}

public sealed partial class AlbumScanner
{
    public Task<ScanResult> ScanAsync(string folder, CancellationToken token = default) =>
        Task.Run(() => Scan(folder, token), token);

    public Task<IReadOnlyList<ScanResult>> ScanAlbumsAsync(string folder, CancellationToken token = default) =>
        Task.Run<IReadOnlyList<ScanResult>>(() => ScanAlbums(folder, token), token);

    public IReadOnlyList<ScanResult> ScanAlbums(string folder, CancellationToken token = default)
    {
        var scan = Scan(folder, token);
        if (scan.Mode != WorkflowMode.MultipleAlbums) return [scan];

        var roots = AlbumRoots(scan.AlbumRoot, scan.Media.Where(IsAlbumRootInput));
        if (roots.Count < 2)
            throw new InvalidOperationException("The batch inventory did not resolve at least two disjoint album roots.");
        return roots.Select(root => Scan(root, token)).ToArray();
    }

    public ScanResult Scan(string folder, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose an album folder first.");
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var warnings = new List<string>();
        var errors = new List<string>();
        string[] files;
        try { files = Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException($"Could not inventory this album: {error.Message}", error); }

        var previousPlans = files
            .Where(path => Path.GetFileName(path).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => PreviousOutputCleanupService.Discover(Path.GetDirectoryName(path)!))
            .Where(plan => plan is not null)
            .Cast<PreviousOutputPlan>()
            .ToArray();
        var verifiedPreviousPlans = files
            .Where(path => Path.GetFileName(path).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => PreviousOutputCleanupService.DiscoverVerified(Path.GetDirectoryName(path)!))
            .Where(plan => plan is not null)
            .Cast<VerifiedOutputPlan>()
            .ToArray();
        var completedPlans = files
            .Where(path => Path.GetFileName(path).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase))
            .Select(path => PreviousOutputCleanupService.DiscoverCompleted(Path.GetDirectoryName(path)!) ??
                            PreviousOutputCleanupService.DiscoverRecoverableStaleFallback(Path.GetDirectoryName(path)!, token))
            .Where(plan => plan is not null)
            .Cast<CompletedOutputPlan>()
            .ToArray();
        var previousOutputs = previousPlans.SelectMany(plan => plan.Files)
            .Concat(verifiedPreviousPlans.SelectMany(plan => plan.Files))
            .Concat(completedPlans.SelectMany(plan => plan.Files))
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cues = files.Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)).ToArray();
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in cues)
        {
            try
            {
                foreach (var line in File.ReadLines(cue))
                {
                    var match = CueFile().Match(line);
                    if (!match.Success) continue;
                    var name = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["u"].Value;
                    references.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cue)!, name)));
                }
            }
            catch (IOException error) { warnings.Add($"Could not read {Path.GetFileName(cue)}: {error.Message}"); }
        }

        var media = new List<MediaItem>();
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var name = Path.GetFileName(path).ToLowerInvariant();
            var referenced = references.Contains(path);
            var (kind, note) = ext switch
            {
                ".cue" => ("CUE sheet", "Track boundaries and edition evidence"),
                ".iso" => ("SACD / DSD image", "Signature must be probed before extraction"),
                ".flac" when previousOutputs.Contains(path) => ("Previous Album Fixer output", "Report-proven output; root files are replaced only after new tracks verify"),
                ".flac" when PreviousOutputCleanupService.IsInnerTracksFile(root, path) => ("Inner-folder FLAC", "Retained legacy inner-folder track; new tracks are written at the album root"),
                ".flac" when referenced => ("FLAC image", "Referenced by CUE"),
                ".flac" => ("Existing FLAC", "Individual track candidate"),
                ".dsf" when previousOutputs.Contains(path) => ("Previous Album Fixer output", "Report-proven SACD output"),
                ".dsf" when referenced => ("DSF image", "Large DSD source referenced by CUE"),
                ".dsf" => ("Existing DSF", "Individual track candidate"),
                ".dff" when previousOutputs.Contains(path) => ("Previous Album Fixer output", "Report-proven SACD output"),
                ".dff" when referenced => ("DFF image", "DSDIFF source referenced by CUE"),
                ".dff" => ("Existing DFF", "Individual track candidate"),
                ".dst" => ("DST stream", "Losslessly compressed DSD; probe required"),
                ".dsd" => ("Raw DSD", "Ambiguous extension; bit order and layout must be established"),
                ".jpg" or ".jpeg" or ".png" when name.Contains("cover") || name.Contains("front") || name == "folder.jpg" => ("Artwork", "Local artwork candidate"),
                ".log" or ".txt" or ".pdf" or ".m3u" or ".m3u8" or ".ddp" => ("Provenance", "Preserved log, scan, or playlist"),
                _ => (string.Empty, string.Empty)
            };
            if (kind.Length == 0) continue;
            long size = 0;
            try { size = new FileInfo(path).Length; } catch (IOException) { }
            media.Add(new MediaItem(path, Path.GetRelativePath(root, path), kind, size, note));
        }

        var images = media.Where(item => item.Kind.Contains("image", StringComparison.OrdinalIgnoreCase) || item.Kind is "DST stream" or "Raw DSD").ToArray();
        var tracks = media.Where(item => item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase)).ToArray();
        var completedPlan = completedPlans.FirstOrDefault(plan => plan.AlbumRoot.Equals(root, StringComparison.OrdinalIgnoreCase));
        var completedSources = completedPlan?.SourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var completed = completedPlan is not null && tracks.Length == 0 &&
            images.All(item => completedSources.Contains(item.Path));
        if (completedPlan?.RecoveredFromStaleFallback == true)
            warnings.Add(completedPlan.RecoveryDetail ?? "Completion was recovered from a verified stale fallback state.");
        if (!completed)
            foreach (var missing in references.Where(path => !File.Exists(path)))
                errors.Add($"CUE references a missing source: {Path.GetRelativePath(root, missing)}");
        if (previousOutputs.Count > 0 && !completed)
            warnings.Add($"Found {previousOutputs.Count} report-proven output file{(previousOutputs.Count == 1 ? "" : "s")} from an earlier Album Fixer run. Root-level outputs are replaced only after new tracks verify; inner-folder tracks are retained.");

        var albumRoots = AlbumRoots(root, media.Where(IsAlbumRootInput));
        WorkflowMode mode;
        if (albumRoots.Count > 1)
        {
            mode = WorkflowMode.MultipleAlbums;
            warnings.Add($"This folder contains {albumRoots.Count} independent albums. Batch mode will use a hardware-aware bounded copy/process/write-back pipeline.");
        }
        else if (completed) mode = WorkflowMode.Completed;
        else if (tracks.Length >= 2)
        {
            mode = WorkflowMode.ExistingTrackRepair;
            if (images.Length > 0) warnings.Add("Separated tracks coexist with an image. Repair-only mode takes precedence; the image stays until equivalence is proven.");
        }
        else if (media.Any(item => item.Kind == "FLAC image")) mode = errors.Count == 0 ? WorkflowMode.FlacCueSplit : WorkflowMode.NeedsInspection;
        else if (media.Any(item => item.Kind is "SACD / DSD image" or "DSF image" or "DFF image" or "DST stream")) mode = WorkflowMode.DsdExtraction;
        else if (media.Any(item => item.Kind == "Raw DSD") || tracks.Length == 1) mode = WorkflowMode.NeedsInspection;
        else { mode = WorkflowMode.Unsupported; errors.Add("No supported FLAC, ISO, DSF, DFF, DST, or DSD source was found."); }

        var sourceBytes = images.Length > 0 ? images.Sum(item => item.Size) : tracks.Sum(item => item.Size);
        return new ScanResult(root, new DirectoryInfo(root).Name, mode,
            media.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), warnings, errors,
            sourceBytes, images.Length, tracks.Length, cues.Length,
            media.Any(item => item.Kind.Contains("FLAC", StringComparison.OrdinalIgnoreCase)),
            media.Any(item => item.Kind.Contains("DS", StringComparison.OrdinalIgnoreCase) || item.Kind.Contains("SACD", StringComparison.OrdinalIgnoreCase)));
    }

    private static IReadOnlyList<string> AlbumRoots(string root, IEnumerable<MediaItem> media) =>
        media.Select(item => AlbumScope(root, item.Path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsAlbumRootInput(MediaItem item) =>
        item.Kind.Contains("image", StringComparison.OrdinalIgnoreCase) ||
        item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase) ||
        item.Kind == "Previous Album Fixer output" ||
        item.Kind is "DST stream" or "Raw DSD";

    private static string AlbumScope(string root, string mediaPath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var directory = Path.GetDirectoryName(Path.GetFullPath(mediaPath))
            ?? throw new InvalidOperationException($"Media file has no parent folder: {mediaPath}");
        if (directory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) return fullRoot;
        if (DiscOrAreaFolder().IsMatch(Path.GetFileName(directory)))
            directory = Directory.GetParent(directory)?.FullName ?? directory;
        if (Path.GetFileName(directory).Equals("Tracks", StringComparison.OrdinalIgnoreCase))
            directory = Directory.GetParent(directory)?.FullName ?? directory;
        return IsWithin(fullRoot, directory) ? directory : fullRoot;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    [GeneratedRegex("^(?:(?:cd|disc|disk)\\s*[-_. ]*\\d+|stereo|multichannel)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscOrAreaFolder();

    [GeneratedRegex("^\\s*FILE\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))\\s+\\S+", RegexOptions.IgnoreCase)]
    private static partial Regex CueFile();
}

public sealed class PreflightService
{
    public async Task<PreflightResult> CheckBatchAsync(
        IReadOnlyList<ScanResult> scans,
        CancellationToken token = default)
    {
        var albums = await CheckAlbumsAsync(scans, token);
        return CombineBatch(albums);
    }

    public async Task<IReadOnlyList<AlbumPreflightResult>> CheckAlbumsAsync(
        IReadOnlyList<ScanResult> scans,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(scans);
        if (scans.Count == 0) throw new ArgumentException("A batch must contain at least one album.", nameof(scans));
        var albums = new List<AlbumPreflightResult>(scans.Count);
        for (var index = 0; index < scans.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            albums.Add(new(index, scans[index], await CheckAsync(scans[index], token)));
        }
        return albums;
    }

    public static PreflightResult CombineBatch(IReadOnlyList<AlbumPreflightResult> albums)
    {
        ArgumentNullException.ThrowIfNull(albums);
        if (albums.Count == 0) throw new ArgumentException("A batch must contain at least one album.", nameof(albums));
        var first = albums[0].Preflight;
        var runnable = albums.Where(album => album.CanStart).ToArray();
        var available = albums.Min(album => album.Preflight.AvailableBytes);
        var pipeline = PipelineLimits(albums);
        var required = runnable.Select(album => album.Preflight.RequiredBytes)
            .OrderByDescending(bytes => bytes)
            .Take(pipeline.MaxInFlight)
            .Aggregate(0L, SaturatingAdd);
        var checks = first.Checks
            .Where(check => check.Name is not "Local staging capacity" and not "Album classification" and not "Inventory" and
                            not "Verified write-back" and not "Previous run cleanup" and not "Source cache")
            .Select(check => check with { BlocksRun = false })
            .ToList();
        checks.Add(new("Pipeline scheduler", runnable.Length > 0 ? CheckState.Passed : CheckState.Failed,
            runnable.Length == 0
                ? $"0 of {albums.Count} albums admitted."
                : $"{runnable.Length} of {albums.Count} album{(albums.Count == 1 ? "" : "s")} admitted; {pipeline.Description}."));
        checks.Add(available >= required
            ? new("Concurrent staging capacity", CheckState.Passed,
                $"{SizeText.Format(available)} available; {SizeText.Format(required)} estimated for admitted simultaneous jobs.")
            : new("Concurrent staging capacity", CheckState.Failed,
                $"{SizeText.Format(required)} estimated for simultaneous jobs; only {SizeText.Format(available)} available.", true));

        foreach (var album in albums)
        {
            checks.Add(album.CanStart
                ? new($"Album {album.Index + 1}: {album.Scan.AlbumName}", CheckState.Passed, album.Detail)
                : new($"Album {album.Index + 1}: {album.Scan.AlbumName}", CheckState.Failed, album.Detail));
        }
        checks.Add(runnable.Length > 0
            ? new("Runnable albums", CheckState.Passed, $"{runnable.Length} album{(runnable.Length == 1 ? "" : "s")} can start; blocked siblings will be skipped.", true)
            : new("Runnable albums", CheckState.Failed, "No album passed all blocking preflight checks.", true));

        return new(checks, first.TempRoot, required, available, first.Tools);
    }

    public static int WorkerLimit(IReadOnlyList<AlbumPreflightResult> albums)
        => PipelineLimits(albums).MaxInFlight;

    public static BatchPipelineLimits PipelineLimits(IReadOnlyList<AlbumPreflightResult> albums)
    {
        var runnable = albums.Where(album => album.CanStart).ToArray();
        if (runnable.Length == 0) return BatchPipelineLimits.None;
        var available = runnable.Min(album => album.Preflight.AvailableBytes);
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (memory <= 0) memory = 8L * 1024 * 1024 * 1024;
        return BatchPipelineLimits.Recommend(
            runnable.Select(album => album.Preflight.RequiredBytes).ToArray(),
            available,
            Environment.ProcessorCount,
            memory);
    }

    public async Task<PreflightResult> CheckAsync(ScanResult scan, CancellationToken token = default)
    {
        var tools = await FindToolsAsync(token);
        var checks = new List<PreflightCheck>
        {
            new("Metadata enrichment", CheckState.Passed, "Missing album metadata is handled by deterministic local code using the configured public catalog lookups.")
        };
        if (scan.HasFlac || scan.HasDsd)
        {
            Require(checks, "ffmpeg", tools["ffmpeg"], scan.HasFlac
                ? "local FLAC splitting and in-memory artwork normalization"
                : "in-memory SACD artwork normalization");
            Require(checks, "ffprobe", tools["ffprobe"], "stream, tag, and artwork verification");
        }
        var sacdImages = scan.Media.Where(item => item.Kind == "SACD / DSD image").ToArray();
        if (sacdImages.Length > 0)
            Require(checks, "sacd_extract", tools["sacd_extract"], "SACD ISO layout inspection and DSF extraction");
        var previousOutput = PreviousOutputCleanupService.Discover(scan.AlbumRoot);
        if (previousOutput is not null)
            checks.Add(new("Previous run cleanup", CheckState.Warning,
                $"{previousOutput.Files.Count} report-proven legacy track{(previousOutput.Files.Count == 1 ? "" : "s")} will be deleted before staging; the old report will be archived."));
        var verifiedPreviousOutput = PreviousOutputCleanupService.DiscoverVerified(scan.AlbumRoot);
        var directVerifiedFiles = PreviousOutputCleanupService.DirectFiles(verifiedPreviousOutput);
        if (directVerifiedFiles.Count > 0)
            checks.Add(new("Previous run replacement", CheckState.Warning,
                $"{directVerifiedFiles.Count} report-proven root output file{(directVerifiedFiles.Count == 1 ? "" : "s")} will be replaced only after the new tracks pass verification."));

        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "album-fixer"));
        var sourceCacheRequired = HostStagingService.RequiresSourceCache(scan.AlbumRoot);
        checks.Add(sourceCacheRequired
            ? new("Source cache", CheckState.Passed, "NAS/network source: the album will be copied to Windows Temp and checked by file size before processing.")
            : new("Source cache", CheckState.Passed, "Fixed local source: files will be read in place and checked by file size; no source copy will be stored in Windows Temp."));
        long available = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(tempRoot)!);
            available = drive.AvailableFreeSpace;
            checks.Add(drive.DriveType == DriveType.Fixed ? new("Windows Temp", CheckState.Passed, $"Local fixed staging volume: {tempRoot}") : new("Windows Temp", CheckState.Failed, "Windows Temp is not on a local fixed drive.", true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { checks.Add(new("Windows Temp", CheckState.Failed, error.Message, true)); }
        var factor = scan.Mode == WorkflowMode.DsdExtraction ? 7.2 : scan.Mode == WorkflowMode.ExistingTrackRepair ? 3.0 : 3.84;
        if (!sourceCacheRequired) factor = Math.Max(1.0, factor - 1.0);
        var required = (long)(Math.Max(scan.SourceBytes, 512L * 1024 * 1024) * factor);
        checks.Add(available >= required ? new("Local staging capacity", CheckState.Passed, $"{SizeText.Format(available)} available; {SizeText.Format(required)} estimated.") : new("Local staging capacity", CheckState.Failed, $"{SizeText.Format(required)} estimated; only {SizeText.Format(available)} available.", true));
        checks.Add(scan.Mode switch
        {
            WorkflowMode.Unsupported or WorkflowMode.MultipleAlbums or WorkflowMode.Completed => new("Album classification", CheckState.Failed, scan.WorkflowLabel, true),
            WorkflowMode.NeedsInspection => new("Album classification", CheckState.Failed, "The source type could not be classified deterministically; the original will be retained.", true),
            _ => new("Album classification", CheckState.Passed, scan.WorkflowLabel)
        });
        if (scan.Mode == WorkflowMode.DsdExtraction)
        {
            var otherDsdSources = scan.Media.Count(item => item.Kind is "DSF image" or "DFF image" or "DST stream" or "Raw DSD");
            checks.Add(sacdImages.Length == 1 && otherDsdSources == 0
                ? new("Verified write-back", CheckState.Passed, "Host-managed SACD ISO extraction, DSD verification, and transactional final placement are enabled.")
                : new("Verified write-back", CheckState.Failed, "This release supports one SACD ISO per album. Other DSD source types remain read-only.", true));
        }
        else if (scan.Mode is not WorkflowMode.FlacCueSplit and not WorkflowMode.MultipleAlbums and not WorkflowMode.Unsupported)
            checks.Add(new("Verified write-back", CheckState.Failed, "Host-managed final placement is enabled for FLAC + CUE and single SACD ISO workflows. Other modes stop before changing files.", true));
        checks.AddRange(scan.Errors.Select(error => new PreflightCheck("Inventory", CheckState.Failed, error, true)));
        return new(checks, tempRoot, required, available, tools);
    }

    public async Task<IReadOnlyDictionary<string, string?>> FindToolsAsync(CancellationToken token = default)
    {
        var names = new[] { "ffmpeg", "ffprobe", "sacd_extract" };
        var tasks = names.ToDictionary(name => name, name => FindToolAsync(name, token));
        await Task.WhenAll(tasks.Values);
        return tasks.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);
    }

    public static string CreateJobDirectory(string tempRoot)
    {
        var root = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        var path = Path.GetFullPath(Path.Combine(root, $"ui-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe Temp job path.");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Require(ICollection<PreflightCheck> checks, string name, string? path, string reason) =>
        checks.Add(path is not null ? new(name, CheckState.Passed, path) : new(name, CheckState.Failed, $"Required for {reason}.", true));

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static async Task<string?> FindToolAsync(string name, CancellationToken token)
    {
        var exe = Path.HasExtension(name) ? name : name + ".exe";
        var assemblyRoot = Path.GetDirectoryName(typeof(PreflightService).Assembly.Location);
        var bundled = Path.Combine(string.IsNullOrWhiteSpace(assemblyRoot) ? AppContext.BaseDirectory : assemblyRoot, "Tools", exe);
        if (File.Exists(bundled)) return Path.GetFullPath(bundled);

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var path = Path.Combine(directory.Trim('"'), exe); if (File.Exists(path)) return Path.GetFullPath(path); }
            catch (ArgumentException) { }
        }

        foreach (var linkRoot in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links")
        })
        {
            var candidate = Path.Combine(linkRoot, exe);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }

        if (name.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase) || name.Equals("ffprobe", StringComparison.OrdinalIgnoreCase))
        {
            var packageRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            try
            {
                var packaged = Directory.EnumerateDirectories(packageRoot, "Gyan.FFmpeg_*", SearchOption.TopDirectoryOnly)
                    .SelectMany(directory => Directory.EnumerateFiles(directory, exe, SearchOption.AllDirectories))
                    .FirstOrDefault();
                if (packaged is not null) return Path.GetFullPath(packaged);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }

        if (name.Equals("sacd_extract", StringComparison.OrdinalIgnoreCase))
        {
            var albumFixerTools = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AlbumFixer", "Tools", "sacd_extract");
            try
            {
                var installed = Directory.EnumerateFiles(albumFixerTools, exe, SearchOption.AllDirectories)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (installed is not null) return Path.GetFullPath(installed);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { }
        }

        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "where.exe")) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        info.ArgumentList.Add(name);
        try
        {
            using var process = Process.Start(info);
            if (process is not null)
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(token);
                var errorTask = process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token); await errorTask;
                var output = await outputTask;
                var discovered = process.ExitCode == 0 ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists) : null;
                if (discovered is not null) return discovered;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        return null;
    }
}


public static class ReportReader
{
    public static async Task<ReportSummary> LoadAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token); var root = doc.RootElement;
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        var album = Get(root, "album") ?? "Album conversion"; var edition = Get(root, "edition"); var workflow = Get(root, "workflow_mode") ?? Get(root, "source_type");
        var verification = Prop(root, "verification", out var v) ? v : default; var status = verification.ValueKind == JsonValueKind.Object ? Get(verification, "status") ?? "pending" : "pending";
        var deleted = verification.ValueKind == JsonValueKind.Object && Bool(verification, "sources_deleted");
        var errors = new List<string>(); if (verification.ValueKind == JsonValueKind.Object && Prop(verification, "errors", out var e) && e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) errors.Add(item.ToString());
        if (verification.ValueKind == JsonValueKind.Object && Prop(verification, "warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array) foreach (var item in warnings.EnumerateArray()) errors.Add(item.ToString());
        var tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Files(root, tracks);
        var sections = Count(root, "discs") + Count(root, "areas") + Count(root, "audio_areas");
        var label = status.ToLowerInvariant() switch { "passed" => "Verification passed", "incomplete" => "Tracks ready · artwork incomplete", "failed" => "Verification failed", "blocked" => "Run blocked safely", "canceled" => "Run canceled safely", _ => "Report pending" };
        return new(status, $"{album} · {label}", string.Join("  •  ", new[] { edition, workflow?.Replace('_', ' '), verification.ValueKind == JsonValueKind.Object ? Get(verification, "method") : null }.Where(x => !string.IsNullOrWhiteSpace(x))), tracks.Count, sections, deleted, errors, json);
    }
    private static string? Get(JsonElement e, string n) => Prop(e, n, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ToString() : null;
    private static bool Bool(JsonElement e, string n) => Prop(e, n, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False && p.GetBoolean();
    private static bool Prop(JsonElement e, string n, out JsonElement p) { if (e.ValueKind == JsonValueKind.Object) foreach (var item in e.EnumerateObject()) if (item.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) { p = item.Value; return true; } p = default; return false; }
    private static int Count(JsonElement e, string n) => Prop(e, n, out var p) && p.ValueKind == JsonValueKind.Array ? p.GetArrayLength() : 0;
    private static void Files(JsonElement e, ISet<string> files)
    {
        if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) { if (p.Name.Equals("file", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { } file && new[] { ".flac", ".dsf", ".dff" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) files.Add(file); else Files(p.Value, files); }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) Files(item, files);
    }
}
