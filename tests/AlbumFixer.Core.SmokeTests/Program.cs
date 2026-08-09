using System.Diagnostics;
using AlbumFixer.Core;

if (args is ["--process-sacd", var sacdAlbum])
    return await ProcessSacdAlbum(sacdAlbum);

var root = Path.Combine(Path.GetTempPath(), "album-fixer-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ScannerClassifiesFlacCue(root);
    await ScannerPrefersExistingTracks(root);
    await ScannerPlansMultipleAlbums(root);
    await ScannerAcceptsMultipleImagesInOneAlbumFolder(root);
    await ScannerRecognizesAndCleansIncompletePreviousOutput(root);
    BatchPreflightSkipsBlockedAlbums();
    await BoundedBatchRunsConcurrentlyAndIsolatesFailures();
    PipelineLimitsScaleWithHardwareAndCapacity();
    await StageAwarePipelineBoundsEveryLane();
    await PreflightFindsRunningCodex(root);
    await HostStagesAndVerifiesSource(root);
    await LocalSplitterRunsWithoutCodex(root);
    await LocalSplitterCropsAndNormalizesBookletFront(root);
    await LocalSplitterCreatesCdFoldersForMultipleImages(root);
    await HostCommitsVerifiedFlac(root);
    await HostReplacesVerifiedRootOutput(root);
    await HostCommitsMultipleImagesAndRetainsSources(root);
    await HostCommitsIncompleteFlacWithoutArtwork(root);
    await HostCommitFailureRetainsSource(root);
    await FailureReportIsAlwaysWritten(root);
    await MetadataHandoffIsConditional(root);
    await ExternalMetadataResolvesExactSacdRelease();
    await ExternalMetadataUsesAppleGenreFallback();
    await ExternalMetadataFailuresAreNonblocking();
    ProgressContractParses();
    DiagnosticContractClassifies();
    await ReportSummaryLoads(root);
    CommandContractIsSandboxed(root);
    Console.WriteLine("AlbumFixer.Core smoke tests passed (28/28).");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine("AlbumFixer.Core smoke tests failed.");
    Console.Error.WriteLine(error);
    return 1;
}
finally
{
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}

static async Task ScannerClassifiesFlacCue(string root)
{
    var folder = Path.Combine(root, "flac-cue"); Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "album.flac"), [1, 2, 3]);
    await File.WriteAllTextAsync(Path.Combine(folder, "album.cue"), "FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var result = await new AlbumScanner().ScanAsync(folder);
    Assert(result.Mode == WorkflowMode.FlacCueSplit, "FLAC+CUE should use image-split mode.");
    Assert(result.ImageCount == 1 && result.CueCount == 1, "FLAC+CUE inventory counts are wrong.");
}

static async Task ScannerPrefersExistingTracks(string root)
{
    var folder = Path.Combine(root, "repair"); Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "disc.flac"), [1]);
    await File.WriteAllTextAsync(Path.Combine(folder, "disc.cue"), "FILE \"disc.flac\" WAVE");
    await File.WriteAllBytesAsync(Path.Combine(folder, "01.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(folder, "02.flac"), [2]);
    var result = await new AlbumScanner().ScanAsync(folder);
    Assert(result.Mode == WorkflowMode.ExistingTrackRepair, "Existing tracks must take repair-only precedence.");
    Assert(result.Warnings.Any(value => value.Contains("coexist", StringComparison.OrdinalIgnoreCase)), "Coexisting image warning is missing.");
}

static async Task ScannerPlansMultipleAlbums(string root)
{
    var folder = Path.Combine(root, "artist");
    var first = Path.Combine(folder, "Album One");
    var second = Path.Combine(folder, "Album Two");
    Directory.CreateDirectory(first); Directory.CreateDirectory(second);
    await File.WriteAllBytesAsync(Path.Combine(first, "album.flac"), [1]);
    await File.WriteAllTextAsync(Path.Combine(first, "album.cue"), "FILE \"album.flac\" WAVE");
    var covers = Path.Combine(first, "Covers"); Directory.CreateDirectory(covers);
    await File.WriteAllBytesAsync(Path.Combine(covers, "cover.jpg"), [3]);
    await File.WriteAllBytesAsync(Path.Combine(second, "album.flac"), [2]);
    await File.WriteAllTextAsync(Path.Combine(second, "album.cue"), "FILE \"album.flac\" WAVE");
    var result = await new AlbumScanner().ScanAsync(folder);
    Assert(result.Mode == WorkflowMode.MultipleAlbums, "An artist folder must be classified as a batch, not one album.");
    Assert(result.Warnings.Any(value => value.Contains("2 independent albums", StringComparison.OrdinalIgnoreCase)), "Multiple-album batch guidance is missing.");
    var albums = await new AlbumScanner().ScanAlbumsAsync(folder);
    Assert(albums.Count == 2, "The batch scanner must create one plan per independent album root.");
    Assert(albums.All(album => album.Mode == WorkflowMode.FlacCueSplit), "Every discovered FLAC+CUE album must be independently runnable.");
    Assert(albums.Select(album => album.AlbumRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Batch album roots must be disjoint.");
    Assert(albums.All(album => !album.AlbumName.Equals("Covers", StringComparison.OrdinalIgnoreCase)), "Artwork-only folders must not become blocked batch albums.");
}

static async Task ScannerAcceptsMultipleImagesInOneAlbumFolder(string root)
{
    var folder = Path.Combine(root, "multi-image-album"); Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "disc1.flac"), [1]);
    await File.WriteAllTextAsync(Path.Combine(folder, "disc1.cue"), "FILE \"disc1.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    await File.WriteAllBytesAsync(Path.Combine(folder, "disc2.flac"), [2]);
    await File.WriteAllTextAsync(Path.Combine(folder, "disc2.cue"), "FILE \"disc2.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var result = await new AlbumScanner().ScanAsync(folder);
    Assert(result.Mode == WorkflowMode.FlacCueSplit, "Multiple FLAC+CUE images in one album folder should use image-split mode.");
    Assert(result.ImageCount == 2 && result.CueCount == 2, "The multi-image album inventory counts are wrong.");
}

static async Task ScannerRecognizesAndCleansIncompletePreviousOutput(string root)
{
    var folder = Path.Combine(root, "stale-legacy-output");
    var tracks = Path.Combine(folder, "Tracks", "CD1");
    Directory.CreateDirectory(tracks);
    var source = Path.Combine(folder, "album.flac");
    var previousTrack = Path.Combine(tracks, "01 - Previous.flac");
    await File.WriteAllBytesAsync(source, [1, 2, 3]);
    await File.WriteAllTextAsync(Path.Combine(folder, "album.cue"), "FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    await File.WriteAllBytesAsync(previousTrack, [4, 5, 6]);
    await File.WriteAllTextAsync(Path.Combine(folder, "conversion-report.json"), """
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "tracks": [{ "file": "Tracks/CD1/01 - Previous.flac" }] }],
      "verification": { "status": "failed", "sources_deleted": false }
    }
    """);

    var scan = await new AlbumScanner().ScanAsync(folder);
    Assert(scan.Mode == WorkflowMode.FlacCueSplit, "Report-proven incomplete legacy output must not be classified as a second album or a complete repair set.");
    Assert(scan.Media.Count(item => item.Kind == "Previous Album Fixer output") == 1, "The stale legacy track was not identified for cleanup.");
    var cleanup = await PreviousOutputCleanupService.CleanupAsync(folder);
    Assert(cleanup?.DeletedFiles == 1 && !File.Exists(previousTrack), "The exact report-listed legacy track was not deleted.");
    Assert(File.Exists(source) && File.Exists(Path.Combine(folder, "album.cue")), "Previous-output cleanup must preserve the source image and CUE.");
    Assert(!File.Exists(Path.Combine(folder, "conversion-report.json")) && File.Exists(cleanup!.ArchivedReportPath), "The incomplete report must be archived for provenance.");
    Assert(!Directory.Exists(Path.Combine(folder, "Tracks")), "Empty legacy Tracks directories should be removed without recursive deletion.");

    var completed = Path.Combine(root, "completed-legacy-output");
    var completedTracks = Path.Combine(completed, "Tracks", "CD1"); Directory.CreateDirectory(completedTracks);
    await File.WriteAllBytesAsync(Path.Combine(completedTracks, "01 - Verified.flac"), [7, 8, 9]);
    await File.WriteAllTextAsync(Path.Combine(completed, "conversion-report.json"), """
    { "workflow_mode": "flac_cue_split", "discs": [{ "tracks": [{ "file": "Tracks/CD1/01 - Verified.flac" }] }], "verification": { "status": "passed" } }
    """);
    Assert(PreviousOutputCleanupService.Discover(completed) is null, "A successfully verified prior track set must never be scheduled for deletion.");
    Assert(PreviousOutputCleanupService.DiscoverVerified(completed) is not null, "A successfully verified prior track set must be recognized for safe replacement.");

    var completedRoot = Path.Combine(root, "completed-root-output"); Directory.CreateDirectory(completedRoot);
    var completedSource = Path.Combine(completedRoot, "album.flac");
    var completedTrack = Path.Combine(completedRoot, "01 - Verified.flac");
    await File.WriteAllBytesAsync(completedSource, [1, 2, 3]);
    await File.WriteAllBytesAsync(completedTrack, [4, 5, 6]);
    await File.WriteAllTextAsync(Path.Combine(completedRoot, "album.cue"), "FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    await File.WriteAllTextAsync(Path.Combine(completedRoot, "conversion-report.json"), """
    { "workflow_mode": "flac_cue_split", "discs": [{ "tracks": [{ "file": "01 - Verified.flac" }] }], "verification": { "status": "passed" }, "commit": { "files": [{ "file": "01 - Verified.flac", "sha256": "DUMMY" }] } }
    """);
    var completedRootScan = await new AlbumScanner().ScanAsync(completedRoot);
    Assert(completedRootScan.Mode == WorkflowMode.FlacCueSplit && completedRootScan.Media.Count(item => item.Kind == "Previous Album Fixer output") == 1,
        "A report-proven root output must be retried as a FLAC+CUE split instead of repair-only mode.");

    var retainedInner = Path.Combine(root, "retained-inner-output"); Directory.CreateDirectory(Path.Combine(retainedInner, "Tracks", "CD1"));
    await File.WriteAllBytesAsync(Path.Combine(retainedInner, "album.flac"), [1, 2, 3]);
    await File.WriteAllTextAsync(Path.Combine(retainedInner, "album.cue"), "FILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    await File.WriteAllBytesAsync(Path.Combine(retainedInner, "Tracks", "CD1", "01 - Retained.flac"), [4]);
    await File.WriteAllBytesAsync(Path.Combine(retainedInner, "Tracks", "CD1", "02 - Retained.flac"), [5]);
    var retainedInnerScan = await new AlbumScanner().ScanAsync(retainedInner);
    Assert(retainedInnerScan.Mode == WorkflowMode.FlacCueSplit && retainedInnerScan.Media.Count(item => item.Kind == "Inner-folder FLAC") == 2,
        "Inner-folder tracks must be retained without blocking a new album-root split.");

    var traversal = Path.Combine(root, "malicious-legacy-report"); Directory.CreateDirectory(Path.Combine(traversal, "Tracks"));
    var protectedFile = Path.Combine(traversal, "protected.flac"); await File.WriteAllBytesAsync(protectedFile, [10]);
    await File.WriteAllTextAsync(Path.Combine(traversal, "conversion-report.json"), """
    { "workflow_mode": "flac_cue_split", "discs": [{ "tracks": [{ "file": "Tracks/../protected.flac" }] }], "verification": { "status": "failed" } }
    """);
    Assert(PreviousOutputCleanupService.Discover(traversal) is null && File.Exists(protectedFile), "A report path that escapes the legacy Tracks directory must never become a deletion target.");
}

static void BatchPreflightSkipsBlockedAlbums()
{
    var readyScan = new ScanResult("C:\\Ready", "Ready", WorkflowMode.FlacCueSplit, [], [], [], 1, 1, 0, 1, true, false);
    var blockedScan = new ScanResult("C:\\Blocked", "Blocked", WorkflowMode.ExistingTrackRepair, [], [], [], 1, 0, 2, 0, true, false);
    var tools = new Dictionary<string, string?> { ["ffmpeg"] = "ffmpeg.exe", ["ffprobe"] = "ffprobe.exe" };
    var ready = new PreflightResult([new("Album classification", CheckState.Passed, "FLAC + CUE", true)], "C:\\Temp", 100, 1000, tools);
    var blocked = new PreflightResult([new("Verified write-back", CheckState.Failed, "Unsupported workflow", true)], "C:\\Temp", 100, 1000, tools);
    AlbumPreflightResult[] albums = [new(0, readyScan, ready), new(1, blockedScan, blocked)];
    var combined = PreflightService.CombineBatch(albums);

    Assert(combined.CanStart, "One album-specific blocker must not block a healthy sibling album.");
    Assert(PreflightService.WorkerLimit(albums) == 1, "Only admitted albums may consume batch workers.");
    var blockedRow = combined.Checks.Single(check => check.Name.StartsWith("Album 2:", StringComparison.Ordinal));
    Assert(blockedRow.State == CheckState.Failed && !blockedRow.BlocksRun, "The blocked album must remain visible without becoming a global blocker.");
}

static async Task BoundedBatchRunsConcurrentlyAndIsolatesFailures()
{
    var active = 0;
    var maximum = 0;
    var started = 0;
    var firstPairStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var results = await BoundedBatchProcessor.RunAsync<int, int>([1, 2, 3, 4], async (item, _, token) =>
    {
        var nowActive = Interlocked.Increment(ref active);
        int observed;
        do
        {
            observed = Volatile.Read(ref maximum);
            if (observed >= nowActive) break;
        } while (Interlocked.CompareExchange(ref maximum, nowActive, observed) != observed);
        if (Interlocked.Increment(ref started) == 2) firstPairStarted.TrySetResult();
        try
        {
            await firstPairStarted.Task.WaitAsync(token);
            await Task.Delay(25, token);
            if (item == 2) throw new InvalidOperationException("expected isolated failure");
            return item * 10;
        }
        finally
        {
            Interlocked.Decrement(ref active);
        }
    }, maxParallelism: 2);

    Assert(maximum == 2, "The batch processor must run two independent jobs simultaneously and never exceed its worker limit.");
    Assert(results.Count(result => result.Succeeded) == 3, "A failed album must not stop the other batch jobs.");
    Assert(results.Single(result => !result.Succeeded).Item == 2, "The batch processor reported the wrong failed item.");
    Assert(results.Where(result => result.Succeeded).Select(result => result.Value).SequenceEqual([10, 30, 40]), "Successful batch results must remain ordered by input album.");
}

static void PipelineLimitsScaleWithHardwareAndCapacity()
{
    const long gib = 1024L * 1024 * 1024;
    var strong = BatchPipelineLimits.Recommend(Enumerable.Repeat(2 * gib, 33).ToArray(), 1000 * gib, 16, 32 * gib);
    Assert(strong == new BatchPipelineLimits(6, 2, 4, 2, 2), "A strong CPU, NVMe staging volume, and ample memory should use the optimized six-job pipeline.");

    var capacityBound = BatchPipelineLimits.Recommend([10 * gib, 10 * gib, 10 * gib], 25 * gib, 16, 32 * gib);
    Assert(capacityBound.MaxInFlight == 2, "The 20% staging reserve must reduce in-flight admissions before local storage is overcommitted.");

    var constrained = BatchPipelineLimits.Recommend(Enumerable.Repeat(2 * gib, 8).ToArray(), 100 * gib, 2, 4 * gib);
    Assert(constrained == new BatchPipelineLimits(2, 1, 1, 1, 1), "Constrained hardware must fall back to one worker per stage and only two in-flight jobs.");
}

static async Task StageAwarePipelineBoundsEveryLane()
{
    using var pipeline = new BatchPipelineScheduler(new(6, 2, 4, 2, 2));
    static async Task<int> BriefWork(CancellationToken token)
    {
        await Task.Delay(40, token);
        return 1;
    }

    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pipeline.RunCopyInAsync(BriefWork)));
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pipeline.RunProcessingAsync(false, BriefWork)));
    await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => pipeline.RunProcessingAsync(true, BriefWork)));
    await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => pipeline.RunCopyBackAsync(BriefWork)));
    var observed = pipeline.Telemetry;
    Assert(observed == new BatchPipelineTelemetry(2, 4, 2, 2), "Each independent pipeline lane must reach, but never exceed, its configured bound.");
}

static async Task PreflightFindsRunningCodex(string root)
{
    if (Process.GetProcessesByName("codex").Length == 0) return;
    var path = await PreflightService.FindOptionalCodexAsync();
    Assert(path is not null && File.Exists(path), "The installed, running Codex desktop app should be discovered only when metadata fallback is needed.");
}

static async Task HostStagesAndVerifiesSource(string root)
{
    var album = Path.Combine(root, "flac-cue");
    var toolsRoot = Path.Combine(root, "fake-tools"); Directory.CreateDirectory(toolsRoot);
    var ffmpeg = Path.Combine(toolsRoot, "ffmpeg.exe"); await File.WriteAllBytesAsync(ffmpeg, [4, 5, 6]);
    var ffprobe = Path.Combine(toolsRoot, "ffprobe.exe"); await File.WriteAllBytesAsync(ffprobe, [7, 8, 9]);
    var skillRoot = Path.Combine(root, "fake-skill"); Directory.CreateDirectory(skillRoot);
    var skill = Path.Combine(skillRoot, "SKILL.md"); await File.WriteAllTextAsync(skill, "# test skill");
    var scan = await new AlbumScanner().ScanAsync(album);
    var tempRoot = Path.Combine(root, "jobs"); Directory.CreateDirectory(tempRoot);
    var job = Path.Combine(tempRoot, "job-one"); Directory.CreateDirectory(job);
    var tools = new Dictionary<string, string?> { ["codex"] = "codex.exe", ["ffmpeg"] = ffmpeg, ["ffprobe"] = ffprobe, ["sacd_extract"] = null };
    var preflight = new PreflightResult([], tempRoot, 0, long.MaxValue, tools);
    var staged = await new HostStagingService().StageAsync(scan, preflight, skill, job, new Progress<ProgressSnapshot>());
    var stagedSource = Path.Combine(staged.AlbumRoot, "album.flac");
    Assert(File.Exists(stagedSource) && File.Exists(Path.Combine(staged.AlbumRoot, "album.cue")), "The album and CUE were not copied into local staging.");
    Assert(staged.Sources.Count == 1 && staged.Sources[0].Sha256 == await HostStagingService.Sha256Async(stagedSource), "The staged source SHA-256 was not verified.");
    Assert(File.Exists(staged.FfmpegPath) && File.Exists(staged.FfprobePath), "Required local audio tools were not staged.");
    Assert(string.IsNullOrEmpty(staged.SkillPath) && !Directory.Exists(Path.Combine(job, "skill")), "A complete local run must not stage the optional Codex skill.");
}
static async Task LocalSplitterRunsWithoutCodex(string root)
{
    var installedSkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", "album-fixer", "SKILL.md");
    var album = Path.Combine(root, "Rock", "Test Artist", "(2026) Fast Album [Test]");
    Directory.CreateDirectory(album);
    var source = Path.Combine(album, "album.flac");
    var cue = Path.Combine(album, "album.cue");
    var cover = Path.Combine(album, "front.jpg");
    await File.WriteAllBytesAsync(source, [1, 2, 3]);
    await File.WriteAllTextAsync(cue, """
    REM GENRE Rock
    REM DATE 2026
    PERFORMER "Test Artist"
    TITLE "Fast Album"
    FILE "album.flac" WAVE
      TRACK 01 AUDIO
        TITLE "First"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Second"
        INDEX 01 00:01:37
    """);

    var provisional = await new AlbumScanner().ScanAsync(album);
    var preflight = await new PreflightService().CheckAsync(provisional, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    File.Delete(source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "flac", source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=1200x900", "-frames:v", "1", "-update", "1", cover);
    var scan = await new AlbumScanner().ScanAsync(album);

    var job = Path.Combine(root, "local-split-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "album.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "album.cue"));
    File.Copy(cover, Path.Combine(stagedAlbum, "front.jpg"));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), []);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 2 && !result.Metadata.RequiresResearch, "Complete local CUE metadata and artwork must split without Codex.");
    Assert(File.Exists(Path.Combine(stagedAlbum, "01 - First.flac")) &&
           File.Exists(Path.Combine(stagedAlbum, "02 - Second.flac")), "A single image must create both CUE tracks directly in the album folder.");
    Assert(File.Exists(Path.Combine(stagedAlbum, "cover.jpg")) && File.Exists(result.ReportPath), "The local cover or conversion report is missing.");
    var normalizedCover = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", Path.Combine(stagedAlbum, "cover.jpg"));
    Assert(normalizedCover.Trim() == "600x600" && new FileInfo(Path.Combine(stagedAlbum, "cover.jpg")).Length <= 1024 * 1024,
        "A large explicit front cover must be square and normalized to at most 600x600 and 1 MB.");
    var handoff = await MetadataGapService.LoadAsync(job);
    Assert(!handoff.RequiresResearch && handoff.MissingFields.Count == 0, "Complete local evidence must produce an empty metadata handoff.");
    Assert(!File.Exists(Path.Combine(job, "metadata-agent-events.jsonl")) && !File.Exists(Path.Combine(job, "metadata-agent-final-message.txt")), "The complete local path must not start Codex.");

    var probeJson = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", Path.Combine(stagedAlbum, "01 - First.flac"));
    using var document = System.Text.Json.JsonDocument.Parse(probeJson);
    var streams = document.RootElement.GetProperty("streams");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "audio" && stream.GetProperty("codec_name").GetString() == "flac"), "The local output has no FLAC stream.");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1), "The local output has no embedded front cover.");
    var tags = document.RootElement.GetProperty("format").GetProperty("tags");
    Assert(tags.EnumerateObject().Any(tag => tag.Name.Equals("TITLE", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() == "First"), "The local track title tag is missing.");
    Assert(tags.EnumerateObject().Any(tag => (tag.Name.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("ALBUM_ARTIST", StringComparison.OrdinalIgnoreCase)) && tag.Value.GetString() == "Test Artist"), "The local album-artist tag is missing.");
}

static async Task LocalSplitterCropsAndNormalizesBookletFront(string root)
{
    var installedSkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", "album-fixer", "SKILL.md");
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(seed, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "Rock", "Test Artist", "(2026) Booklet Cover Album");
    var covers = Path.Combine(album, "Covers");
    Directory.CreateDirectory(covers);
    var source = Path.Combine(album, "album.flac");
    var cue = Path.Combine(album, "album.cue");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.25", "-c:a", "flac", source);
    await File.WriteAllTextAsync(cue, "REM GENRE Rock\nREM DATE 2026\nPERFORMER \"Test Artist\"\nTITLE \"Booklet Cover Album\"\nFILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"Track\"\n    INDEX 01 00:00:00");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error",
        "-f", "lavfi", "-i", "color=c=red:s=100x100", "-f", "lavfi", "-i", "color=c=blue:s=100x100",
        "-filter_complex", "hstack", "-frames:v", "1", Path.Combine(covers, "Booklet 1.jpg"));
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=200x150",
        "-frames:v", "1", Path.Combine(covers, "Back.png"));

    var scan = await new AlbumScanner().ScanAsync(album);
    var job = Path.Combine(root, "booklet-cover-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(Path.Combine(stagedAlbum, "Covers"));
    File.Copy(source, Path.Combine(stagedAlbum, "album.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "album.cue"));
    File.Copy(Path.Combine(covers, "Booklet 1.jpg"), Path.Combine(stagedAlbum, "Covers", "Booklet 1.jpg"));
    File.Copy(Path.Combine(covers, "Back.png"), Path.Combine(stagedAlbum, "Covers", "Back.png"));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), []);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(!result.Metadata.RequiresResearch, "A recognizable first booklet spread should provide a local front cover without online research.");
    var cover = Path.Combine(stagedAlbum, "cover.jpg");
    Assert(File.Exists(cover) && new FileInfo(cover).Length < 8L * 1024 * 1024, "The derived cover must be normalized below the safe embedding limit.");
    var coverProbe = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_entries", "stream=width,height", "-of", "csv=p=0:s=x", cover);
    Assert(coverProbe.Trim() == "100x100", "The right-side booklet panel must be cropped to a square without upscaling.");
    var trackProbe = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_entries", "stream=codec_type:stream_disposition=attached_pic", "-of", "json", Path.Combine(stagedAlbum, "01 - Track.flac"));
    using var trackDocument = System.Text.Json.JsonDocument.Parse(trackProbe);
    Assert(trackDocument.RootElement.GetProperty("streams").EnumerateArray().Any(stream =>
        stream.GetProperty("codec_type").GetString() == "video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1),
        "The normalized booklet front must be embedded in the split track.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    Assert(report.RootElement.GetProperty("cover").GetProperty("source").GetString()!.Contains("Booklet 1", StringComparison.OrdinalIgnoreCase),
        "Back artwork must never outrank a recognizable first-booklet front panel.");
}

static async Task LocalSplitterCreatesCdFoldersForMultipleImages(string root)
{
    var installedSkill = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "skills", "album-fixer", "SKILL.md");
    var album = Path.Combine(root, "Rock", "Test Artist", "(2026) Multi Image Album");
    Directory.CreateDirectory(album);
    var cover = Path.Combine(album, "front.jpg");
    var provisional = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisional, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=96x96", "-frames:v", "1", "-update", "1", cover);
    for (var disc = 1; disc <= 2; disc++)
    {
        var source = Path.Combine(album, $"disc{disc}.flac");
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={400 + disc * 100}:duration=0.25", "-c:a", "flac", source);
        await File.WriteAllTextAsync(Path.Combine(album, $"disc{disc}.cue"), $"""
        REM GENRE Rock
        REM DATE 2026
        PERFORMER "Test Artist"
        TITLE "Multi Image Album"
        FILE "disc{disc}.flac" WAVE
          TRACK 01 AUDIO
            TITLE "Disc {disc} Track"
            INDEX 01 00:00:00
        """);
    }

    var scan = await new AlbumScanner().ScanAsync(album);
    var job = Path.Combine(root, "multi-local-split-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    foreach (var file in Directory.EnumerateFiles(album)) File.Copy(file, Path.Combine(stagedAlbum, Path.GetFileName(file)));
    var stagedSources = scan.Media.Where(item => item.Kind == "FLAC image")
        .Select(item => new StagedSource(item.RelativePath, item.Size, string.Empty)).ToArray();
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), stagedSources);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 2, "Both FLAC images must be split.");
    Assert(File.Exists(Path.Combine(stagedAlbum, "CD1", "01 - Disc 1 Track.flac")) &&
           File.Exists(Path.Combine(stagedAlbum, "CD2", "01 - Disc 2 Track.flac")), "Multiple images must create deterministic CD<n> folders.");
    Assert(!Directory.Exists(Path.Combine(stagedAlbum, "Tracks")), "The obsolete Tracks wrapper folder must not be created.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    var discs = report.RootElement.GetProperty("discs");
    Assert(discs.GetArrayLength() == 2 &&
           discs[0].GetProperty("tracks")[0].GetProperty("file").GetString() == "CD1/01 - Disc 1 Track.flac" &&
           discs[1].GetProperty("tracks")[0].GetProperty("file").GetString() == "CD2/01 - Disc 2 Track.flac", "The conversion report must preserve the CD<n> output paths.");
}
static async Task<string> RunToolOutputAsync(string tool, params string[] arguments)
{
    var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask; var error = await errorTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} failed: {error}");
    return output;
}
static async Task HostCommitsVerifiedFlac(string root)
{
    const string installedSkill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    if (!File.Exists(installedSkill)) return;
    var destination = Path.Combine(root, "commit-destination"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisionalScan = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisionalScan, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=1000:duration=0.25", "-c:a", "flac", source);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"commit-test-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album");
    var stagedSkill = Path.Combine(job, "skill");
    Directory.CreateDirectory(stagedAlbum); Directory.CreateDirectory(Path.Combine(stagedSkill, "scripts"));
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    File.Copy(installedSkill, Path.Combine(stagedSkill, "SKILL.md"));
    var cover = Path.Combine(stagedAlbum, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var track = Path.Combine(stagedAlbum, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-i", cover,
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "copy", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Test", "-metadata", "ALBUM=Test Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    var report = """
    {
      "album": "Test Album",
      "edition": "Synthetic transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": { "file": "cover.jpg", "size": [64, 64], "source": "generated test fixture" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Test", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """;
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), report);
    var manifest = Path.Combine(job, "host-manifest.json"); await File.WriteAllTextAsync(manifest, "{}");
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length, await HostStagingService.Sha256Async(source));
    var staged = new StagedJob(job, stagedAlbum, Path.Combine(stagedSkill, "SKILL.md"), ffmpeg, ffprobe, manifest, [stagedSource],
        PipelineLimits: new(6, 2, 4, 2, 2), PipelineTelemetry: new(2, 4, 1, 2));
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(result.Tracks == 1 && result.SourcesDeleted, "The exact source must be deleted after final quick checks.");
    Assert(File.Exists(Path.Combine(destination, "01 - Test.flac")) && File.Exists(Path.Combine(destination, "cover.jpg")), "Verified outputs were not committed to the album folder.");
    Assert(!File.Exists(source) && File.Exists(cue) && File.Exists(result.ReportPath), "Only the source FLAC should be deleted; the CUE and final report must remain.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == "passed" && summary.Tracks == 1 && summary.Deleted, "The final report did not record quick-check source deletion.");
    using var finalReport = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    var pipeline = finalReport.RootElement.GetProperty("pipeline");
    Assert(pipeline.GetProperty("configured").GetProperty("processing_workers").GetInt32() == 4 &&
           pipeline.GetProperty("observed_at_commit").GetProperty("copy_back_workers").GetInt32() == 2,
        "The final album report must record configured and observed stage-aware pipeline limits.");
}

static async Task HostReplacesVerifiedRootOutput(string root)
{
    const string installedSkill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    if (!File.Exists(installedSkill)) return;
    var destination = Path.Combine(root, "replace-verified-root-output"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisional = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisional, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=900:duration=0.25", "-c:a", "flac", source);
    var cover = Path.Combine(destination, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var priorTrack = Path.Combine(destination, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", source, "-c:a", "copy", priorTrack);
    var priorTrackHash = await HostStagingService.Sha256Async(priorTrack);
    var coverHash = await HostStagingService.Sha256Async(cover);
    await File.WriteAllTextAsync(Path.Combine(destination, "conversion-report.json"), $$"""
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "file": "01 - Test.flac" }] }],
      "cover": { "file": "cover.jpg" },
      "verification": { "status": "passed" },
      "commit": { "files": [
        { "file": "01 - Test.flac", "sha256": "{{priorTrackHash}}" },
        { "file": "cover.jpg", "sha256": "{{coverHash}}" }
      ] }
    }
    """);
    var scan = await new AlbumScanner().ScanAsync(destination);
    Assert(scan.Mode == WorkflowMode.FlacCueSplit, "A verified root output must permit a new FLAC+CUE split.");
    var plan = PreviousOutputCleanupService.DiscoverVerified(destination);
    Assert(plan is not null, "The verified root output was not available for transactional replacement.");

    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"replace-verified-root-output-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    File.Copy(cover, Path.Combine(stagedAlbum, "cover.jpg"));
    var replacementTrack = Path.Combine(stagedAlbum, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-i", Path.Combine(stagedAlbum, "cover.jpg"),
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "copy", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Replacement", "-metadata", "ALBUM=Replacement Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", replacementTrack);
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), """
    {
      "album": "Replacement Album",
      "edition": "Synthetic replacement test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": { "file": "cover.jpg", "size": [64, 64], "source": "generated test fixture" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Replacement", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [], PreviousVerifiedOutput: plan);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(result.Tracks == 1 && File.Exists(priorTrack), "The report-proven root track was not replaced.");
    var output = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_format", "-of", "json", priorTrack);
    using var document = System.Text.Json.JsonDocument.Parse(output);
    var finalTitle = document.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
        .FirstOrDefault(property => property.Name.Equals("TITLE", StringComparison.OrdinalIgnoreCase)).Value.GetString();
    Assert(finalTitle == "Replacement",
        "The final root track did not contain the verified replacement output.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    Assert(report.RootElement.GetProperty("commit").GetProperty("replaced_previous_outputs").GetArrayLength() == 2,
        "The final report did not record replacement of the prior root track and cover.");
}

static async Task HostCommitsMultipleImagesAndRetainsSources(string root)
{
    const string installedSkill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    if (!File.Exists(installedSkill)) return;
    var destination = Path.Combine(root, "multi-commit-destination"); Directory.CreateDirectory(destination);
    var provisionalScan = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisionalScan, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    for (var disc = 1; disc <= 2; disc++)
    {
        var source = Path.Combine(destination, $"disc{disc}.flac");
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={600 + disc * 100}:duration=0.25", "-c:a", "flac", source);
        await File.WriteAllTextAsync(Path.Combine(destination, $"disc{disc}.cue"), $"""
        REM GENRE Rock
        REM DATE 2026
        PERFORMER "Test Artist"
        TITLE "Committed Multi Image Album"
        FILE "disc{disc}.flac" WAVE
          TRACK 01 AUDIO
            TITLE "Committed Disc {disc}"
            INDEX 01 00:00:00
        """);
    }
    var scan = await new AlbumScanner().ScanAsync(destination);
    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"multi-commit-test-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    foreach (var file in Directory.EnumerateFiles(destination)) File.Copy(file, Path.Combine(stagedAlbum, Path.GetFileName(file)));
    var cover = Path.Combine(stagedAlbum, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=yellow:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var stagedSources = new List<StagedSource>();
    foreach (var item in scan.Media.Where(item => item.Kind == "FLAC image"))
        stagedSources.Add(new(item.RelativePath, item.Size, await HostStagingService.Sha256Async(item.Path)));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), stagedSources);

    var local = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(!local.Metadata.RequiresResearch, "The multi-image commit fixture should not require metadata research.");
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 2 && !result.SourcesDeleted, "A multi-image commit must retain every original source.");
    Assert(File.Exists(Path.Combine(destination, "CD1", "01 - Committed Disc 1.flac")) &&
           File.Exists(Path.Combine(destination, "CD2", "01 - Committed Disc 2.flac")), "The CD<n> tracks were not committed to final paths.");
    Assert(File.Exists(Path.Combine(destination, "disc1.flac")) && File.Exists(Path.Combine(destination, "disc2.flac")), "Multi-image originals must be retained without explicit deletion authorization.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    Assert(report.RootElement.GetProperty("deletion").GetProperty("status").GetString() == "retained" &&
           !report.RootElement.GetProperty("verification").GetProperty("sources_deleted").GetBoolean(), "The final report must record retained multi-image sources.");
}

static async Task HostCommitsIncompleteFlacWithoutArtwork(string root)
{
    const string installedSkill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    if (!File.Exists(installedSkill)) return;
    var destination = Path.Combine(root, "incomplete-artwork-destination");
    Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisional = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisional, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=700:duration=0.25", "-c:a", "flac", source);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"incomplete-cover-test-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var track = Path.Combine(stagedAlbum, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-c:a", "copy",
        "-metadata", "TITLE=Test", "-metadata", "ALBUM=Incomplete Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), """
    {
      "album": "Incomplete Album",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Test", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length, await HostStagingService.Sha256Async(source));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 1 && result.Incomplete && !result.SourcesDeleted, "Missing artwork must deliver tracks as incomplete work without deleting the source.");
    Assert(File.Exists(Path.Combine(destination, "01 - Test.flac")) && File.Exists(source), "The incomplete track and original source must both remain in the album folder.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == "incomplete" && !summary.Deleted, "The final report must mark deferred artwork as incomplete work.");
    var retryPlan = PreviousOutputCleanupService.Discover(destination);
    Assert(retryPlan is not null && retryPlan.Files.Any(file => file.RelativePath.Equals("01 - Test.flac", StringComparison.OrdinalIgnoreCase)),
        "A later retry must recognize the hash-proven incomplete root track for safe replacement.");
}

static async Task HostCommitFailureRetainsSource(string root)
{
    const string installedSkill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    if (!File.Exists(installedSkill)) return;
    var destination = Path.Combine(root, "failed-commit-destination"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisionalScan = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisionalScan, installedSkill);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=750:duration=0.25", "-c:a", "flac", source);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(root, "failed-commit-job");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var track = Path.Combine(stagedAlbum, "01 - Invalid.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-c:a", "copy", track);
    var cover = Path.Combine(stagedAlbum, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", "-update", "1", cover);
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), """
    {
      "album": "Invalid transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": { "file": "cover.jpg" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Invalid", "file": "01 - Invalid.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length, await HostStagingService.Sha256Async(source));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);

    Exception? failure = null;
    try { await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>()); }
    catch (Exception error) { failure = error; }

    Assert(failure is InvalidOperationException && failure.Message.Contains("tags", StringComparison.OrdinalIgnoreCase),
        "Missing required tags must still fail the quick playback-file checks.");
    Assert(File.Exists(source), "A failed commit must retain the exact source FLAC.");
    Assert(!File.Exists(Path.Combine(destination, "01 - Invalid.flac")), "A failed local check must not commit the output track.");
}

static async Task RunToolAsync(string tool, params string[] arguments)
{
    var info = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
    foreach (var argument in arguments) info.ArgumentList.Add(argument);
    using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
    var outputTask = process.StandardOutput.ReadToEndAsync();
    var errorTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    var output = await outputTask; var error = await errorTask;
    if (process.ExitCode != 0) throw new InvalidOperationException($"{tool} failed: {error} {output}");
}
static async Task FailureReportIsAlwaysWritten(string root)
{
    var scan = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var tools = new Dictionary<string, string?> { ["codex"] = "codex.exe", ["ffmpeg"] = "ffmpeg.exe", ["ffprobe"] = "ffprobe.exe", ["sacd_extract"] = null };
    var preflight = new PreflightResult([], Path.GetTempPath(), 0, long.MaxValue, tools);
    var job = Path.Combine(root, "failed-job");
    var path = await HostReportWriter.EnsureTerminalReportAsync(scan, preflight, job, "failed", JobPhase.Failed, 1,
        "Inventory access was denied.", 0, "test-thread");
    Assert(File.Exists(path), "A stopped run must preserve a conversion report.");
    var report = await ReportReader.LoadAsync(path);
    Assert(report.Status == "failed" && !report.Deleted && report.Errors.Count == 1, "Failure report status or retention state is wrong.");
}

static async Task MetadataHandoffIsConditional(string root)
{
    var completeJob = Path.Combine(root, "metadata-complete");
    Directory.CreateDirectory(completeJob);
    await File.WriteAllTextAsync(MetadataGapService.GetPath(completeJob), """
    {"split_completed":true,"requires_research":false,"missing_fields":[],"local_evidence":["CUE","cover scan"]}
    """);
    var complete = await MetadataGapService.LoadAsync(completeJob);
    Assert(complete.SplitCompleted && !complete.RequiresResearch && complete.MissingFields.Count == 0, "Complete local metadata must skip the research agent.");

    var missingJob = Path.Combine(root, "metadata-missing");
    Directory.CreateDirectory(missingJob);
    await File.WriteAllTextAsync(MetadataGapService.GetPath(missingJob), """
    {"split_completed":true,"requires_research":true,"missing_fields":["GENRE","DATE"],"local_evidence":["CUE"]}
    """);
    var missing = await MetadataGapService.LoadAsync(missingJob);
    Assert(missing.RequiresResearch && missing.MissingFields.SequenceEqual(["GENRE", "DATE"]), "Only named metadata gaps should start the research agent.");
}
static void ProgressContractParses()
{
    var json = "{\"phase\":\"Final-path verification passed\",\"percent\":92,\"status\":\"running\",\"detail\":\"Network copy verified\"}";
    Assert(CodexRunner.TryProgress(json, out var snapshot), "Progress JSON should parse.");
    Assert(snapshot.Phase == JobPhase.FinalVerificationPassed && snapshot.Percent == 92, "Progress phase mapping is wrong.");
}

static void DiagnosticContractClassifies()
{
    var pluginWarning = "2026-08-07 WARN codex_core::skills::loader: ignoring interface.icon_small";
    Assert(CodexRunner.DiagnosticKind(pluginWarning) == "warning", "Codex WARN diagnostics must not be labeled as errors.");
    Assert(CodexRunner.IsPluginMetadataWarning(pluginWarning), "Optional plugin metadata warnings should be collapsible.");
    Assert(CodexRunner.DiagnosticKind("2026-08-07 ERROR codex_core::exec: failed") == "error", "Real Codex errors must remain errors.");
}
static async Task ReportSummaryLoads(string root)
{
    var path = Path.Combine(root, "conversion-report.json");
    await File.WriteAllTextAsync(path, """
    {"album":"Test Album","edition":"Label CAT-1","workflow_mode":"flac_cue_split","discs":[{"tracks":[{"file":"Tracks/01.flac"},{"file":"Tracks/02.flac"}]}],"verification":{"status":"passed","method":"Quick header and tag checks","sources_deleted":true,"errors":[]}}
    """);
    var summary = await ReportReader.LoadAsync(path);
    Assert(summary.Status == "passed" && summary.Tracks == 2 && summary.Sections == 1 && summary.Deleted, "Report summary is wrong.");
}

static async Task ExternalMetadataResolvesExactSacdRelease()
{
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
            {"releases":[{"id":"release-1","score":100,"title":"Brothers in Arms","artist-credit":[{"name":"Dire Straits"}],"release-group":{"id":"group-1"},"date":"2014-04-23","country":"JP","barcode":"4988005811783","track-count":9,"media":[{"format":"SHM-SACD","track-count":9}],"label-info":[{"catalog-number":"UIGY-9547","label":{"name":"Vertigo"}}]}]}
            """);
        if (uri.Contains("/ws/2/release-group/group-1", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"genres":[{"name":"rock","count":20},{"name":"pop rock","count":8}],"tags":[],"relations":[{"type":"discogs","url":{"resource":"https://www.discogs.com/master/23684"}}]}""");
        if (uri.Contains("api.discogs.com/masters/23684", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"title":"Brothers In Arms","artists":[{"name":"Dire Straits"}],"genres":["Rock"],"styles":["Blues Rock"]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"artistName":"Dire Straits","collectionName":"Brothers In Arms","primaryGenreName":"Rock","releaseDate":"1985-05-17T07:00:00Z","trackCount":9,"collectionViewUrl":"https://music.apple.com/album/1"}]}""");
        throw new InvalidOperationException($"Unexpected metadata request: {uri}");
    }));
    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var result = await service.ResolveAsync(new("Brothers In Arms", "Dire Straits", 9, 1985, 2014));
    Assert(result.Genre == "Rock" && result.GenreSourceType == "discogs_linked_from_musicbrainz" && result.Label == "Vertigo" && result.CatalogNumber == "UIGY-9547", "Exact MusicBrainz/Discogs SACD metadata was not selected.");
    Assert(result.Barcode == "4988005811783" && result.ReleaseCountry == "JP" && result.ReleaseDate == "2014-04-23", "Exact SACD edition fields were not retained.");
    Assert(result.OriginalDate?.StartsWith("1985", StringComparison.Ordinal) == true && result.Sources.Count >= 3, "Corroborating Discogs/Apple Music metadata or source provenance is missing.");
}

static async Task ExternalMetadataUsesAppleGenreFallback()
{
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"artistName":"Dire Straits","collectionName":"Communiqué","primaryGenreName":"Rock","releaseDate":"1979-06-15T07:00:00Z","trackCount":9,"collectionViewUrl":"https://music.apple.com/album/2"}]}""");
        throw new InvalidOperationException($"Unexpected metadata request: {uri}");
    }));
    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var result = await service.ResolveAsync(new("Communique'", "Dire Straits", 9, 1979, 2012));
    Assert(result.Genre == "Rock" && result.GenreSourceType == "apple_music_catalog", "Apple Music should provide a broad fallback genre when MusicBrainz has no match.");
}

static async Task ExternalMetadataFailuresAreNonblocking()
{
    using var client = new HttpClient(new StubHttpHandler((_, _) => throw new HttpRequestException("offline")));
    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromMilliseconds(100));
    var result = await service.ResolveAsync(new("Offline Album", "Offline Artist", 10, 1980, 2010));
    Assert(!result.HasMatch && result.Genre is null, "A failed external lookup must not invent a metadata match.");
    Assert(result.Warnings.Any(value => value.Contains("continue", StringComparison.OrdinalIgnoreCase)), "A failed external lookup must be recorded as a nonblocking warning.");
}

static void CommandContractIsSandboxed(string root)
{
    var options = new RunOptions("codex.exe", root, Path.Combine(root, "job"), "SKILL.md", Path.Combine(root, "ffmpeg.exe"), Path.Combine(root, "ffprobe.exe"), CodexWorkKind.MetadataEnrichment);
    var args = CodexContract.Arguments(options);
    Assert(args.Contains("workspace-write") && args.Contains("never") && !args.Any(value => value.Contains("yolo", StringComparison.OrdinalIgnoreCase)), "Unsafe Codex command flags detected.");
    Assert(args.Zip(args.Skip(1)).Any(pair => pair.First == "--cd" && pair.Second == options.JobDirectory), "The local staging job must be the Codex workspace.");
    Assert(!args.Contains("--add-dir") && !args.Contains(options.AlbumRoot), "The protected runner must not receive an external album path.");
    var protectedRunner = "C:" + Path.DirectorySeparatorChar + Path.Combine("Program Files", "WindowsApps", "OpenAI.Codex_1.0", "app", "resources", "codex.exe");
    var normalRunner = "C:" + Path.DirectorySeparatorChar + Path.Combine("Tools", "codex.exe");
    Assert(CodexRunner.RequiresLocalStaging(protectedRunner), "Protected WindowsApps runners must be staged locally.");
    Assert(!CodexRunner.RequiresLocalStaging(normalRunner), "Normal Codex executables must run in place.");
    var prompt = CodexContract.Prompt(options);
    Assert(prompt.Contains("deletes one exact inventoried FLAC image", StringComparison.OrdinalIgnoreCase) &&
           prompt.Contains("retains every original", StringComparison.OrdinalIgnoreCase), "The source-disposition policy is missing.");
    Assert(prompt.Contains("do not fully decode", StringComparison.OrdinalIgnoreCase) && prompt.Contains("do not run verify-flac-split.ps1", StringComparison.OrdinalIgnoreCase), "Fast mode must prohibit full PCM/MD5 verification.");
    Assert(prompt.Contains("Do not probe, map, or access any UNC/network path", StringComparison.OrdinalIgnoreCase), "The local-only runner boundary is missing.");
    Assert(prompt.Contains("already", StringComparison.OrdinalIgnoreCase) && prompt.Contains("split every track locally", StringComparison.OrdinalIgnoreCase), "The metadata agent must receive already-split tracks.");
    Assert(prompt.Contains("Research only those explicitly listed fields", StringComparison.OrdinalIgnoreCase) && prompt.Contains("Never split, extract, or re-encode", StringComparison.OrdinalIgnoreCase), "Codex must be metadata-only and limited to named gaps.");
    Assert(prompt.Contains("Discogs", StringComparison.OrdinalIgnoreCase) && prompt.Contains("MusicBrainz", StringComparison.OrdinalIgnoreCase) &&
           prompt.Contains("must never turn a successful extraction into a failed job", StringComparison.OrdinalIgnoreCase), "External metadata research must be explicit and nonblocking.");
    Assert(!prompt.Contains("split-first local worker", StringComparison.OrdinalIgnoreCase), "The obsolete Codex split worker is still present.");
    Assert(CodexContract.WorkerStem(options) == "metadata-agent", "Codex may only run as the optional metadata agent.");
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<int> ProcessSacdAlbum(string albumRoot)
{
    try
    {
        const string skill = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
        var scan = await new AlbumScanner().ScanAsync(albumRoot);
        var preflight = await new PreflightService().CheckAsync(scan, skill);
        foreach (var check in preflight.Checks)
            Console.WriteLine($"[{check.State}] {check.Name}: {check.Detail}");
        if (!preflight.CanStart)
            throw new InvalidOperationException("The SACD album did not pass preflight.");

        var jobDirectory = PreflightService.CreateJobDirectory(preflight.TempRoot);
        var progress = new Progress<ProgressSnapshot>(snapshot =>
            Console.WriteLine($"{snapshot.Percent,3}% {snapshot.Phase}: {snapshot.Detail}"));
        var staged = await new HostStagingService().StageAsync(scan, preflight, skill, jobDirectory, progress);
        var local = await new LocalDsdProcessor().ProcessAsync(scan, staged, progress);
        if (local.Metadata.RequiresResearch)
            Console.WriteLine($"SACD metadata remains incomplete ({string.Join(", ", local.Metadata.MissingFields)}); committing verified tracks and retaining the source ISO.");
        var committed = await new HostCommitService().CommitAsync(scan, staged, progress);
        Console.WriteLine($"SACD completed: {committed.Tracks} tracks; source deleted: {committed.SourcesDeleted}; report: {committed.ReportPath}");
        return 0;
    }
    catch (Exception error)
    {
        Console.Error.WriteLine(error);
        return 1;
    }
}

sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(responder(request, cancellationToken));

    public static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };
}
