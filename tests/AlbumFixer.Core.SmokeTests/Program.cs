using System.Diagnostics;
using System.Security.Cryptography;
using AlbumFixer.Core;

if (args is ["--process-sacd", var sacdAlbum])
    return await ProcessSacdAlbum(sacdAlbum);

var root = Path.Combine(Path.GetTempPath(), "album-fixer-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ToolDiscoveryReportsEveryStartupComponent();
    await ScannerClassifiesFlacCue(root);
    await ScannerPrefersExistingTracks(root);
    await ScannerPlansMultipleAlbums(root);
    await ScannerAcceptsMultipleImagesInOneAlbumFolder(root);
    await ScannerRecognizesAndCleansIncompletePreviousOutput(root);
    await ScannerSkipsCompletedAlbumsWithDeletedSources(root);
    await ScannerRejectsMissingSourceFallbackCompletion(root);
    await ScannerRecoversRetainedEquivalentFlacAfterCanceledFallback(root);
    await ScannerRecoversCompletedSacdAfterCanceledFallback(root);
    await AlbumTransactionLockSerializesOwners(root);
    BatchPreflightSkipsBlockedAlbums();
    await BoundedBatchRunsConcurrentlyAndIsolatesFailures();
    PipelineLimitsScaleWithHardwareAndCapacity();
    await StageAwarePipelineBoundsEveryLane();
    await HostStagesAndChecksSourceSize(root);
    await LocalSplitterRunsLocally(root);
    await LocalMetadataEnrichmentRunsInCode(root);
    await LocalSplitterCropsAndNormalizesBookletFront(root);
    await LocalSplitterCreatesCdFoldersForMultipleImages(root);
    await HostProcessesExistingDuplicateCoverWithoutImageOutputs(root);
    await HostCommitsVerifiedFlac(root, deleteOriginals: true);
    await HostCommitsVerifiedFlac(root, deleteOriginals: false);
    await HostReplacesVerifiedRootOutput(root);
    await HostCommitsMultipleImagesAndRetainsSources(root);
    await HostCommitsIncompleteFlacWithoutArtwork(root);
    await HostCommitFailureRetainsSource(root);
    await FailureCleanupRemovesLocalAndDestinationStages(root);
    SacdLayoutParserSupportsToolIndexConventions();
    await FailureReportIsAlwaysWritten(root);
    await FailureReportPreservesCompletedReport(root);
    await MetadataHandoffIsConditional(root);
    await ExternalMetadataResolvesExactSacdRelease();
    await ExternalMetadataUsesAppleGenreFallback();
    await ExternalMetadataFailuresAreNonblocking();
    await ExternalCoverDownloadIsMemoryOnlyAndBounded();
    await ReportSummaryLoads(root);
    Console.WriteLine("AlbumFixer.Core smoke tests passed.");
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

static async Task ToolDiscoveryReportsEveryStartupComponent()
{
    var tools = await new PreflightService().FindToolsAsync();
    Assert(tools.ContainsKey("ffmpeg"), "Startup tool discovery must report ffmpeg.");
    Assert(tools.ContainsKey("ffprobe"), "Startup tool discovery must report ffprobe.");
    Assert(tools.ContainsKey("sacd_extract"), "Startup tool discovery must report sacd_extract.");
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
    var cleanup = PreviousOutputCleanupService.Cleanup(folder);
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
    { "workflow_mode": "flac_cue_split", "discs": [{ "tracks": [{ "file": "01 - Verified.flac" }] }], "verification": { "status": "passed" }, "commit": { "files": [{ "file": "01 - Verified.flac", "size": 3 }] } }
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

static async Task ScannerSkipsCompletedAlbumsWithDeletedSources(string root)
{
    var batch = Path.Combine(root, "completed-album-batch");
    var completed = Path.Combine(batch, "Completed Album");
    var pending = Path.Combine(batch, "Pending Album");
    var pendingTwo = Path.Combine(batch, "Pending Album 2");
    var pendingThree = Path.Combine(batch, "Pending Album 3");
    Directory.CreateDirectory(completed);
    Directory.CreateDirectory(pending);
    Directory.CreateDirectory(pendingTwo);
    Directory.CreateDirectory(pendingThree);

    await File.WriteAllBytesAsync(Path.Combine(completed, "01.flac"), [1, 2, 3]);
    await File.WriteAllBytesAsync(Path.Combine(completed, "02.flac"), [4, 5, 6]);
    await File.WriteAllTextAsync(Path.Combine(completed, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllTextAsync(Path.Combine(completed, "conversion-report.json"), """
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "source": "album.flac", "tracks": [{ "file": "01.flac" }, { "file": "02.flac" }] }],
      "verification": { "status": "passed", "sources_deleted": true },
      "commit": { "status": "completed" },
      "deletion": { "status": "completed", "performed": true }
    }
    """);

    await File.WriteAllBytesAsync(Path.Combine(pending, "album.flac"), [7, 8, 9]);
    await File.WriteAllTextAsync(Path.Combine(pending, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllBytesAsync(Path.Combine(pendingTwo, "album.flac"), [10, 11, 12]);
    await File.WriteAllTextAsync(Path.Combine(pendingTwo, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllBytesAsync(Path.Combine(pendingThree, "album.flac"), [13, 14, 15]);
    await File.WriteAllTextAsync(Path.Combine(pendingThree, "album.cue"), "FILE \"album.flac\" WAVE");

    var completedScan = await new AlbumScanner().ScanAsync(completed);
    Assert(completedScan.Mode == WorkflowMode.Completed && !completedScan.RequiresProcessing,
        "A verified album whose source was intentionally deleted must be recognized as already completed.");
    Assert(completedScan.Errors.Count == 0,
        "A preserved CUE must not report its intentionally deleted source as missing after successful completion.");

    var completedSacd = Path.Combine(batch, "Completed SACD");
    var completedSacdStereo = Path.Combine(completedSacd, "Stereo");
    Directory.CreateDirectory(completedSacdStereo);
    await File.WriteAllBytesAsync(Path.Combine(completedSacdStereo, "01.dsf"), [12, 13]);
    await File.WriteAllBytesAsync(Path.Combine(completedSacdStereo, "02.dsf"), [14, 15]);
    await File.WriteAllBytesAsync(Path.Combine(completedSacd, "cover.jpg"), [16]);
    await File.WriteAllTextAsync(Path.Combine(completedSacd, "conversion-report.json"), """
    {
      "workflow_mode": "sacd_iso_extract",
      "areas": [{ "area": "stereo", "tracks": [{ "file": "Stereo/01.dsf" }, { "file": "Stereo/02.dsf" }] }],
      "cover": { "file": "cover.jpg" },
      "verification": { "status": "passed", "sources_deleted": true },
      "commit": { "status": "completed" },
      "deletion": { "status": "completed", "performed": true }
    }
    """);
    var completedSacdScan = await new AlbumScanner().ScanAsync(completedSacd);
    Assert(completedSacdScan.Mode == WorkflowMode.Completed && !completedSacdScan.RequiresProcessing,
        "A verified SACD extraction whose ISO was intentionally deleted must be recognized as already completed.");
    Assert(completedSacdScan.Media.Count(item => item.Kind == "Previous Album Fixer output") == 2 && completedSacdScan.TrackCount == 0,
        "Report-proven DSF outputs must not be misclassified as an existing-track repair job.");

    var retained = Path.Combine(batch, "Completed With Retained Source");
    Directory.CreateDirectory(retained);
    await File.WriteAllBytesAsync(Path.Combine(retained, "album.flac"), [17, 18, 19]);
    await File.WriteAllTextAsync(Path.Combine(retained, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllBytesAsync(Path.Combine(retained, "01.flac"), [20, 21]);
    await File.WriteAllBytesAsync(Path.Combine(retained, "02.flac"), [22, 23]);
    await File.WriteAllTextAsync(Path.Combine(retained, "conversion-report.json"), """
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "source": "album.flac", "tracks": [{ "file": "01.flac" }, { "file": "02.flac" }] }],
      "verification": { "status": "passed", "sources_deleted": false },
      "commit": { "status": "completed" },
      "deletion": { "status": "retained", "performed": false }
    }
    """);
    var retainedScan = await new AlbumScanner().ScanAsync(retained);
    Assert(retainedScan.Mode == WorkflowMode.Completed && File.Exists(Path.Combine(retained, "album.flac")),
        "A verified completed album must be skipped even when its source was intentionally retained.");

    var batchScan = await new AlbumScanner().ScanAsync(batch);
    Assert(batchScan.Mode == WorkflowMode.MultipleAlbums,
        "A completed album and a pending sibling must remain independently discoverable.");
    var discovered = await new AlbumScanner().ScanAlbumsAsync(batch);
    var pendingScans = discovered.Where(scan => scan.RequiresProcessing).ToArray();
    Assert(discovered.Count == 6 && pendingScans.Length == 3 &&
           pendingScans.Select(scan => scan.AlbumRoot).ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals([pending, pendingTwo, pendingThree]),
        "A mixed six-folder batch must expose exactly its three pending albums before preflight.");

    var unsafeCompleted = Path.Combine(root, "completed-report-with-unexpected-missing-source");
    Directory.CreateDirectory(unsafeCompleted);
    await File.WriteAllBytesAsync(Path.Combine(unsafeCompleted, "01.flac"), [10]);
    await File.WriteAllBytesAsync(Path.Combine(unsafeCompleted, "02.flac"), [11]);
    await File.WriteAllTextAsync(Path.Combine(unsafeCompleted, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllTextAsync(Path.Combine(unsafeCompleted, "conversion-report.json"), """
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "tracks": [{ "file": "01.flac" }, { "file": "02.flac" }] }],
      "verification": { "status": "passed", "sources_deleted": false }
    }
    """);
    var unsafeScan = await new AlbumScanner().ScanAsync(unsafeCompleted);
    Assert(unsafeScan.Mode != WorkflowMode.Completed && unsafeScan.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "A missing source must remain a blocker unless the completed report confirms intentional deletion.");
}

static async Task AlbumTransactionLockSerializesOwners(string root)
{
    var folder = Path.Combine(root, "album-transaction-lock");
    Directory.CreateDirectory(folder);
    await using (var first = await AlbumTransactionLock.AcquireAsync(folder))
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        Exception? blocked = null;
        try { await using var second = await AlbumTransactionLock.AcquireAsync(folder, cancellation.Token); }
        catch (Exception error) { blocked = error; }
        Assert(blocked is OperationCanceledException,
            "A second transaction must wait while another process owns the album lock.");
    }

    await using var acquiredAfterRelease = await AlbumTransactionLock.AcquireAsync(folder);
    Assert(File.Exists(Path.Combine(folder, AlbumTransactionLock.FileName)),
        "The released album lock must be reusable by the next transaction.");
}

static async Task ScannerRejectsMissingSourceFallbackCompletion(string root)
{
    var tools = await new PreflightService().FindToolsAsync();
    if (tools["ffmpeg"] is not { } ffmpeg) return;
    var folder = Path.Combine(root, "recover-stale-fallback");
    Directory.CreateDirectory(folder);
    var cover = Path.Combine(folder, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=64x64",
        "-frames:v", "1", "-update", "1", cover);
    for (var number = 1; number <= 2; number++)
    {
        var track = Path.Combine(folder, $"{number:00} - Track {number}.flac");
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={400 + number * 100}:duration=0.1", "-i", cover,
            "-map", "0:a:0", "-map", "1:v:0", "-c:a", "flac", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
            "-metadata", $"TITLE=Track {number}", "-metadata", "ALBUM=Recovered Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
            "-metadata", $"TRACKNUMBER={number}/2", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    }
    await File.WriteAllTextAsync(Path.Combine(folder, "album.cue"), """
    FILE "album.flac" WAVE
      TRACK 01 AUDIO
        TITLE "Track 1"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Track 2"
        INDEX 01 00:00:01
    """);
    var missingSource = Path.Combine(folder, "album.flac");
    await File.WriteAllTextAsync(Path.Combine(folder, "conversion-report.json"), $$"""
    {
      "workflow_mode": "FlacCueSplit",
      "generated_by": "Album Fixer host fallback",
      "sources": [{ "path": "album.flac", "type": "FLAC image", "size": 12345 }],
      "pipeline": { "status": "failed", "stopped_phase": "Inventoried", "detail": "Could not find file '{{missingSource.Replace("\\", "\\\\")}}'." },
      "discs": [],
      "verification": { "status": "failed", "sources_deleted": false },
      "commit": { "status": "not_completed" },
      "deletion": { "performed": false }
    }
    """);

    var rejected = await new AlbumScanner().ScanAsync(folder);
    Assert(rejected.Mode != WorkflowMode.Completed && rejected.RequiresProcessing &&
           rejected.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "A fallback report must fail closed when the source image is absent and output equivalence cannot be recomputed.");

    File.Copy(Path.Combine(folder, "02 - Track 2.flac"), Path.Combine(folder, "03 - Unexpected.flac"));
    var unsafeRecovery = await new AlbumScanner().ScanAsync(folder);
    Assert(unsafeRecovery.Mode != WorkflowMode.Completed && unsafeRecovery.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "Recovery must fail closed when an unexpected or incomplete track set is present.");
}

static async Task ScannerRecoversRetainedEquivalentFlacAfterCanceledFallback(string root)
{
    var tools = await new PreflightService().FindToolsAsync();
    if (tools["ffmpeg"] is not { } ffmpeg) return;
    var folder = Path.Combine(root, "recover-retained-flac");
    Directory.CreateDirectory(folder);
    var cover = Path.Combine(folder, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=64x64",
        "-frames:v", "1", "-update", "1", cover);
    var tracks = new List<string>();
    for (var number = 1; number <= 2; number++)
    {
        var track = Path.Combine(folder, $"{number:00} - Track {number}.flac");
        tracks.Add(track);
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", $"sine=frequency={500 + number * 100}:duration=0.08", "-i", cover,
            "-map", "0:a:0", "-map", "1:v:0", "-c:a", "flac", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
            "-metadata", $"TITLE=Track {number}", "-metadata", "ALBUM=Retained Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
            "-metadata", $"TRACKNUMBER={number}/2", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    }
    var source = Path.Combine(folder, "album.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", tracks[0], "-i", tracks[1],
        "-filter_complex", "[0:a][1:a]concat=n=2:v=0:a=1[a]", "-map", "[a]", "-c:a", "flac", source);
    var cuePath = Path.Combine(folder, "album.cue");
    const string cue = """
    FILE "album.flac" WAVE
      TRACK 01 AUDIO
        TITLE "Track 1"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Track 2"
        INDEX 01 00:00:06
    """;
    await File.WriteAllTextAsync(cuePath, cue);
    var reportPath = Path.Combine(folder, "conversion-report.json");
    var report = $$"""
    {
      "workflow_mode": "FlacCueSplit",
      "generated_by": "Album Fixer host fallback",
      "sources": [{ "path": "album.flac", "type": "FLAC image", "size": {{new FileInfo(source).Length}} }],
      "pipeline": { "status": "canceled", "stopped_phase": "CopyingIn", "detail": "A task was canceled." },
      "discs": [],
      "verification": { "status": "canceled", "sources_deleted": false },
      "commit": { "status": "not_completed" },
      "deletion": { "performed": false }
    }
    """;
    await File.WriteAllTextAsync(reportPath, report);

    var recovered = await new AlbumScanner().ScanAsync(folder);
    Assert(recovered.Mode == WorkflowMode.Completed &&
           recovered.Warnings.Any(warning => warning.Contains("CUE boundaries", StringComparison.OrdinalIgnoreCase)),
        "A retained FLAC image must be skipped only after per-track decoded PCM equality at the current CUE boundaries is recomputed.");

    await File.WriteAllTextAsync(cuePath, cue.Replace("00:00:06", "00:00:03", StringComparison.Ordinal));
    var wrongBoundary = await new AlbumScanner().ScanAsync(folder);
    Assert(wrongBoundary.Mode != WorkflowMode.Completed,
        "Concatenated PCM equality must not recover tracks whose individual boundaries disagree with the CUE.");
    await File.WriteAllTextAsync(cuePath, cue);
    await File.WriteAllTextAsync(reportPath, report);

    await RunToolAsync(ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=990:duration=0.1", "-i", cover,
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "flac", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Track 2", "-metadata", "ALBUM=Retained Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=2/2", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", tracks[1]);
    await File.WriteAllTextAsync(reportPath, report);
    var mismatched = await new AlbumScanner().ScanAsync(folder);
    Assert(mismatched.Mode != WorkflowMode.Completed,
        "A retained FLAC image must fail closed when the ordered tracks no longer reconstruct its decoded PCM exactly.");
}

static async Task ScannerRecoversCompletedSacdAfterCanceledFallback(string root)
{
    var folder = Path.Combine(root, "recover-completed-sacd");
    var stereo = Path.Combine(folder, "Stereo");
    Directory.CreateDirectory(stereo);
    CreateTaggedDsfFixture(Path.Combine(stereo, "01 - First.dsf"), "First", 1, 2, 0x55);
    CreateTaggedDsfFixture(Path.Combine(stereo, "02 - Second.dsf"), "Second", 2, 2, 0xAA);
    const long sourceSize = 123456;
    await File.WriteAllTextAsync(Path.Combine(folder, "album.md5"), "0123456789abcdef0123456789abcdef *album.iso");
    await File.WriteAllTextAsync(Path.Combine(folder, "sacd_extract-layout.txt"), $$"""
    The size of sacd is ok
    Size is: {{sourceSize}} bytes
    Area count: 1
    Area Information [0]:
      Track Count: 2
      Speaker config: 2 Channel
      Track list of area[0]:
        Title[1]: First
        Title[2]: Second
    """);
    var successfulLog = """
    Processed 100 audioframes. Duration specified: 100 (00:01:25 [mins:secs:frames])
    Processed 200 audioframes. Duration specified: 200 (00:02:50 [mins:secs:frames])
    We are done exporting DSF...
    Program terminates!
    """;
    var primaryLog = Path.Combine(folder, "sacd_extract-stereo.log");
    var independentLog = Path.Combine(folder, "sacd_extract-stereo-independent.log");
    await File.WriteAllTextAsync(primaryLog, successfulLog);
    await File.WriteAllTextAsync(independentLog, successfulLog);
    var reportPath = Path.Combine(folder, "conversion-report.json");
    var report = $$"""
    {
      "workflow_mode": "DsdExtraction",
      "generated_by": "Album Fixer host fallback",
      "sources": [{ "path": "album.iso", "type": "SACD / DSD image", "size": {{sourceSize}} }],
      "pipeline": { "status": "canceled", "stopped_phase": "Inventoried", "detail": "The operation was canceled." },
      "discs": [],
      "verification": { "status": "canceled", "sources_deleted": false },
      "commit": { "status": "not_completed" },
      "deletion": { "performed": false }
    }
    """;
    await File.WriteAllTextAsync(reportPath, report);

    var recovered = await new AlbumScanner().ScanAsync(folder);
    Assert(recovered.Mode == WorkflowMode.Completed && recovered.TrackCount == 0 &&
           recovered.Media.Count(item => item.Kind == "Previous Album Fixer output") == 2 &&
           recovered.Warnings.Any(warning => warning.Contains("independent extraction", StringComparison.OrdinalIgnoreCase)),
        "A completed SACD extraction with a deleted ISO must be recovered from exact layout, dual-log, DSF, tag, and artwork evidence.");

    await File.WriteAllTextAsync(independentLog, successfulLog.Replace("Processed 200", "Processed 201", StringComparison.Ordinal));
    await File.WriteAllTextAsync(reportPath, report);
    var mismatched = await new AlbumScanner().ScanAsync(folder);
    Assert(mismatched.Mode != WorkflowMode.Completed,
        "SACD fallback recovery must fail closed when primary and independent extraction frame evidence differs.");
}

static void CreateTaggedDsfFixture(string path, string title, uint track, uint trackCount, byte sample)
{
    const uint channels = 2;
    const uint sampleRate = 2_822_400;
    const uint blockSize = 4096;
    const ulong sampleCount = blockSize * 8;
    using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
    using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
    {
        writer.Write(System.Text.Encoding.ASCII.GetBytes("DSD "));
        writer.Write((ulong)28);
        writer.Write((ulong)0);
        writer.Write((ulong)0);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write((ulong)52);
        writer.Write((uint)1);
        writer.Write((uint)0);
        writer.Write((uint)2);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write((uint)1);
        writer.Write(sampleCount);
        writer.Write(blockSize);
        writer.Write((uint)0);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write((ulong)(12 + channels * blockSize));
        writer.Write(Enumerable.Repeat(sample, checked((int)(channels * blockSize))).ToArray());
        writer.Flush();
        stream.Position = 12;
        writer.Write((ulong)stream.Length);
    }
    using var file = TagLib.File.Create(path);
    file.Tag.Title = title;
    file.Tag.Album = "Recovered SACD";
    file.Tag.Performers = ["Tester"];
    file.Tag.AlbumArtists = ["Tester"];
    file.Tag.Track = track;
    file.Tag.TrackCount = trackCount;
    file.Tag.Disc = 1;
    file.Tag.DiscCount = 1;
    file.Tag.Year = 2026;
    file.Tag.Genres = ["Rock"];
    file.Tag.Pictures =
    [
        new TagLib.Picture(new TagLib.ByteVector([0xFF, 0xD8, 0xFF, 0xD9]))
        {
            Type = TagLib.PictureType.FrontCover,
            MimeType = "image/jpeg",
            Description = "Front cover"
        }
    ];
    file.Save();
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

static async Task HostStagesAndChecksSourceSize(string root)
{
    var album = Path.Combine(root, "flac-cue");
    var toolsRoot = Path.Combine(root, "fake-tools"); Directory.CreateDirectory(toolsRoot);
    var ffmpeg = Path.Combine(toolsRoot, "ffmpeg.exe"); await File.WriteAllBytesAsync(ffmpeg, [4, 5, 6]);
    var ffprobe = Path.Combine(toolsRoot, "ffprobe.exe"); await File.WriteAllBytesAsync(ffprobe, [7, 8, 9]);
    var scan = await new AlbumScanner().ScanAsync(album);
    var tempRoot = Path.Combine(root, "jobs"); Directory.CreateDirectory(tempRoot);
    var job = Path.Combine(tempRoot, "job-one"); Directory.CreateDirectory(job);
    var tools = new Dictionary<string, string?> { ["ffmpeg"] = ffmpeg, ["ffprobe"] = ffprobe, ["sacd_extract"] = null };
    var preflight = new PreflightResult([], tempRoot, 0, long.MaxValue, tools);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var stagedSource = Path.Combine(staged.AlbumRoot, "album.flac");
    var originalSource = Path.Combine(album, "album.flac");
    Assert(!staged.SourceCacheUsed && staged.InputAlbumRoot == scan.AlbumRoot,
        "A fixed local album must be read in place without a Windows Temp source cache.");
    Assert(!File.Exists(stagedSource) && !File.Exists(Path.Combine(staged.AlbumRoot, "album.cue")),
        "Local source media and sidecars must not be copied into the Windows Temp output workspace.");
    Assert(staged.Sources.Count == 1 && staged.Sources[0].Size == new FileInfo(originalSource).Length,
        "The in-place local source size was not recorded.");
    Assert(HostStagingService.RequiresSourceCache(@"\\server\share\album"), "A UNC album must use the verified Temp source cache.");
    Assert(File.Exists(staged.FfmpegPath) && File.Exists(staged.FfprobePath), "Required local audio tools were not staged.");
    Assert(!Directory.Exists(Path.Combine(job, "skill")), "A local run must not stage an agent skill.");
    using var manifest = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(staged.ManifestPath));
    var manifestSource = manifest.RootElement.GetProperty("sources")[0];
    Assert(!manifest.RootElement.GetProperty("source_cache_used").GetBoolean() &&
           manifestSource.GetProperty("copy_in_status").GetString() == "not_required_local_fixed_disk" &&
           manifestSource.GetProperty("size").GetInt64() == new FileInfo(originalSource).Length &&
           !manifestSource.TryGetProperty("sha256", out _),
        "The host manifest must record that local fixed-disk source caching was skipped.");
}
static async Task LocalSplitterRunsLocally(string root)
{
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
    var preflight = await new PreflightService().CheckAsync(provisional);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    File.Delete(source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:a", "flac", source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=1200x900", "-frames:v", "1", "-update", "1", cover);
    var scan = await new AlbumScanner().ScanAsync(album);

    var job = Path.Combine(root, "local-split-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [],
        SourceAlbumRoot: album, SourceCacheUsed: false);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 2 && !result.Metadata.RequiresResearch, "Complete local CUE metadata and artwork must split without a fallback process.");
    Assert(File.Exists(Path.Combine(stagedAlbum, "01 - First.flac")) &&
           File.Exists(Path.Combine(stagedAlbum, "02 - Second.flac")), "A single image must create both CUE tracks directly in the album folder.");
    Assert(!Directory.EnumerateFiles(stagedAlbum, "*", SearchOption.AllDirectories).Any(IsImagePath) && File.Exists(result.ReportPath),
        "Local splitting must not create any image file in staging.");
    Assert(File.Exists(source) && File.Exists(cue) && File.Exists(cover), "In-place local inputs must remain untouched during processing.");
    var handoff = await MetadataGapService.LoadAsync(job);
    Assert(!handoff.RequiresResearch && handoff.MissingFields.Count == 0, "Complete local evidence must produce an empty metadata handoff.");
    Assert(!File.Exists(Path.Combine(job, "metadata-agent-events.jsonl")) && !File.Exists(Path.Combine(job, "metadata-agent-final-message.txt")), "The complete local path must not start an agent process.");

    var probeJson = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", Path.Combine(stagedAlbum, "01 - First.flac"));
    using var document = System.Text.Json.JsonDocument.Parse(probeJson);
    var streams = document.RootElement.GetProperty("streams");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "audio" && stream.GetProperty("codec_name").GetString() == "flac"), "The local output has no FLAC stream.");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1), "The local output has no embedded front cover.");
    var tags = document.RootElement.GetProperty("format").GetProperty("tags");
    Assert(tags.EnumerateObject().Any(tag => tag.Name.Equals("TITLE", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() == "First"), "The local track title tag is missing.");
    Assert(tags.EnumerateObject().Any(tag => (tag.Name.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("ALBUM_ARTIST", StringComparison.OrdinalIgnoreCase)) && tag.Value.GetString() == "Test Artist"), "The local album-artist tag is missing.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    var reportCover = report.RootElement.GetProperty("cover");
    Assert(report.RootElement.GetProperty("schema_version").GetString() == "2.0" &&
           reportCover.GetProperty("storage").GetString() == "embedded_only" &&
           reportCover.GetProperty("width").GetInt32() == 600 && reportCover.GetProperty("height").GetInt32() == 600 &&
           reportCover.GetProperty("byte_size").GetInt32() <= 1024 * 1024 &&
           EmbeddedCoverSha256(Path.Combine(stagedAlbum, "01 - First.flac")) == reportCover.GetProperty("sha256").GetString(),
        "The report must prove the bounded in-memory artwork embedded in every FLAC track.");
}

static async Task LocalMetadataEnrichmentRunsInCode(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(seed);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "metadata-lookup", "Test Artist", "Lookup Album");
    Directory.CreateDirectory(album);
    var source = Path.Combine(album, "album.flac");
    var cue = Path.Combine(album, "album.cue");
    var downloadedCoverFixture = Path.Combine(root, "metadata-downloaded-cover-fixture.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=0.25", "-c:a", "flac", source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=900x700", "-frames:v", "1", "-update", "1", downloadedCoverFixture);
    var downloadedCoverBytes = await File.ReadAllBytesAsync(downloadedCoverFixture);
    await File.WriteAllTextAsync(cue, "PERFORMER \"Test Artist\"\nTITLE \"Lookup Album\"\nFILE \"album.flac\" WAVE\n  TRACK 01 AUDIO\n    TITLE \"Track One\"\n    INDEX 01 00:00:00");

    var scan = await new AlbumScanner().ScanAsync(album);
    var job = Path.Combine(root, "metadata-lookup-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [],
        SourceAlbumRoot: album, SourceCacheUsed: false);
    var split = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(split.Metadata.MissingFields.OrderBy(value => value).SequenceEqual(new[] { "COVER", "DATE", "GENRE" }),
        "The fixture must hand COVER, DATE, and GENRE to local enrichment.");

    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[{"id":"release-lookup","score":100,"title":"Lookup Album","artist-credit":[{"name":"Test Artist"}],"release-group":{"id":"group-lookup"},"date":"2001-02-03","track-count":1,"media":[{"format":"CD","track-count":1}],"label-info":[]}]}""");
        if (uri.Contains("/ws/2/release-group/group-lookup", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"first-release-date":"2001-02-03","genres":[{"name":"rock","count":10}],"tags":[],"relations":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"artistName":"Test Artist","collectionName":"Lookup Album","primaryGenreName":"Rock","releaseDate":"2001-02-03T00:00:00Z","trackCount":1,"collectionViewUrl":"https://music.apple.com/album/test"}]}""");
        if (uri.Contains("coverartarchive.org", StringComparison.Ordinal))
            return StubHttpHandler.Bytes(downloadedCoverBytes, "image/jpeg");
        throw new InvalidOperationException($"Unexpected metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var enriched = await new LocalMetadataEnrichmentService(external)
        .EnrichAsync(scan, staged, split, new Progress<ProgressSnapshot>());

    Assert(!enriched.RequiresResearch && enriched.MissingFields.Count == 0,
        "Deterministic local code must resolve the exact album date and genre.");
    var probe = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", Path.Combine(stagedAlbum, "01 - Track One.flac"));
    using var tagDocument = System.Text.Json.JsonDocument.Parse(probe);
    var tags = tagDocument.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
        .ToDictionary(tag => tag.Name, tag => tag.Value.GetString(), StringComparer.OrdinalIgnoreCase);
    Assert((tags.GetValueOrDefault("DATE") == "2001" || tags.GetValueOrDefault("YEAR") == "2001") && tags.GetValueOrDefault("GENRE") == "Rock",
        "Local enrichment did not write the resolved DATE and GENRE tags.");
    Assert(tagDocument.RootElement.GetProperty("streams").EnumerateArray().Any(stream =>
            stream.GetProperty("codec_type").GetString() == "video" &&
            stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1),
        "Local tag enrichment must preserve the embedded front cover.");
    Assert(!Directory.EnumerateFiles(stagedAlbum, "*", SearchOption.AllDirectories).Any(IsImagePath),
        "Metadata enrichment must not create downloaded, temporary, normalized, or sidecar image files.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(split.ReportPath));
    var reportCover = report.RootElement.GetProperty("cover");
    Assert(report.RootElement.GetProperty("metadata_lookup").GetProperty("implementation").GetString() == "deterministic_local_code" &&
           reportCover.GetProperty("storage").GetString() == "embedded_only" &&
           EmbeddedCoverSha256(Path.Combine(stagedAlbum, "01 - Track One.flac")) == reportCover.GetProperty("sha256").GetString(),
        "The report must identify local code and the downloaded in-memory cover used for embedding.");
}

static async Task LocalSplitterCropsAndNormalizesBookletFront(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(seed);
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
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), []);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(!result.Metadata.RequiresResearch, "A recognizable first booklet spread should provide a local front cover without online research.");
    Assert(!File.Exists(Path.Combine(stagedAlbum, "cover.jpg")), "The derived booklet panel must exist only in memory and embedded tags.");
    var trackProbe = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_entries", "stream=codec_type:stream_disposition=attached_pic", "-of", "json", Path.Combine(stagedAlbum, "01 - Track.flac"));
    using var trackDocument = System.Text.Json.JsonDocument.Parse(trackProbe);
    Assert(trackDocument.RootElement.GetProperty("streams").EnumerateArray().Any(stream =>
        stream.GetProperty("codec_type").GetString() == "video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1),
        "The normalized booklet front must be embedded in the split track.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    var reportCover = report.RootElement.GetProperty("cover");
    Assert(reportCover.GetProperty("source").GetString()!.Contains("Booklet 1", StringComparison.OrdinalIgnoreCase) &&
           reportCover.GetProperty("width").GetInt32() == 100 && reportCover.GetProperty("height").GetInt32() == 100 &&
           EmbeddedCoverSha256(Path.Combine(stagedAlbum, "01 - Track.flac")) == reportCover.GetProperty("sha256").GetString(),
        "The right-side booklet panel must be normalized in memory, embedded, and preferred over back artwork.");
}

static async Task LocalSplitterCreatesCdFoldersForMultipleImages(string root)
{
    var album = Path.Combine(root, "Rock", "Test Artist", "(2026) Multi Image Album");
    Directory.CreateDirectory(album);
    var cover = Path.Combine(album, "front.jpg");
    var provisional = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisional);
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
        .Select(item => new StagedSource(item.RelativePath, item.Size)).ToArray();
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), stagedSources);
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

static async Task HostProcessesExistingDuplicateCoverWithoutImageOutputs(string root)
{
    var album = Path.Combine(root, "Rock", "Dire Straits", "(1985) Brothers In Arms [existing cover fixture]");
    var covers = Path.Combine(album, "Covers");
    Directory.CreateDirectory(covers);
    var source = Path.Combine(album, "Dire Straits - Brothers In Arms.flac");
    var cue = Path.Combine(album, "Dire Straits - Brothers In Arms.cue");
    var cover = Path.Combine(album, "cover.jpg");
    var duplicateCover = Path.Combine(covers, "Front.jpg");
    await File.WriteAllBytesAsync(source, [1]);
    await File.WriteAllTextAsync(cue, "FILE \"Dire Straits - Brothers In Arms.flac\" WAVE");
    var provisional = await new AlbumScanner().ScanAsync(album);
    var preflight = await new PreflightService().CheckAsync(provisional);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    File.Delete(source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=9.25", "-c:a", "flac", source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=1200x900", "-frames:v", "1", "-update", "1", cover);
    File.Copy(cover, duplicateCover);
    var cueText = new System.Text.StringBuilder("REM GENRE Rock\nREM DATE 1985\nPERFORMER \"Dire Straits\"\nTITLE \"Brothers In Arms\"\nFILE \"Dire Straits - Brothers In Arms.flac\" WAVE\n");
    for (var track = 1; track <= 9; track++)
    {
        cueText.Append($"  TRACK {track:00} AUDIO\n    TITLE \"Track {track}\"\n    INDEX 01 00:{track - 1:00}:00\n");
    }
    await File.WriteAllTextAsync(cue, cueText.ToString());
    var originalImages = Directory.EnumerateFiles(album, "*", SearchOption.AllDirectories)
        .Where(IsImagePath)
        .ToDictionary(path => Path.GetRelativePath(album, path), FileSha256, StringComparer.OrdinalIgnoreCase);

    var scan = await new AlbumScanner().ScanAsync(album);
    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"existing-cover-{Guid.NewGuid():N}");
    var progress = new Progress<ProgressSnapshot>();
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, progress);
    var local = await new LocalFlacProcessor().ProcessAsync(scan, staged, progress);
    Assert(local.Tracks == 9 && !Directory.EnumerateFiles(staged.AlbumRoot, "*", SearchOption.AllDirectories).Any(IsImagePath),
        "Nine tracks must be produced with embedded artwork and no generated image files in local staging.");

    var abandonedStage = Path.Combine(album, $".album-fixer-stage-{Path.GetFileName(job)}");
    Directory.CreateDirectory(abandonedStage);
    var committed = await new HostCommitService().CommitAsync(scan, staged, progress, deleteOriginals: false);
    Assert(committed.Tracks == 9 && !Directory.Exists(abandonedStage) &&
           !Directory.EnumerateFileSystemEntries(album, $"{WorkflowCleanupService.DestinationStagePrefix}*", SearchOption.TopDirectoryOnly).Any(),
        "The completed workflow must remove current and abandoned destination staging folders.");

    var finalImages = Directory.EnumerateFiles(album, "*", SearchOption.AllDirectories)
        .Where(IsImagePath)
        .ToDictionary(path => Path.GetRelativePath(album, path), FileSha256, StringComparer.OrdinalIgnoreCase);
    Assert(originalImages.Count == finalImages.Count && originalImages.All(pair => finalImages.GetValueOrDefault(pair.Key) == pair.Value),
        "The existing root cover and duplicate Covers/Front artwork must remain the only image files and stay byte-identical.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(committed.ReportPath));
    var reportCover = report.RootElement.GetProperty("cover");
    var expectedArtworkHash = reportCover.GetProperty("sha256").GetString();
    Assert(reportCover.GetProperty("storage").GetString() == "embedded_only" &&
           !reportCover.TryGetProperty("file", out _) &&
           Enumerable.Range(1, 9).All(track =>
               EmbeddedCoverSha256(Path.Combine(album, $"{track:00} - Track {track}.flac")) == expectedArtworkHash),
        "Every final FLAC must contain the same report-proven in-memory artwork and the report must expose no image path.");
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

static string EmbeddedCoverDescriptorJson(string trackPath, int width, int height, string source)
{
    using var file = TagLib.File.Create(trackPath);
    var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
        ?? file.Tag.Pictures.FirstOrDefault()
        ?? throw new InvalidOperationException("The fixture track has no embedded artwork.");
    var bytes = picture.Data.Data;
    return System.Text.Json.JsonSerializer.Serialize(new
    {
        storage = "embedded_only",
        source,
        mime_type = "image/jpeg",
        width,
        height,
        byte_size = bytes.Length,
        sha256 = Convert.ToHexString(SHA256.HashData(bytes))
    });
}

static string? EmbeddedCoverSha256(string trackPath)
{
    using var file = TagLib.File.Create(trackPath);
    var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
        ?? file.Tag.Pictures.FirstOrDefault();
    return picture is null ? null : Convert.ToHexString(SHA256.HashData(picture.Data.Data));
}

static string FileSha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

static bool IsImagePath(string path) => new[] { ".jpg", ".jpeg", ".png" }
    .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

static async Task HostCommitsVerifiedFlac(string root, bool deleteOriginals)
{
    var destination = Path.Combine(root, deleteOriginals ? "commit-destination" : "commit-retain-original"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisionalScan = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisionalScan);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=1000:duration=0.25", "-c:a", "flac", source);
    var cover = Path.Combine(destination, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var originalCoverHash = FileSha256(cover);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"commit-test-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var track = Path.Combine(stagedAlbum, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-i", cover,
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "copy", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Test", "-metadata", "ALBUM=Test Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    var coverDescriptor = EmbeddedCoverDescriptorJson(track, 64, 64, "existing user cover");
    var report = $$"""
    {
      "schema_version": "2.0",
      "album": "Test Album",
      "edition": "Synthetic transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": {{coverDescriptor}},
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Test", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """;
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), report);
    var manifest = Path.Combine(job, "host-manifest.json"); await File.WriteAllTextAsync(manifest, "{}");
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, manifest, [stagedSource],
        PipelineLimits: new(6, 2, 4, 2, 2), PipelineTelemetry: new(2, 4, 1, 2));
    Assert(!Directory.EnumerateFiles(stagedAlbum, "*", SearchOption.AllDirectories).Any(IsImagePath),
        "The commit staging tree must contain embedded artwork only, not an image sidecar.");
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals);
    Assert(result.Tracks == 1 && result.SourcesDeleted == deleteOriginals,
        deleteOriginals ? "The exact source must be deleted after final quick checks." : "The source must be retained when deletion is not requested.");
    Assert(File.Exists(Path.Combine(destination, "01 - Test.flac")) && FileSha256(cover) == originalCoverHash,
        "The verified track must be committed while the existing user cover remains byte-identical.");
    Assert(File.Exists(source) != deleteOriginals && File.Exists(cue) && File.Exists(result.ReportPath),
        "The source disposition did not match the requested delete-originals option; the CUE and final report must remain.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == "passed" && summary.Tracks == 1 && summary.Deleted == deleteOriginals,
        "The final report did not record the requested source disposition.");
    using var finalReport = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    var deletion = finalReport.RootElement.GetProperty("deletion");
    var commit = finalReport.RootElement.GetProperty("commit");
    Assert(commit.GetProperty("destination_sizes_verified").GetBoolean() &&
           commit.GetProperty("files").EnumerateArray().All(file =>
               file.TryGetProperty("size", out var size) && size.GetInt64() > 0 && !file.TryGetProperty("sha256", out _) &&
               !IsImagePath(file.GetProperty("file").GetString()!)),
        "The final report must record file-size checks without committing image files.");
    Assert(deleteOriginals || deletion.GetProperty("policy").GetString() == "source_retained_by_user_request",
        "The final report did not record that the user chose to retain originals.");
    Assert(deleteOriginals || !finalReport.RootElement.GetProperty("verification").GetProperty("source_deletion_requested").GetBoolean(),
        "The final verification must record that source deletion was not requested.");
    var pipeline = finalReport.RootElement.GetProperty("pipeline");
    Assert(pipeline.GetProperty("configured").GetProperty("processing_workers").GetInt32() == 4 &&
           pipeline.GetProperty("observed_at_commit").GetProperty("copy_back_workers").GetInt32() == 2,
        "The final album report must record configured and observed stage-aware pipeline limits.");
}

static async Task HostReplacesVerifiedRootOutput(string root)
{
    var destination = Path.Combine(root, "replace-verified-root-output"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisional = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisional);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=900:duration=0.25", "-c:a", "flac", source);
    var cover = Path.Combine(destination, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var priorTrack = Path.Combine(destination, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", source, "-c:a", "copy", priorTrack);
    var priorTrackSize = new FileInfo(priorTrack).Length;
    var coverSize = new FileInfo(cover).Length;
    var originalCoverHash = FileSha256(cover);
    await File.WriteAllTextAsync(Path.Combine(destination, "conversion-report.json"), $$"""
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "file": "01 - Test.flac" }] }],
      "cover": { "file": "cover.jpg" },
      "verification": { "status": "passed" },
      "commit": { "files": [
        { "file": "01 - Test.flac", "size": {{priorTrackSize}} },
        { "file": "cover.jpg", "size": {{coverSize}} }
      ] }
    }
    """);
    var scan = await new AlbumScanner().ScanAsync(destination);
    Assert(scan.Mode == WorkflowMode.FlacCueSplit, "A verified root output must permit a new FLAC+CUE split.");
    var plan = PreviousOutputCleanupService.DiscoverVerified(destination);
    Assert(plan is not null && plan.Files.Count == 1 && plan.Files[0].RelativePath == "01 - Test.flac",
        "Only the verified audio output may be scheduled for replacement; the v1 cover sidecar must be preserved.");

    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"replace-verified-root-output-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var replacementTrack = Path.Combine(stagedAlbum, "01 - Test.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-i", cover,
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "copy", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Replacement", "-metadata", "ALBUM=Replacement Album", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", replacementTrack);
    var replacementCoverDescriptor = EmbeddedCoverDescriptorJson(replacementTrack, 64, 64, "preserved v1 cover sidecar");
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), $$"""
    {
      "schema_version": "2.0",
      "album": "Replacement Album",
      "edition": "Synthetic replacement test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": {{replacementCoverDescriptor}},
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Replacement", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [], PreviousVerifiedOutput: plan);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(result.Tracks == 1 && File.Exists(priorTrack), "The report-proven root track was not replaced.");
    var output = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_format", "-of", "json", priorTrack);
    using var document = System.Text.Json.JsonDocument.Parse(output);
    var finalTitle = document.RootElement.GetProperty("format").GetProperty("tags").EnumerateObject()
        .FirstOrDefault(property => property.Name.Equals("TITLE", StringComparison.OrdinalIgnoreCase)).Value.GetString();
    Assert(finalTitle == "Replacement",
        "The final root track did not contain the verified replacement output.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    Assert(report.RootElement.GetProperty("commit").GetProperty("replaced_previous_outputs").GetArrayLength() == 1 &&
           FileSha256(cover) == originalCoverHash,
        "The final report must record only audio replacement and preserve the v1 cover sidecar byte-for-byte.");
}

static async Task HostCommitsMultipleImagesAndRetainsSources(string root)
{
    var destination = Path.Combine(root, "multi-commit-destination"); Directory.CreateDirectory(destination);
    var provisionalScan = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(provisionalScan);
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
    var cover = Path.Combine(destination, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=yellow:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var originalCoverHash = FileSha256(cover);
    var scan = await new AlbumScanner().ScanAsync(destination);
    var job = Path.Combine(Path.GetTempPath(), "album-fixer", $"multi-commit-test-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    var stagedSources = new List<StagedSource>();
    foreach (var item in scan.Media.Where(item => item.Kind == "FLAC image"))
        stagedSources.Add(new(item.RelativePath, item.Size));
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), stagedSources,
        SourceAlbumRoot: destination, SourceCacheUsed: false);

    var local = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(!local.Metadata.RequiresResearch, "The multi-image commit fixture should not require metadata research.");
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 2 && !result.SourcesDeleted, "A multi-image commit must retain every original source.");
    Assert(File.Exists(Path.Combine(destination, "CD1", "01 - Committed Disc 1.flac")) &&
           File.Exists(Path.Combine(destination, "CD2", "01 - Committed Disc 2.flac")), "The CD<n> tracks were not committed to final paths.");
    Assert(File.Exists(Path.Combine(destination, "disc1.flac")) && File.Exists(Path.Combine(destination, "disc2.flac")), "Multi-image originals must be retained without explicit deletion authorization.");
    Assert(FileSha256(cover) == originalCoverHash, "The multi-image commit must preserve existing artwork byte-for-byte.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(result.ReportPath));
    Assert(report.RootElement.GetProperty("deletion").GetProperty("status").GetString() == "retained" &&
           !report.RootElement.GetProperty("verification").GetProperty("sources_deleted").GetBoolean(), "The final report must record retained multi-image sources.");
}

static async Task HostCommitsIncompleteFlacWithoutArtwork(string root)
{
    var destination = Path.Combine(root, "incomplete-artwork-destination");
    Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisional = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisional);
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
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 1 && result.Incomplete && !result.SourcesDeleted, "Missing artwork must deliver tracks as incomplete work without deleting the source.");
    Assert(File.Exists(Path.Combine(destination, "01 - Test.flac")) && File.Exists(source), "The incomplete track and original source must both remain in the album folder.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == "incomplete" && !summary.Deleted, "The final report must mark deferred artwork as incomplete work.");
    var retryPlan = PreviousOutputCleanupService.Discover(destination);
    Assert(retryPlan is not null && retryPlan.Files.Any(file => file.RelativePath.Equals("01 - Test.flac", StringComparison.OrdinalIgnoreCase)),
        "A later retry must recognize the size-proven incomplete root track for safe replacement.");
}

static async Task HostCommitFailureRetainsSource(string root)
{
    var destination = Path.Combine(root, "failed-commit-destination"); Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisionalScan = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisionalScan);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=750:duration=0.25", "-c:a", "flac", source);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(root, "failed-commit-job");
    var stagedAlbum = Path.Combine(job, "album"); Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var track = Path.Combine(stagedAlbum, "01 - Invalid.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-c:a", "copy", track);
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), """
    {
      "album": "Invalid transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Invalid", "file": "01 - Invalid.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);

    Exception? failure = null;
    try { await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>()); }
    catch (Exception error) { failure = error; }

    Assert(failure is InvalidOperationException && failure.Message.Contains("tags", StringComparison.OrdinalIgnoreCase),
        "Missing required tags must still fail the quick playback-file checks.");
    Assert(File.Exists(source), "A failed commit must retain the exact source FLAC.");
    Assert(!File.Exists(Path.Combine(destination, "01 - Invalid.flac")), "A failed local check must not commit the output track.");
}

static async Task FailureCleanupRemovesLocalAndDestinationStages(string root)
{
    var destination = Path.Combine(root, "failed-after-copyback-start");
    Directory.CreateDirectory(destination);
    var source = Path.Combine(destination, "source.flac");
    var cue = Path.Combine(destination, "source.cue");
    await File.WriteAllTextAsync(cue, "FILE \"source.flac\" WAVE\n  TRACK 01 AUDIO\n    INDEX 01 00:00:00");
    var provisional = await new AlbumScanner().ScanAsync(destination);
    var preflight = await new PreflightService().CheckAsync(provisional);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=810:duration=0.25", "-c:a", "flac", source);
    var cover = Path.Combine(destination, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=green:s=64x64", "-frames:v", "1", "-update", "1", cover);
    var scan = await new AlbumScanner().ScanAsync(destination);

    var job = Path.Combine(preflight.TempRoot, $"failure-cleanup-{Guid.NewGuid():N}");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    File.Copy(source, Path.Combine(stagedAlbum, "source.flac"));
    File.Copy(cue, Path.Combine(stagedAlbum, "source.cue"));
    var track = Path.Combine(stagedAlbum, "01 - Collision.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-i", cover,
        "-map", "0:a:0", "-map", "1:v:0", "-c:a", "copy", "-c:v", "mjpeg", "-disposition:v:0", "attached_pic",
        "-metadata", "TITLE=Collision", "-metadata", "ALBUM=Cleanup Test", "-metadata", "ARTIST=Tester", "-metadata", "ALBUMARTIST=Tester",
        "-metadata", "TRACKNUMBER=1/1", "-metadata", "DISCNUMBER=1/1", "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", track);
    var coverDescriptor = EmbeddedCoverDescriptorJson(track, 64, 64, "existing user cover");
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), $$"""
    {
      "schema_version": "2.0",
      "album": "Cleanup Test",
      "edition": "Synthetic failure cleanup test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": {{coverDescriptor}},
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Collision", "file": "01 - Collision.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    File.Copy(track, Path.Combine(destination, "01 - Collision.flac"));
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"),
        [new("source.flac", new FileInfo(source).Length)]);

    Exception? failure = null;
    try { await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: false); }
    catch (Exception error) { failure = error; }
    Assert(failure is IOException && failure.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase),
        "The fixture must fail after destination staging begins because its final audio path is unproven.");
    Assert(!Directory.EnumerateFileSystemEntries(destination, $"{WorkflowCleanupService.DestinationStagePrefix}*", SearchOption.TopDirectoryOnly).Any(),
        "A failed commit must remove its destination staging folder.");

    var reportPath = await HostReportWriter.EnsureTerminalReportAsync(scan, preflight, job, "failed", JobPhase.CopyingBack, 58, failure!.Message);
    var localCleaned = await WorkflowCleanupService.CleanupLocalJobAsync(job, preflight.TempRoot);
    Assert(localCleaned && !Directory.Exists(job), "A failed workflow must remove its local Temp job after writing the terminal report.");
    Assert(File.Exists(reportPath) && Path.GetFullPath(reportPath).StartsWith(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase),
        "The failure report must remain in the album folder after transient cleanup.");
    Assert(File.Exists(source), "Failure cleanup must retain the original source.");
}

static void SacdLayoutParserSupportsToolIndexConventions()
{
    const string currentOutput = """
    Album Information:
        Album Catalog Number: UIGY-9519
        Title: Communique
        Artist: Dire Straits
    Disc Information:
        Creation date: 2011-12-15
    Area count: 1
        Area Information [0]:
        Track Count: 2
        Total play time: 09:36:48 [mins:secs:frames]
        Speaker config: 2 Channel
        Title[1]: Once Upon A Time In The West
        Performer[1]: Dire Straits
        ISRC[1]: GBF087900658 (country:GB, owner:F08, year:79, designation:00658)
        Duration: 05:24:40 [mins:secs:frames]
        Title[2]: News
        Performer[2]: Dire Straits
        ISRC[2]: GBF087900659 (country:GB, owner:F08, year:79, designation:00659)
        Duration: 04:12:08 [mins:secs:frames]
    """;
    var current = LocalDsdProcessor.ParseLayout(currentOutput);
    Assert(current.CatalogNumber == "UIGY-9519" && current.Areas.Count == 1 && current.Areas[0].Tracks.Count == 2,
        "The current sacd_extract one-based layout must parse as one two-track area.");
    Assert(current.Areas[0].Tracks[0].Number == 1 && current.Areas[0].Tracks[0].Title == "Once Upon A Time In The West" &&
           current.Areas[0].Tracks[0].Isrc == "GBF087900658" && current.Areas[0].Tracks[1].Isrc == "GBF087900659",
        "One-based SACD titles, performers, and current-format ISRC values must retain their track association.");

    const string legacyOutput = """
    Title: Legacy Album
    Artist: Legacy Artist
    Area Information [0]:
    Track Count: 1
    Total play time: 03:00:00 [mins:secs:frames]
    Speaker config: 2 Channel
    Title[0]: Legacy Track
    Performer[0]: Legacy Performer
    ISRC Track [0]:
        Country: GB, Owner: ABC, Year: 79, Designation: 00001
    Duration: 03:00:00 [mins:secs:frames]
    """;
    var legacy = LocalDsdProcessor.ParseLayout(legacyOutput);
    Assert(legacy.Areas[0].Tracks[0].Title == "Legacy Track" &&
           legacy.Areas[0].Tracks[0].Performer == "Legacy Performer" &&
           legacy.Areas[0].Tracks[0].Isrc == "GBABC7900001",
        "Legacy zero-based title indexes and multiline ISRC values must remain supported.");

    Exception? invalidIndexes = null;
    try { LocalDsdProcessor.ParseLayout(currentOutput.Replace("Title[2]", "Title[3]", StringComparison.Ordinal)); }
    catch (Exception error) { invalidIndexes = error; }
    Assert(invalidIndexes is InvalidDataException && invalidIndexes.Message.Contains("noncontiguous", StringComparison.OrdinalIgnoreCase),
        "A gapped SACD title table must produce a clear validation error instead of a dictionary exception.");
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
    var tools = new Dictionary<string, string?> { ["ffmpeg"] = "ffmpeg.exe", ["ffprobe"] = "ffprobe.exe", ["sacd_extract"] = null };
    var preflight = new PreflightResult([], Path.GetTempPath(), 0, long.MaxValue, tools);
    var job = Path.Combine(root, "failed-job");
    var path = await HostReportWriter.EnsureTerminalReportAsync(scan, preflight, job, "failed", JobPhase.Failed, 1,
        "Inventory access was denied.");
    Assert(File.Exists(path), "A stopped run must preserve a conversion report.");
    var report = await ReportReader.LoadAsync(path);
    Assert(report.Status == "failed" && !report.Deleted && report.Errors.Count == 1, "Failure report status or retention state is wrong.");
    using var document = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(path));
    Assert(document.RootElement.GetProperty("workflow_mode").GetString() == "flac_cue_split",
        "Fallback reports must use the same canonical workflow identifier as completion readers.");
    Assert(document.RootElement.GetProperty("job").GetProperty("staging_preserved").GetBoolean() == false &&
           document.RootElement.GetProperty("job").GetProperty("cleanup_policy").GetString() == "always_remove_after_terminal_report",
        "A terminal failure report must require transient cleanup instead of promising a preserved Temp job.");
}

static async Task FailureReportPreservesCompletedReport(string root)
{
    var folder = Path.Combine(root, "completed-report-preservation");
    Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "01.flac"), [1, 2, 3]);
    var canonicalPath = Path.Combine(folder, "conversion-report.json");
    const string canonical = """
    {
      "workflow_mode": "flac_cue_split",
      "discs": [{ "tracks": [{ "file": "01.flac" }] }],
      "verification": { "status": "passed", "sources_deleted": false },
      "commit": { "status": "completed", "files": [{ "file": "01.flac", "size": 3 }] },
      "deletion": { "status": "retained", "performed": false }
    }
    """;
    await File.WriteAllTextAsync(canonicalPath, canonical);
    var scan = new ScanResult(folder, "Completed report preservation", WorkflowMode.FlacCueSplit,
        [], [], [], 0, 0, 0, 0, true, false);
    var tools = new Dictionary<string, string?> { ["ffmpeg"] = "ffmpeg.exe", ["ffprobe"] = "ffprobe.exe", ["sacd_extract"] = null };
    var preflight = new PreflightResult([], Path.GetTempPath(), 0, long.MaxValue, tools);
    var failurePath = await HostReportWriter.EnsureTerminalReportAsync(scan, preflight,
        Path.Combine(root, "completed-report-preservation-job"), "failed", JobPhase.Inventoried, 1, "A stale attempt failed.");

    Assert(await File.ReadAllTextAsync(canonicalPath) == canonical,
        "A later failed attempt must never overwrite a completed canonical report.");
    Assert(!failurePath.Equals(canonicalPath, StringComparison.OrdinalIgnoreCase) &&
           Path.GetFileName(failurePath).StartsWith("conversion-report.failed-", StringComparison.OrdinalIgnoreCase),
        "A failure after completion must be written as a separate timestamped report.");
    var completedScan = await new AlbumScanner().ScanAsync(folder);
    Assert(completedScan.Mode == WorkflowMode.Completed && !completedScan.RequiresProcessing,
        "A preserved completed report must keep the album out of the candidate list.");
}

static async Task MetadataHandoffIsConditional(string root)
{
    var completeJob = Path.Combine(root, "metadata-complete");
    Directory.CreateDirectory(completeJob);
    await File.WriteAllTextAsync(MetadataGapService.GetPath(completeJob), """
    {"split_completed":true,"requires_research":false,"missing_fields":[],"local_evidence":["CUE","cover scan"]}
    """);
    var complete = await MetadataGapService.LoadAsync(completeJob);
    Assert(complete.SplitCompleted && !complete.RequiresResearch && complete.MissingFields.Count == 0, "Complete local metadata must skip catalog lookup.");

    var missingJob = Path.Combine(root, "metadata-missing");
    Directory.CreateDirectory(missingJob);
    await File.WriteAllTextAsync(MetadataGapService.GetPath(missingJob), """
    {"split_completed":true,"requires_research":true,"missing_fields":["GENRE","DATE"],"local_evidence":["CUE"]}
    """);
    var missing = await MetadataGapService.LoadAsync(missingJob);
    Assert(missing.RequiresResearch && missing.MissingFields.SequenceEqual(["GENRE", "DATE"]), "Only named metadata gaps should trigger deterministic lookup.");
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

static async Task ExternalCoverDownloadIsMemoryOnlyAndBounded()
{
    var expected = new byte[] { 1, 2, 3, 4 };
    using (var client = new HttpClient(new StubHttpHandler((request, _) =>
           request.RequestUri?.AbsoluteUri.Contains("coverartarchive.org", StringComparison.Ordinal) == true
               ? StubHttpHandler.Bytes(expected, "image/jpeg")
               : throw new InvalidOperationException("Unexpected cover request."))))
    {
        var service = new ExternalMetadataService(client, requestTimeout: TimeSpan.FromSeconds(1));
        var downloaded = await service.DownloadFrontCoverAsync("release-memory-only");
        Assert(downloaded.Data.SequenceEqual(expected) && downloaded.MimeType == "image/jpeg" &&
               downloaded.Source.Contains("coverartarchive.org", StringComparison.Ordinal),
            "Cover Art Archive bytes must be returned in memory with MIME type and provenance.");
    }

    using (var client = new HttpClient(new StubHttpHandler((_, _) =>
           StubHttpHandler.Bytes(new byte[15 * 1024 * 1024 + 1], "image/jpeg"))))
    {
        var service = new ExternalMetadataService(client, requestTimeout: TimeSpan.FromSeconds(1));
        Exception? oversized = null;
        try { await service.DownloadFrontCoverAsync("release-too-large"); }
        catch (Exception error) { oversized = error; }
        Assert(oversized is InvalidDataException, "An external cover above 15 MB must be rejected before normalization.");
    }

    using (var client = new HttpClient(new StubHttpHandler((_, _) => StubHttpHandler.Bytes(expected, "image/jpeg"))))
    {
        var service = new ExternalMetadataService(client, requestTimeout: TimeSpan.FromSeconds(1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Exception? cancellation = null;
        try { await service.DownloadFrontCoverAsync("release-canceled", canceled.Token); }
        catch (Exception error) { cancellation = error; }
        Assert(cancellation is OperationCanceledException, "A canceled in-memory cover download must stop without creating an image artifact.");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task<int> ProcessSacdAlbum(string albumRoot)
{
    ScanResult? scan = null;
    PreflightResult? preflight = null;
    string? jobDirectory = null;
    try
    {
        scan = await new AlbumScanner().ScanAsync(albumRoot);
        preflight = await new PreflightService().CheckAsync(scan);
        foreach (var check in preflight.Checks)
            Console.WriteLine($"[{check.State}] {check.Name}: {check.Detail}");
        if (!preflight.CanStart)
            throw new InvalidOperationException("The SACD album did not pass preflight.");

        jobDirectory = PreflightService.CreateJobDirectory(preflight.TempRoot);
        var progress = new Progress<ProgressSnapshot>(snapshot =>
            Console.WriteLine($"{snapshot.Percent,3}% {snapshot.Phase}: {snapshot.Detail}"));
        var staged = await new HostStagingService().StageAsync(scan, preflight, jobDirectory, progress);
        var local = await new LocalDsdProcessor().ProcessAsync(scan, staged, progress);
        if (local.Metadata.RequiresResearch)
            Console.WriteLine($"SACD metadata remains incomplete ({string.Join(", ", local.Metadata.MissingFields)}); committing verified tracks and retaining the source ISO.");
        var committed = await new HostCommitService().CommitAsync(scan, staged, progress);
        Console.WriteLine($"SACD completed: {committed.Tracks} tracks; source deleted: {committed.SourcesDeleted}; report: {committed.ReportPath}");
        return 0;
    }
    catch (Exception error)
    {
        if (scan is not null && preflight is not null && jobDirectory is not null && Directory.Exists(jobDirectory))
            await HostReportWriter.EnsureTerminalReportAsync(scan, preflight, jobDirectory, "failed", JobPhase.Failed, 0, error.Message);
        Console.Error.WriteLine(error);
        return 1;
    }
    finally
    {
        if (Directory.Exists(albumRoot)) await WorkflowCleanupService.CleanupDestinationStagesAsync(albumRoot);
        if (preflight is not null && jobDirectory is not null)
            await WorkflowCleanupService.CleanupLocalJobAsync(jobDirectory, preflight.TempRoot);
    }
}

sealed class StubHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
            ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
            : Task.FromResult(responder(request, cancellationToken));

    public static HttpResponseMessage Json(string json) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
    };

    public static HttpResponseMessage Bytes(byte[] bytes, string mediaType) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new ByteArrayContent(bytes)
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType) }
        }
    };
}
