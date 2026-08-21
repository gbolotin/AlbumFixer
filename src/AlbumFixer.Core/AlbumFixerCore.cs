using System.Diagnostics;
using System.Globalization;
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

public sealed record InventoryProgress(int Completed, int Total, string Stage, string CurrentItem)
{
    public int Percent => Total <= 0 ? 0 : Math.Clamp((int)Math.Round(Completed * 100d / Total), 0, 100);
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
    public static int InventoryWorkerLimit => Math.Clamp(Environment.ProcessorCount, 1, 4);

    public Task<ScanResult> ScanAsync(string folder, CancellationToken token = default) =>
        ScanAsync(folder, progress: null, token);

    public Task<ScanResult> ScanAsync(
        string folder,
        IProgress<InventoryProgress>? progress,
        CancellationToken token = default) =>
        Task.Run(() => Scan(folder, progress, token, excludedAlbumRoots: null), token);

    public async Task<IReadOnlyList<ScanResult>> ScanAlbumsAsync(string folder, CancellationToken token = default)
    {
        var scan = await ScanAsync(folder, token).ConfigureAwait(false);
        return await ScanAlbumsAsync(scan, progress: null, token).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ScanResult>> ScanAlbumsAsync(
        ScanResult scan,
        IProgress<InventoryProgress>? progress = null,
        CancellationToken token = default)
    {
        if (scan.Mode != WorkflowMode.MultipleAlbums) return [scan];
        var roots = ResolveAlbumRoots(scan);

        var results = new ScanResult[roots.Count];
        var completed = 0;
        var progressGate = new object();
        progress?.Report(new(0, roots.Count, $"Scanning album folders with up to {InventoryWorkerLimit} workers", roots[0]));
        await Parallel.ForEachAsync(Enumerable.Range(0, roots.Count), new ParallelOptions
        {
            MaxDegreeOfParallelism = InventoryWorkerLimit,
            CancellationToken = token
        }, async (index, itemToken) =>
        {
            results[index] = await ScanAsync(roots[index], itemToken).ConfigureAwait(false);
            lock (progressGate)
            {
                completed++;
                progress?.Report(new(completed, roots.Count,
                    $"Scanning album folders with up to {InventoryWorkerLimit} workers", roots[index]));
            }
        }).ConfigureAwait(false);
        return results;
    }

    public IReadOnlyList<string> ResolveAlbumRoots(ScanResult scan)
    {
        if (scan.Mode != WorkflowMode.MultipleAlbums) return [scan.AlbumRoot];
        var roots = AlbumRoots(scan.AlbumRoot, scan.Media.Where(IsAlbumRootInput));
        if (roots.Count < 2)
            throw new InvalidOperationException("The batch inventory did not resolve at least two disjoint album roots.");
        return roots;
    }

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
        => Scan(folder, progress: null, token, excludedAlbumRoots: null);

    private ScanResult Scan(
        string folder,
        IProgress<InventoryProgress>? progress,
        CancellationToken token,
        IReadOnlyList<string>? excludedAlbumRoots)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Choose an album folder first.");
        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var warnings = new List<string>();
        var errors = new List<string>();
        string[] files;
        progress?.Report(new(0, 0, "Discovering files", new DirectoryInfo(root).Name));
        try
        {
            files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                .Where(path => !IsTransientAlbumFixerPath(root, path))
                .Where(path => excludedAlbumRoots is null ||
                               !excludedAlbumRoots.Any(excluded => IsWithin(excluded, path)))
                .ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new IOException($"Could not inventory this album: {error.Message}", error); }

        var reportPaths = files
            .Where(path => Path.GetFileName(path).Equals("conversion-report.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var cues = files.Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)).ToArray();
        var totalSteps = reportPaths.Length * 3 + cues.Length + files.Length;
        var completedSteps = 0;
        var scanProgressGate = new object();
        void Advance(string stage, string path)
        {
            var relative = Path.GetRelativePath(root, path);
            lock (scanProgressGate)
            {
                completedSteps++;
                progress?.Report(new(completedSteps, totalSteps, stage,
                    relative.Equals(".", StringComparison.Ordinal) ? new DirectoryInfo(root).Name : relative));
            }
        }

        var previousPlanResults = new PreviousOutputPlan?[reportPaths.Length];
        var verifiedPlanResults = new VerifiedOutputPlan?[reportPaths.Length];
        var completedPlanResults = new CompletedOutputPlan?[reportPaths.Length];
        Parallel.For(0, reportPaths.Length, new ParallelOptions
        {
            MaxDegreeOfParallelism = InventoryWorkerLimit,
            CancellationToken = token
        }, index =>
        {
            token.ThrowIfCancellationRequested();
            var reportPath = reportPaths[index];
            var reportRoot = Path.GetDirectoryName(reportPath)!;

            previousPlanResults[index] = PreviousOutputCleanupService.Discover(reportRoot);
            Advance("Reading previous-run reports", reportRoot);
            verifiedPlanResults[index] = PreviousOutputCleanupService.DiscoverVerified(reportRoot);
            Advance("Checking prior output evidence", reportRoot);
            completedPlanResults[index] = PreviousOutputCleanupService.DiscoverCompleted(reportRoot) ??
                                          PreviousOutputCleanupService.DiscoverRecoverableStaleFallback(reportRoot, token);
            Advance("Verifying completion evidence", reportRoot);
        });
        var previousPlans = previousPlanResults.OfType<PreviousOutputPlan>().ToArray();
        var verifiedPreviousPlans = verifiedPlanResults.OfType<VerifiedOutputPlan>().ToArray();
        var completedPlans = completedPlanResults.OfType<CompletedOutputPlan>().ToArray();
        var previousOutputs = previousPlans.SelectMany(plan => plan.Files)
            .Concat(verifiedPreviousPlans.SelectMany(plan => plan.Files))
            .Concat(completedPlans.SelectMany(plan => plan.Files))
            .Select(file => file.FullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
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
            Advance("Reading CUE references", cue);
        }
        var trackPerFileCue = AreTrackPerFileCueSheets(cues, references);
        var verifiedLegacySplitCue = IsVerifiedLegacySplitCue(root, cues, references, files);

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
                ".flac" when referenced && trackPerFileCue =>
                    ("Existing FLAC", "Already-separated track referenced by a one-file-per-track CUE"),
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
                ".jpg" or ".jpeg" or ".png" or ".tif" or ".tiff" when name.Contains("cover") || name.Contains("front") || name is "folder.jpg" or "folder.tif" or "folder.tiff" => ("Artwork", "Local artwork candidate"),
                ".md5" or ".sfv" => ("Provenance", "Preserved checksum manifest and album identity evidence"),
                ".log" or ".txt" or ".pdf" or ".m3u" or ".m3u8" or ".ddp" => ("Provenance", "Preserved log, scan, or playlist"),
                _ => (string.Empty, string.Empty)
            };
            if (kind.Length > 0)
            {
                long size = 0;
                try { size = new FileInfo(path).Length; } catch (IOException) { }
                media.Add(new MediaItem(path, Path.GetRelativePath(root, path), kind, size, note));
            }
            Advance("Classifying media", path);
        }

        var images = media.Where(item => item.Kind.Contains("image", StringComparison.OrdinalIgnoreCase) || item.Kind is "DST stream" or "Raw DSD").ToArray();
        var tracks = media.Where(item => item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase)).ToArray();
        var repairTracks = tracks.Where(item => item.Kind is "Existing FLAC" or "Existing DSF" or "Existing DFF").ToArray();
        var repairKind = repairTracks.Select(item => item.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var completedPlan = completedPlans.FirstOrDefault(plan => plan.AlbumRoot.Equals(root, StringComparison.OrdinalIgnoreCase));
        var completedSources = completedPlan?.SourcePaths.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        var hasDsdRepairHistory = repairKind.Length == 1 && repairKind[0] is "Existing DSF" or "Existing DFF" &&
                                  File.Exists(Path.Combine(root, "conversion-report.json"));
        var completeExistingTrackSet = images.Length == 0 && repairTracks.Length == tracks.Length &&
                                       !hasDsdRepairHistory &&
                                       PreviousOutputCleanupService.HasCompleteExistingTrackSet(repairTracks);
        var completed = completeExistingTrackSet || completedPlan is not null && tracks.Length == 0 &&
            images.All(item => completedSources.Contains(item.Path));
        if (completeExistingTrackSet)
        {
            var format = repairKind[0].Replace("Existing ", string.Empty, StringComparison.OrdinalIgnoreCase);
            warnings.Add(references.Any(path => !File.Exists(path))
                ? $"The already-separated {format} tracks form a complete multi-disc or multi-area set with required tags and embedded artwork. Missing source files named by preserved CUE sheets were accepted as historical provenance; no repair or external lookup is required."
                : $"The already-separated {format} tracks form a complete set with required tags and embedded artwork; no repair or external lookup is required.");
        }
        if (completedPlan?.RecoveredFromStaleFallback == true)
            warnings.Add(completedPlan.RecoveryDetail ?? "Completion was recovered from a verified stale fallback state.");
        if (!completed && !verifiedLegacySplitCue)
            foreach (var missing in references.Where(path => !File.Exists(path)))
                errors.Add($"CUE references a missing source: {Path.GetRelativePath(root, missing)}");
        if (previousOutputs.Count > 0 && !completed)
            warnings.Add($"Found {previousOutputs.Count} report-proven output file{(previousOutputs.Count == 1 ? "" : "s")} from an earlier Album Fixer run. Root-level outputs are replaced only after new tracks verify; inner-folder tracks are retained.");

        var albumRoots = AlbumRoots(root, media.Where(IsAlbumRootInput));
        if (excludedAlbumRoots is null && albumRoots.Count > 1 &&
            albumRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            var nestedAlbumRoots = albumRoots
                .Where(path => !path.Equals(root, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            return Scan(root, progress, token, nestedAlbumRoots);
        }
        if (albumRoots.Count == 1 && !albumRoots[0].Equals(root, StringComparison.OrdinalIgnoreCase))
            return Scan(albumRoots[0], progress, token, excludedAlbumRoots: null);

        WorkflowMode mode;
        if (albumRoots.Count > 1)
        {
            mode = WorkflowMode.MultipleAlbums;
            warnings.Add($"This folder contains {albumRoots.Count} independent albums. Batch mode will use a hardware-aware bounded copy/process/write-back pipeline.");
        }
        else if (completed) mode = WorkflowMode.Completed;
        else if (tracks.Length > 0)
        {
            if (repairTracks.Length >= 2 && repairTracks.Length == tracks.Length && repairKind.Length == 1)
            {
                mode = WorkflowMode.ExistingTrackRepair;
                if (images.Length > 0) warnings.Add((repairKind[0] is "Existing DSF" or "Existing DFF") && images.Length == 1 && images[0].Kind == "SACD / DSD image"
                    ? "Separated DSD tracks coexist with one retained SACD ISO. The ISO is eligible for deletion only when Delete originals is selected and every repaired track passes final payload, tag, and artwork verification."
                    : "Separated tracks coexist with an image. Repair-only mode takes precedence and the image remains untouched.");
                if (trackPerFileCue)
                    warnings.Add("A one-file-per-track CUE was retained as provenance; the already-separated FLAC files will use verified repair-only write-back.");
                if (verifiedLegacySplitCue)
                    warnings.Add("The missing source file or files named by the legacy CUE were accepted as historical provenance because the complete root FLAC set has matching sequential numbers and normalized CUE titles; the tracks will continue through structural and metadata verification.");
                if (repairKind[0].Equals("Existing DSF", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("Standalone DSF tracks will be repaired transactionally; the native DSD data chunk must remain byte-identical through final write-back.");
                if (repairKind[0].Equals("Existing DFF", StringComparison.OrdinalIgnoreCase))
                    warnings.Add("Standalone DFF tracks will be repaired transactionally through a native DSDIFF ID3 writer; the DSD audio chunk must remain byte-identical through final write-back.");
            }
            else
            {
                mode = WorkflowMode.NeedsInspection;
                errors.Add(repairTracks.Length == tracks.Length && repairKind.Length <= 1
                    ? "Existing-track repair requires at least two standalone FLAC, DSF, or DFF tracks."
                    : "Mixed standalone track formats are not eligible for one automatic repair transaction.");
            }
        }
        else if (media.Any(item => item.Kind == "FLAC image")) mode = errors.Count == 0 ? WorkflowMode.FlacCueSplit : WorkflowMode.NeedsInspection;
        else if (media.Any(item => item.Kind is "SACD / DSD image" or "DSF image" or "DFF image" or "DST stream")) mode = WorkflowMode.DsdExtraction;
        else if (media.Any(item => item.Kind == "Raw DSD") || tracks.Length == 1) mode = WorkflowMode.NeedsInspection;
        else { mode = WorkflowMode.Unsupported; errors.Add("No supported FLAC, ISO, DSF, DFF, DST, or DSD source was found."); }

        var sourceBytes = images.Length > 0 ? images.Sum(item => item.Size) : tracks.Sum(item => item.Size);
        var result = new ScanResult(root, new DirectoryInfo(root).Name, mode,
            media.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), warnings, errors,
            sourceBytes, images.Length, tracks.Length, cues.Length,
            media.Any(item => item.Kind.Contains("FLAC", StringComparison.OrdinalIgnoreCase)),
            media.Any(item => item.Kind.Contains("DS", StringComparison.OrdinalIgnoreCase) || item.Kind.Contains("SACD", StringComparison.OrdinalIgnoreCase)));
        progress?.Report(new(Math.Max(totalSteps, 1), Math.Max(totalSteps, 1), "Inventory complete", result.AlbumName));
        return result;
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

    private static bool IsTransientAlbumFixerPath(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.StartsWith(".album-fixer-", StringComparison.OrdinalIgnoreCase));

    private static bool AreTrackPerFileCueSheets(IReadOnlyList<string> cues, IReadOnlySet<string> references)
    {
        // Two one-track images are indistinguishable from a conventional two-disc
        // image set. Require album track scale before choosing track-per-file repair.
        if (cues.Count == 0 || references.Count < 3) return false;
        var allSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var cue in cues)
        {
            var sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var audioTracks = 0;
            var zeroIndexes = 0;
            var currentTrackHasZeroIndex = false;
            foreach (var line in File.ReadLines(cue))
            {
                var file = CueFile().Match(line);
                if (file.Success)
                {
                    var name = file.Groups["q"].Success ? file.Groups["q"].Value : file.Groups["u"].Value;
                    var source = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cue)!, name));
                    if (!Path.GetExtension(source).Equals(".flac", StringComparison.OrdinalIgnoreCase) ||
                        !sourceFiles.Add(source) || !allSources.Add(source))
                        return false;
                    continue;
                }

                if (CueAudioTrack().IsMatch(line))
                {
                    if (audioTracks > 0 && !currentTrackHasZeroIndex) return false;
                    audioTracks++;
                    currentTrackHasZeroIndex = false;
                    continue;
                }

                if (audioTracks > 0 && CueIndex01Zero().IsMatch(line) && !currentTrackHasZeroIndex)
                {
                    currentTrackHasZeroIndex = true;
                    zeroIndexes++;
                }
            }
            if (audioTracks == 0 || !currentTrackHasZeroIndex ||
                sourceFiles.Count != audioTracks || zeroIndexes != audioTracks)
                return false;
        }
        return allSources.SetEquals(references);
    }

    private static bool IsVerifiedLegacySplitCue(
        string root,
        IReadOnlyList<string> cues,
        IReadOnlySet<string> references,
        IReadOnlyList<string> files)
    {
        if (cues.Count == 0 || references.Count == 0 || references.Any(File.Exists)) return false;

        var cueDirectory = Path.GetDirectoryName(cues[0])!;
        if (!Path.GetFullPath(cueDirectory).Equals(Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase) ||
            cues.Any(cue => !Path.GetDirectoryName(cue)!.Equals(cueDirectory, StringComparison.OrdinalIgnoreCase)))
            return false;
        var allFlacs = files.Where(path => Path.GetExtension(path).Equals(".flac", StringComparison.OrdinalIgnoreCase)).ToArray();
        var rootFlacs = allFlacs.Where(path => Path.GetDirectoryName(path)!.Equals(cueDirectory, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (rootFlacs.Length < 2 || rootFlacs.Length != allFlacs.Length) return false;

        Dictionary<int, string>? cueTitles = null;
        foreach (var cue in cues)
        {
            if (!TryReadCueAudioTracks(cue, out var candidateTitles)) return false;
            if (cueTitles is null) cueTitles = candidateTitles;
            else if (cueTitles.Count != candidateTitles.Count || cueTitles.Any(pair =>
                         !candidateTitles.TryGetValue(pair.Key, out var candidate) ||
                         !CueTitleIdentity(pair.Value).Equals(CueTitleIdentity(candidate), StringComparison.Ordinal)))
                return false;
        }

        if (cueTitles is null) return false;
        var expectedNumbers = Enumerable.Range(1, cueTitles.Count).ToArray();
        if (cueTitles.Count != rootFlacs.Length || !cueTitles.Keys.Order().SequenceEqual(expectedNumbers) ||
            references.Count != 1 && references.Count != cueTitles.Count)
            return false;

        var splitTracks = new Dictionary<int, string>();
        foreach (var flac in rootFlacs)
        {
            var parsed = LocalTrackRepairProcessor.ParseFileName(Path.GetFileNameWithoutExtension(flac));
            if (parsed.Number is not { } number || number == 0 || number > int.MaxValue ||
                !splitTracks.TryAdd((int)number, parsed.Title))
                return false;
        }

        if (!splitTracks.Keys.Order().SequenceEqual(expectedNumbers) || expectedNumbers.Any(number =>
                !CueTitleIdentity(cueTitles[number]).Equals(CueTitleIdentity(splitTracks[number]), StringComparison.Ordinal)))
            return false;

        if (references.Count == 1) return true;
        var referencedTracks = new Dictionary<int, string>();
        foreach (var reference in references)
        {
            if (Path.GetDirectoryName(reference) is not { } directory ||
                !directory.Equals(cueDirectory, StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(reference) is not { } extension ||
                extension is not (".wav" or ".WAV" or ".flac" or ".FLAC"))
                return false;
            var parsed = LocalTrackRepairProcessor.ParseFileName(Path.GetFileNameWithoutExtension(reference));
            if (parsed.Number is not { } number || number == 0 || number > int.MaxValue ||
                !referencedTracks.TryAdd((int)number, parsed.Title))
                return false;
        }
        return referencedTracks.Keys.Order().SequenceEqual(expectedNumbers) && expectedNumbers.All(number =>
            CueTitleIdentity(cueTitles[number]).Equals(CueTitleIdentity(referencedTracks[number]), StringComparison.Ordinal));
    }

    private static bool TryReadCueAudioTracks(string cue, out Dictionary<int, string> titles)
    {
        titles = [];
        var indexedTracks = new HashSet<int>();
        int? currentTrack = null;
        foreach (var line in File.ReadLines(cue))
        {
            var track = CueAudioTrackNumber().Match(line);
            if (track.Success)
            {
                if (!int.TryParse(track.Groups["number"].Value, out var number) || number <= 0 ||
                    !titles.TryAdd(number, string.Empty))
                    return false;
                currentTrack = number;
                continue;
            }

            if (CueAnyTrack().IsMatch(line))
            {
                currentTrack = null;
                continue;
            }
            if (currentTrack is not { } activeTrack) continue;
            var title = CueTitle().Match(line);
            if (title.Success && titles[activeTrack].Length == 0)
            {
                titles[activeTrack] = title.Groups["q"].Success
                    ? title.Groups["q"].Value.Trim()
                    : title.Groups["u"].Value.Trim();
                continue;
            }
            if (CueIndex01().IsMatch(line)) indexedTracks.Add(activeTrack);
        }
        return titles.Count >= 2 && indexedTracks.Count == titles.Count && titles.Values.All(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string CueTitleIdentity(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var identity = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                identity.Append(char.ToLowerInvariant(character));
        return identity.ToString();
    }

    [GeneratedRegex("^(?:(?:cd|disc|disk)\\s*[-_. ]*\\d+(?:\\s*[-_.].*)?|stereo|multichannel)$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscOrAreaFolder();

    [GeneratedRegex("^\\s*FILE\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))\\s+\\S+", RegexOptions.IgnoreCase)]
    private static partial Regex CueFile();

    [GeneratedRegex("^\\s*TRACK\\s+\\d+\\s+AUDIO\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueAudioTrack();

    [GeneratedRegex("^\\s*TRACK\\s+(?<number>\\d+)\\s+AUDIO\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueAudioTrackNumber();

    [GeneratedRegex("^\\s*TRACK\\s+\\d+\\s+\\S+\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueAnyTrack();

    [GeneratedRegex("^\\s*TITLE\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S.*))\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueTitle();

    [GeneratedRegex("^\\s*INDEX\\s+01\\s+\\d{1,3}:\\d{2}:\\d{2}\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueIndex01();

    [GeneratedRegex("^\\s*INDEX\\s+01\\s+00:00:00\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CueIndex01Zero();
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
            var otherDsdSources = scan.Media.Count(item => item.Kind is "DSF image" or "DFF image" or "Existing DSF" or "Existing DFF" or "DST stream" or "Raw DSD");
            checks.Add(sacdImages.Length == 1 && otherDsdSources == 0
                ? new("Verified write-back", CheckState.Passed, "Host-managed SACD ISO extraction, DSD verification, and transactional final placement are enabled.")
                : new("Verified write-back", CheckState.Failed, "This release supports one SACD ISO per album. Other DSD source types remain read-only.", true));
        }
        else if (scan.Mode == WorkflowMode.ExistingTrackRepair)
        {
            var repairKinds = scan.Media
                .Where(item => item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.Kind)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var isDsfRepair = repairKinds.Length == 1 && repairKinds[0].Equals("Existing DSF", StringComparison.OrdinalIgnoreCase);
            var isDffRepair = repairKinds.Length == 1 && repairKinds[0].Equals("Existing DFF", StringComparison.OrdinalIgnoreCase);
            var retainedDsdIso = (isDsfRepair || isDffRepair) && scan.ImageCount == 1 &&
                                 scan.Media.Count(item => item.Kind == "SACD / DSD image") == 1;
            checks.Add((scan.ImageCount == 0 || retainedDsdIso) && scan.TrackCount >= 2 && repairKinds.Length == 1 &&
                       (repairKinds[0].Equals("Existing FLAC", StringComparison.OrdinalIgnoreCase) || isDsfRepair || isDffRepair)
                ? new("Verified write-back", CheckState.Passed,
                    isDsfRepair
                        ? "Existing DSF tracks will be repaired in local staging, checked for exact native-DSD data-chunk equality, and replaced through destination-side rollback."
                        : isDffRepair
                            ? "Existing DFF tracks will be repaired in local staging with native DSDIFF ID3 handling, checked for exact DSD-chunk equality, and replaced through destination-side rollback; one coexisting SACD ISO may be deleted only after complete final verification."
                        : "Existing FLAC tracks will be repaired in local staging, checked for exact compressed-audio payload equality, and replaced through destination-side rollback; any one-file-per-track CUE remains untouched as provenance.")
                : new("Verified write-back", CheckState.Failed,
                    "Existing-track repair requires at least two standalone FLAC tracks, standalone DSF tracks, or standalone DFF tracks of one format. DSF or DFF repair may coexist with one retained SACD ISO; ambiguous mixed sources remain read-only.", true));
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
        if (PreviousOutputCleanupService.IsOptionalOnlyLegacySacdCompletion(root)) status = "passed";
        var deleted = verification.ValueKind == JsonValueKind.Object && Bool(verification, "sources_deleted");
        var errors = new List<string>(); if (verification.ValueKind == JsonValueKind.Object && Prop(verification, "errors", out var e) && e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) errors.Add(item.ToString());
        if (verification.ValueKind == JsonValueKind.Object && Prop(verification, "warnings", out var warnings) && warnings.ValueKind == JsonValueKind.Array) foreach (var item in warnings.EnumerateArray()) errors.Add(item.ToString());
        var tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Files(root, tracks);
        var sections = Count(root, "discs") + Count(root, "areas") + Count(root, "audio_areas");
        var incompleteKind = verification.ValueKind == JsonValueKind.Object
            ? CompletionIssuePresentation.FromStatus(Get(verification, "incomplete_kind"))
            : CompletionIssueKind.None;
        var label = status.ToLowerInvariant() switch
        {
            "passed" => "Verification passed",
            "incomplete" => incompleteKind == CompletionIssueKind.None
                ? "Tracks ready · required metadata/artwork missing"
                : $"Tracks ready · {CompletionIssuePresentation.Label(incompleteKind).ToLowerInvariant()}",
            "failed" => "Verification failed",
            "blocked" => "Run blocked safely",
            "canceled" => "Run canceled safely",
            _ => "Report pending"
        };
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
