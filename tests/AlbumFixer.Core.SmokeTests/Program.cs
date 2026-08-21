using System.Diagnostics;
using System.Buffers.Binary;
using System.Security.Cryptography;
using AlbumFixer.Core;

if (args is ["--process-sacd", var sacdAlbum])
    return await ProcessSacdAlbum(sacdAlbum);
if (args is ["--scan", var scanAlbum])
{
    var scan = await new AlbumScanner().ScanAsync(scanAlbum);
    var preflight = await new PreflightService().CheckAsync(scan);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        scan.AlbumRoot,
        Mode = scan.Mode.ToString(),
        preflight.CanStart,
        scan.ImageCount,
        scan.TrackCount,
        Media = scan.Media.Select(item => new { item.RelativePath, item.Kind }),
        scan.Warnings,
        scan.Errors,
        BlockingChecks = preflight.Checks.Where(check => check.BlocksRun && check.State == CheckState.Failed)
            .Select(check => new { check.Name, check.Detail })
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
if (args is ["--prepare-artwork", var artworkAlbum])
{
    var scan = await new AlbumScanner().ScanAsync(artworkAlbum);
    var preflight = await new PreflightService().CheckAsync(scan);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe)
        return 2;
    var mode = scan.HasDsd ? ArtworkSelectionMode.Dsd : ArtworkSelectionMode.Flac;
    var result = await new InMemoryArtworkService().PrepareLocalAsync(artworkAlbum, ffmpeg, ffprobe, mode);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        Prepared = result.Artwork is not null,
        result.Issue,
        Source = result.Artwork?.Source,
        result.Artwork?.Width,
        result.Artwork?.Height,
        result.Artwork?.ByteSize
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return result.Artwork is null ? 1 : 0;
}

var root = Path.Combine(Path.GetTempPath(), "album-fixer-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ToolDiscoveryReportsEveryStartupComponent();
    await ScannerClassifiesFlacCue(root);
    await ScannerInventoriesChecksumIdentitySidecars(root);
    await ScannerDescendsIntoSingleNestedAlbum(root);
    await ScannerPrefersExistingTracks(root);
    await ScannerAcceptsVerifiedLegacySplitCue(root);
    await ScannerSkipsCompleteTaggedMultiDiscTracksWithMissingHistoricalCueSources(root);
    await ScannerSkipsCompleteTaggedDsdTracksAndAreas(root);
    await ScannerPlansMultipleAlbums(root);
    await ScannerRoutesTrackPerFileCuesAndScopesNestedAlbums(root);
    await ScannerAcceptsMultipleImagesInOneAlbumFolder(root);
    await ScannerRecognizesAndCleansIncompletePreviousOutput(root);
    await ScannerSkipsCompletedAlbumsWithDeletedSources(root);
    await ScannerAdoptsOptionalOnlyVerifiedSacdCompletion(root);
    await ScannerRoutesStandaloneDsfAndReusableIncompleteSacd(root);
    await ArchivedSacdArtifactsAreTransactionallyReplaceable(root);
    await ScannerRecoversVerifiedMissingSourceFallbackCompletion(root);
    await ScannerRecoversRetainedEquivalentFlacAfterCanceledFallback(root);
    await ScannerRecoversCompletedSacdAfterCanceledFallback(root);
    await AlbumTransactionLockSerializesOwners(root);
    BatchPreflightSkipsBlockedAlbums();
    await BoundedBatchRunsConcurrentlyAndIsolatesFailures();
    PipelineLimitsScaleWithHardwareAndCapacity();
    await StageAwarePipelineBoundsEveryLane();
    await HostStagesAndChecksSourceSize(root);
    await LocalSplitterRunsLocally(root);
    await LocalClassicalCueInfersComposer(root);
    await LocalMetadataEnrichmentRunsInCode(root);
    await ExternalArtworkFallbackSupportsSacdAndPreservesLocalPriority(root);
    await ExistingTracksRepairUsesPrioritizedEvidenceAndTransactionalCommit(root);
    await ExistingCompilationUsesDiscogsTrackArtistsAndPrimaryCover(root);
    await ExistingTrackCorruptionNamesTheExactFile(root);
    await TrackPerFileCueRepairsTransactionally(root);
    await StandaloneDsfRepairPreservesNativePayloadAndDeletesRetainedIso(root);
    await StandaloneDffRepairPreservesNativePayloadAndDeletesRetainedIso(root);
    await ExactDuplicateExistingTracksAreCollapsedTransactionally(root);
    DuplicateTaggedBonusTrackUsesFilenameAnchor();
    RecognizedGenreFoldersAreNotArtistEvidence();
    ClassicalComposerUsesCorroboratedAlbumIdentity();
    ClassicalTrackComposersUseReviewedAlbumAndWorkEvidence();
    CompilationIdentityAndClassicalTrackEvidenceAreConservative();
    ExternalAlbumTitleQualifiersRemainEquivalent();
    await ExistingTracksRepairSupportsMultipleDiscs(root);
    await LocalSplitterCropsAndNormalizesBookletFront(root);
    await LocalSplitterCreatesCdFoldersForMultipleImages(root);
    await HostProcessesExistingDuplicateCoverWithoutImageOutputs(root);
    await HostCommitsVerifiedFlac(root, deleteOriginals: true);
    await HostCommitsVerifiedFlac(root, deleteOriginals: false);
    await HostCommitsVerifiedFlac(root, deleteOriginals: true, optionalMetadataMissing: true);
    await HostCommitsVerifiedFlac(root, deleteOriginals: true, requiredMetadataMissing: true);
    await HostReplacesVerifiedRootOutput(root);
    await HostCommitsMultipleImagesAndRetainsSources(root);
    await HostCommitsIncompleteFlacWithoutArtwork(root);
    await HostCommitFailureRetainsSource(root);
    await FailureCleanupRemovesLocalAndDestinationStages(root);
    SacdLayoutParserSupportsToolIndexConventions();
    await ExternalCatalogIdentityResolvesMissingSacdDiscText();
    await FailureReportIsAlwaysWritten(root);
    await FailureReportPreservesCompletedReport(root);
    await MetadataHandoffIsConditional(root);
    await ExternalMetadataResolvesExactSacdRelease();
    await ExternalMetadataImportsExactTrackComposersAndCuratedGenre();
    await ExternalMetadataReadsEmbeddedDiscogsComposerCredits();
    await ExternalMetadataAlignsAbbreviatedOperaTitlesWithCredits();
    await ExternalMetadataFallsBackFromFolderTitleToLinkedDiscogsTrackCredits();
    await ExternalMetadataAlignsPartialMixedCompilation();
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

static async Task ScannerInventoriesChecksumIdentitySidecars(string root)
{
    var folder = Path.Combine(root, "checksum-identity", "1988 - Spirit Of Eden (2003 Remaster SACD-R)");
    Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "Unknown Album.iso"), [1, 2, 3]);
    await File.WriteAllTextAsync(Path.Combine(folder, "Talk Talk - Spirit Of Eden.md5"),
        "0123456789abcdef0123456789abcdef *Unknown Album.iso");
    await File.WriteAllTextAsync(Path.Combine(folder, "Talk Talk - Spirit Of Eden.sfv"),
        "Unknown Album.iso 12345678");

    var scan = await new AlbumScanner().ScanAsync(folder);
    var checksumSidecars = scan.Media
        .Where(item => Path.GetExtension(item.Path) is ".md5" or ".sfv")
        .ToArray();
    Assert(scan.Mode == WorkflowMode.DsdExtraction && checksumSidecars.Length == 2 &&
           checksumSidecars.All(item => item.Kind == "Provenance"),
        "The scanner must inventory MD5 and SFV sidecars so SACD identity fallback can inspect their filenames.");

    const string missingDiscText = """
    Area Information [0]:
    Track Count: 1
    Total play time: 03:00:00 [mins:secs:frames]
    Speaker config: 2 Channel
    Duration: 03:00:00 [mins:secs:frames]
    """;
    var identity = LocalDsdProcessor.ResolveLocalIdentity(scan, LocalDsdProcessor.ParseLayout(missingDiscText));
    Assert(identity.ChecksumArtist == "Talk Talk" && identity.ChecksumAlbum == "Spirit Of Eden",
        "The scanned checksum filename must resolve the missing SACD artist and album title.");
}

static async Task ScannerDescendsIntoSingleNestedAlbum(string root)
{
    var container = Path.Combine(root, "single-nested-container");
    var album = Path.Combine(container, "Random Access Memories [Limited Box Set Edition]");
    Directory.CreateDirectory(album);
    await File.WriteAllBytesAsync(Path.Combine(album, "1 Give Life Back to Music.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(album, "2 The Game of Love.flac"), [2]);
    await File.WriteAllBytesAsync(Path.Combine(album, "front.jpg"), [3]);

    var scan = await new AlbumScanner().ScanAsync(container);
    Assert(scan.AlbumRoot.Equals(album, StringComparison.OrdinalIgnoreCase) &&
           scan.AlbumName == "Random Access Memories [Limited Box Set Edition]" &&
           scan.Mode == WorkflowMode.ExistingTrackRepair && scan.TrackCount == 2 &&
           scan.Media.Where(item => item.Kind == "Existing FLAC").All(item => !item.RelativePath.Contains("Random Access Memories", StringComparison.OrdinalIgnoreCase)),
        "A source container with exactly one nested album must resolve directly to the child album instead of treating the container name as album identity.");
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
    var preflight = await new PreflightService().CheckAsync(result);
    Assert(!preflight.CanStart && preflight.Checks.Any(check => check.Name == "Verified write-back" && check.BlocksRun &&
        check.Detail.Contains("standalone FLAC tracks", StringComparison.OrdinalIgnoreCase)),
        "A mixed CUE/image plus existing-track folder must remain blocked as ambiguous.");
}

static async Task ScannerAcceptsVerifiedLegacySplitCue(string root)
{
    var folder = Path.Combine(root, "verified-legacy-split-cue");
    Directory.CreateDirectory(folder);
    await File.WriteAllBytesAsync(Path.Combine(folder, "01. Angel.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(folder, "02. Risingson.flac"), [2]);
    await File.WriteAllBytesAsync(Path.Combine(folder, "03.  Inertia Creeps.flac"), [3]);
    var cue = Path.Combine(folder, "album.cue");
    await File.WriteAllTextAsync(cue, """
    FILE "Range.wav" WAVE
      TRACK 01 AUDIO
        TITLE "Angel"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Risingson"
        INDEX 00 06:19:20
        INDEX 01 06:19:40
      TRACK 03 AUDIO
        TITLE " Inertia Creeps"
        INDEX 01 11:18:27
    """);

    var accepted = await new AlbumScanner().ScanAsync(folder);
    Assert(accepted.Mode == WorkflowMode.ExistingTrackRepair && accepted.TrackCount == 3 &&
           accepted.Errors.All(error => !error.Contains("missing source", StringComparison.OrdinalIgnoreCase)) &&
           accepted.Warnings.Any(warning => warning.Contains("historical provenance", StringComparison.OrdinalIgnoreCase)),
        "A complete sequential FLAC set whose normalized filename titles match a missing-image CUE must proceed to structural and metadata repair.");

    File.Move(Path.Combine(folder, "02. Risingson.flac"), Path.Combine(folder, "02. Different Song.flac"));
    var titleMismatch = await new AlbumScanner().ScanAsync(folder);
    Assert(titleMismatch.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "A split FLAC title mismatch must keep the missing CUE image as a blocking inventory error.");

    File.Move(Path.Combine(folder, "02. Different Song.flac"), Path.Combine(folder, "02. Risingson.flac"));
    await File.WriteAllBytesAsync(Path.Combine(folder, "04. Unexpected.flac"), [4]);
    var extraTrack = await new AlbumScanner().ScanAsync(folder);
    Assert(extraTrack.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "An extra FLAC outside the exact CUE track set must fail closed.");

    var perTrackFolder = Path.Combine(root, "verified-legacy-per-track-wav-cue");
    Directory.CreateDirectory(perTrackFolder);
    await File.WriteAllBytesAsync(Path.Combine(perTrackFolder, "01 - Angel.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(perTrackFolder, "02 - Risingson.flac"), [2]);
    await File.WriteAllBytesAsync(Path.Combine(perTrackFolder, "03 - Teardrop.flac"), [3]);
    var perTrackCueText = """
    FILE "01 - Angel.wav" WAVE
      TRACK 01 AUDIO
        TITLE "Angel"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Risingson"
        INDEX 00 06:19:20
    FILE "02 - Risingson.wav" WAVE
        INDEX 01 00:00:00
      TRACK 03 AUDIO
        TITLE "Teardrop"
        INDEX 00 04:57:70
    FILE "03 - Teardrop.wav" WAVE
        INDEX 01 00:00:00
    """;
    await File.WriteAllTextAsync(Path.Combine(perTrackFolder, "album.cue"), perTrackCueText);
    await File.WriteAllTextAsync(Path.Combine(perTrackFolder, "album-no-data-track.cue"), perTrackCueText + Environment.NewLine + """
      TRACK 04 MODEx/2xxx
        TITLE "Data Track"
        INDEX 00 10:00:00
    """);

    var perTrackAccepted = await new AlbumScanner().ScanAsync(perTrackFolder);
    Assert(perTrackAccepted.Mode == WorkflowMode.ExistingTrackRepair && perTrackAccepted.Errors.Count == 0 &&
           perTrackAccepted.Warnings.Any(warning => warning.Contains("historical provenance", StringComparison.OrdinalIgnoreCase)),
        $"Equivalent legacy CUE variants with one missing WAV per audio track must proceed when every source name, FLAC name, number, and title agrees. Mode={perTrackAccepted.Mode}; Errors={string.Join(" | ", perTrackAccepted.Errors)}; Warnings={string.Join(" | ", perTrackAccepted.Warnings)}");

    var mismatchedCue = perTrackCueText.Replace("02 - Risingson.wav", "02 - Wrong Source.wav", StringComparison.Ordinal);
    await File.WriteAllTextAsync(Path.Combine(perTrackFolder, "album-no-data-track.cue"), mismatchedCue);
    var sourceNameMismatch = await new AlbumScanner().ScanAsync(perTrackFolder);
    Assert(sourceNameMismatch.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "A missing per-track source filename that disagrees with the CUE title and split FLAC must remain blocked.");
}

static async Task ScannerSkipsCompleteTaggedMultiDiscTracksWithMissingHistoricalCueSources(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg) return;

    var album = Path.Combine(root, "complete-multidisc-missing-cue-sources");
    var discFolders = new[] { Path.Combine(album, "CD1"), Path.Combine(album, "CD2") };
    foreach (var folder in discFolders) Directory.CreateDirectory(folder);
    var coverPath = Path.Combine(album, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=maroon:s=96x96", "-frames:v", "1", "-update", "1", coverPath);
    var coverBytes = await File.ReadAllBytesAsync(coverPath);

    for (var disc = 1; disc <= 2; disc++)
    for (var track = 1; track <= 2; track++)
    {
        var path = Path.Combine(discFolders[disc - 1], $"{track:00} - Scene {disc}-{track}.flac");
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            $"sine=frequency={500 + disc * 100 + track * 20}:duration=0.08", "-c:a", "flac", path);
        using var file = TagLib.File.Create(path);
        file.Tag.Title = $"Scene {disc}-{track}";
        file.Tag.Album = "Complete Test Opera";
        file.Tag.Performers = ["Test Cast"];
        file.Tag.AlbumArtists = ["Test Cast"];
        file.Tag.Composers = ["Test Composer"];
        file.Tag.Track = (uint)track;
        file.Tag.TrackCount = 2;
        file.Tag.Disc = (uint)disc;
        file.Tag.DiscCount = 2;
        file.Tag.Year = 1960;
        file.Tag.Genres = ["Opera"];
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(coverBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Front cover"
            }
        ];
        file.Save();
    }
    await File.WriteAllTextAsync(Path.Combine(album, "disc-1.cue"), """
    FILE "missing-disc-1.flac" WAVE
      TRACK 01 AUDIO
        TITLE "Scene 1-1"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Scene 1-2"
        INDEX 01 03:00:00
    """);
    await File.WriteAllTextAsync(Path.Combine(album, "disc-2.cue"), """
    FILE "missing-disc-2.flac" WAVE
      TRACK 01 AUDIO
        TITLE "Scene 2-1"
        INDEX 01 00:00:00
      TRACK 02 AUDIO
        TITLE "Scene 2-2"
        INDEX 01 03:00:00
    """);

    var complete = await new AlbumScanner().ScanAsync(album);
    Assert(complete.Mode == WorkflowMode.Completed && !complete.RequiresProcessing && complete.Errors.Count == 0 &&
           complete.Warnings.Any(warning => warning.Contains("historical provenance", StringComparison.OrdinalIgnoreCase)),
        "A complete tagged multi-disc FLAC set with embedded artwork must be skipped even when preserved CUE sheets name missing historical images.");

    var incompletePath = Path.Combine(discFolders[1], "02 - Scene 2-2.flac");
    using (var file = TagLib.File.Create(incompletePath))
    {
        file.Tag.Pictures = [];
        file.Save();
    }
    var incomplete = await new AlbumScanner().ScanAsync(album);
    Assert(incomplete.Mode == WorkflowMode.ExistingTrackRepair &&
           incomplete.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "Missing artwork must prevent current-state completion and keep missing CUE sources blocked.");
}

static async Task ScannerSkipsCompleteTaggedDsdTracksAndAreas(string root)
{
    var dsfAlbum = Path.Combine(root, "complete-dsf-areas", "ERA - The Mass (2003) SACD [DSD] 2.0+5.0");
    foreach (var area in new[] { "Stereo", "Multichannel" })
    {
        var areaRoot = Path.Combine(dsfAlbum, area);
        Directory.CreateDirectory(areaRoot);
        for (var index = 1; index <= 2; index++)
            CreateTaggedDsfFixture(Path.Combine(areaRoot, $"{index:00} - Track {index}.dsf"),
                $"Track {index}", (uint)index, 2, (byte)(index + (area == "Stereo" ? 0x20 : 0x40)));
    }

    var dsf = await new AlbumScanner().ScanAsync(dsfAlbum);
    Assert(dsf.Mode == WorkflowMode.Completed && !dsf.RequiresProcessing && dsf.TrackCount == 4 &&
           dsf.Warnings.Any(warning => warning.Contains("no repair or external lookup", StringComparison.OrdinalIgnoreCase)),
        "Complete standalone DSF Stereo/Multichannel areas must be skipped before staging or external lookup.");

    var dffAlbum = Path.Combine(root, "complete-dff", "Tester - Complete DFF Album");
    Directory.CreateDirectory(dffAlbum);
    for (var index = 1; index <= 2; index++)
    {
        var path = Path.Combine(dffAlbum, $"{index:00} - Track {index}.dff");
        CreateDffFixture(path, (byte)(0x60 + index));
        await DffMetadata.SaveAsync(path, new(
            $"Track {index}", "Complete DFF Album", "Tester", "Tester",
            (uint)index, 2, 1, 1, 2020, "Rock", null, [0xFF, 0xD8, 0xFF, 0xD9]));
    }

    var dff = await new AlbumScanner().ScanAsync(dffAlbum);
    Assert(dff.Mode == WorkflowMode.Completed && !dff.RequiresProcessing && dff.TrackCount == 2,
        "Complete standalone DFF tracks must be skipped before staging or external lookup.");
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
    var scanner = new AlbumScanner();
    var rootProgress = new System.Collections.Concurrent.ConcurrentQueue<InventoryProgress>();
    var result = await scanner.ScanAsync(folder, new TestProgress<InventoryProgress>(rootProgress.Enqueue));
    Assert(result.Mode == WorkflowMode.MultipleAlbums, "An artist folder must be classified as a batch, not one album.");
    Assert(result.Warnings.Any(value => value.Contains("2 independent albums", StringComparison.OrdinalIgnoreCase)), "Multiple-album batch guidance is missing.");
    Assert(rootProgress.Any(update => update.Stage == "Classifying media") && rootProgress.Max(update => update.Percent) == 100,
        "Recursive inventory must report determinate file-level progress through completion.");
    var albumProgress = new System.Collections.Concurrent.ConcurrentQueue<InventoryProgress>();
    var albums = await scanner.ScanAlbumsAsync(result, new TestProgress<InventoryProgress>(albumProgress.Enqueue));
    Assert(albums.Count == 2, "The batch scanner must create one plan per independent album root.");
    Assert(AlbumScanner.InventoryWorkerLimit is >= 1 and <= 4 &&
           albumProgress.Any(update => update.Total == 2 && update.Completed == 2 && update.Percent == 100 &&
                                       update.Stage.Contains("workers", StringComparison.OrdinalIgnoreCase)),
        "Batch inventory must expose bounded concurrent album-scan progress.");
    Assert(albums.All(album => album.Mode == WorkflowMode.FlacCueSplit), "Every discovered FLAC+CUE album must be independently runnable.");
    Assert(albums.Select(album => album.AlbumRoot).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2, "Batch album roots must be disjoint.");
    Assert(albums.All(album => !album.AlbumName.Equals("Covers", StringComparison.OrdinalIgnoreCase)), "Artwork-only folders must not become blocked batch albums.");
}

static async Task ScannerRoutesTrackPerFileCuesAndScopesNestedAlbums(string root)
{
    var discAlbum = Path.Combine(root, "track-per-file-cue-album");
    var discOne = Path.Combine(discAlbum, "CD 1 - First works");
    var discTwo = Path.Combine(discAlbum, "CD 2 - Second works");
    Directory.CreateDirectory(discOne);
    Directory.CreateDirectory(discTwo);
    for (var disc = 1; disc <= 2; disc++)
    {
        var folder = disc == 1 ? discOne : discTwo;
        await File.WriteAllBytesAsync(Path.Combine(folder, "01 - First.flac"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(folder, "02 - Second.flac"), [2]);
        await File.WriteAllTextAsync(Path.Combine(folder, $"disc{disc}.cue"), """
        FILE "01 - First.flac" WAVE
          TRACK 01 AUDIO
            INDEX 01 00:00:00
        FILE "02 - Second.flac" WAVE
          TRACK 02 AUDIO
            INDEX 01 00:00:00
        """);
    }
    var stale = Path.Combine(discAlbum, ".album-fixer-commit-resume-old", "TRACKS");
    Directory.CreateDirectory(stale);
    await File.WriteAllBytesAsync(Path.Combine(stale, "01 - stale.dsf"), [3]);

    var discScan = await new AlbumScanner().ScanAsync(discAlbum);
    Assert(discScan.Mode == WorkflowMode.ExistingTrackRepair && discScan.AlbumRoot == discAlbum &&
           discScan.TrackCount == 4 && discScan.CueCount == 2 && discScan.ImageCount == 0 &&
           discScan.Media.All(item => !item.Path.Contains(".album-fixer-", StringComparison.OrdinalIgnoreCase)),
        "Suffixed CD folders must form one repair album, one-file-per-track CUEs must remain provenance, and stale internal recovery folders must be ignored.");

    var mixedRoot = Path.Combine(root, "album-with-unrelated-nested-album");
    var nested = Path.Combine(mixedRoot, "Unrelated Nested Album");
    Directory.CreateDirectory(nested);
    await File.WriteAllBytesAsync(Path.Combine(mixedRoot, "01 - Root.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(mixedRoot, "02 - Root.flac"), [2]);
    await File.WriteAllBytesAsync(Path.Combine(nested, "01 - Nested.flac"), [3]);
    await File.WriteAllBytesAsync(Path.Combine(nested, "02 - Nested.flac"), [4]);
    var scoped = await new AlbumScanner().ScanAsync(mixedRoot);
    Assert(scoped.Mode == WorkflowMode.ExistingTrackRepair && scoped.TrackCount == 2 &&
           scoped.Media.All(item => !item.Path.StartsWith(nested, StringComparison.OrdinalIgnoreCase)),
        "An album with its own root tracks must be scoped independently from an unrelated nested album instead of remaining blocked as MultipleAlbums.");
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

static async Task ScannerAdoptsOptionalOnlyVerifiedSacdCompletion(string root)
{
    var folder = Path.Combine(root, "adopt-optional-only-sacd");
    var stereo = Path.Combine(folder, "Stereo");
    Directory.CreateDirectory(stereo);
    var iso = Path.Combine(folder, "album.iso");
    await File.WriteAllBytesAsync(iso, [1, 2, 3, 4]);
    var first = Path.Combine(stereo, "01 - First.dsf");
    var second = Path.Combine(stereo, "02 - Second.dsf");
    CreateTaggedDsfFixture(first, "First", 1, 2, 0x33);
    CreateTaggedDsfFixture(second, "Second", 2, 2, 0x77);

    string Report(string missingField, string audioStatus)
    {
        var missing = System.Text.Json.JsonSerializer.Serialize(new[] { missingField });
        var required = MetadataFieldPolicy.IsOptional(missingField) ? "[]" : missing;
        return $$"""
        {
          "workflow_mode": "sacd_iso_extract",
          "source": { "file": "album.iso", "size": 4 },
          "areas": [{ "area": "stereo", "tracks": [
            { "track": 1, "title": "First", "file": "Stereo/01 - First.dsf" },
            { "track": 2, "title": "Second", "file": "Stereo/02 - Second.dsf" }
          ] }],
          "verification": {
            "status": "incomplete",
            "independent_extraction": "passed",
            "tag_payload_size_verification": "passed",
            "audio_and_tags": "{{audioStatus}}",
            "sources_deleted": false,
            "errors": [],
            "missing_metadata": {{missing}},
            "missing_required_metadata": {{required}}
          },
          "commit": {
            "status": "completed_incomplete",
            "destination_sizes_verified": true,
            "final_path_verification": "passed_with_incomplete_metadata_or_artwork",
            "files": [
              { "file": "Stereo/01 - First.dsf", "size": {{new FileInfo(first).Length}} },
              { "file": "Stereo/02 - Second.dsf", "size": {{new FileInfo(second).Length}} }
            ]
          },
          "deletion": { "status": "retained", "performed": false, "files": ["album.iso"] }
        }
        """;
    }

    var reportPath = Path.Combine(folder, "conversion-report.json");
    await File.WriteAllTextAsync(reportPath, Report("LABEL", "passed"));
    var adoptedPlan = PreviousOutputCleanupService.DiscoverCompleted(folder);
    var adopted = await new AlbumScanner().ScanAsync(folder);
    var adoptedSummary = await ReportReader.LoadAsync(reportPath);
    Assert(adoptedPlan is { RecoveredFromStaleFallback: true } &&
           PreviousOutputCleanupService.HasTerminalSuccessEvidence(folder) &&
           adopted.Mode == WorkflowMode.Completed && !adopted.RequiresProcessing && adopted.TrackCount == 0 &&
           adopted.Media.Count(item => item.Kind == "Previous Album Fixer output") == 2 &&
           adopted.Warnings.Any(warning => warning.Contains("optional", StringComparison.OrdinalIgnoreCase)) &&
           adoptedSummary.Status == "passed",
        "A size-matched, tag/artwork-verified SACD extraction with only optional metadata gaps must be adopted and presented as complete even when its ISO was retained.");

    await File.WriteAllTextAsync(reportPath, Report("CATALOGNUMBER", "passed"));
    var requiredMissing = await new AlbumScanner().ScanAsync(folder);
    Assert(requiredMissing.Mode == WorkflowMode.DsdExtraction && requiredMissing.Mode != WorkflowMode.ExistingTrackRepair,
        "A SACD extraction with required metadata missing must reuse its retained exact ISO instead of entering standalone-track repair.");

    await File.WriteAllTextAsync(reportPath, Report("LABEL", "failed"));
    var failedAudioProof = await new AlbumScanner().ScanAsync(folder);
    Assert(failedAudioProof.Mode == WorkflowMode.DsdExtraction && failedAudioProof.Mode != WorkflowMode.ExistingTrackRepair,
        "A failed prior SACD audio/tag verification must rerun from the retained exact ISO instead of adopting or repairing its outputs.");
}

static async Task ScannerRoutesStandaloneDsfAndReusableIncompleteSacd(string root)
{
    var standalone = Path.Combine(root, "Jazz", "Standalone Artist - Standalone DSD64");
    Directory.CreateDirectory(standalone);
    for (var index = 1; index <= 3; index++)
        await File.WriteAllBytesAsync(Path.Combine(standalone, $"{index:00} - Track {index}.dsf"), [(byte)index]);
    await File.WriteAllBytesAsync(Path.Combine(standalone, "front.jpg"), [0xFF, 0xD8, 0xFF, 0xD9]);
    var standaloneScan = await new AlbumScanner().ScanAsync(standalone);
    Assert(standaloneScan.Mode == WorkflowMode.ExistingTrackRepair && standaloneScan.TrackCount == 3 &&
           standaloneScan.Media.Count(item => item.Kind == "Existing DSF") == 3,
        "Multiple standalone DSF tracks must route to verified existing-track repair.");

    var mixed = Path.Combine(root, "mixed-flac-dsf-repair");
    Directory.CreateDirectory(mixed);
    await File.WriteAllBytesAsync(Path.Combine(mixed, "01.flac"), [1]);
    await File.WriteAllBytesAsync(Path.Combine(mixed, "02.dsf"), [2]);
    var mixedScan = await new AlbumScanner().ScanAsync(mixed);
    Assert(mixedScan.Mode == WorkflowMode.NeedsInspection,
        "Mixed standalone FLAC and DSF files must remain read-only instead of entering one repair transaction.");

    var reusable = Path.Combine(root, "Jazz", "Kazumi & The Gentle Thoughts - Mermaid Boulevard [SACD ISO] {DYCP-70131} 2011");
    var stereo = Path.Combine(reusable, "Stereo");
    Directory.CreateDirectory(stereo);
    var iso = Path.Combine(reusable, "album.iso");
    var priorDsf = Path.Combine(stereo, "01 - Mermaid Boulevard.dsf");
    await File.WriteAllBytesAsync(iso, [1, 2, 3, 4]);
    await File.WriteAllBytesAsync(priorDsf, [5, 6, 7]);
    await File.WriteAllTextAsync(Path.Combine(reusable, "conversion-report.json"), $$"""
    {
      "workflow_mode": "sacd_iso_extract",
      "source": { "file": "album.iso", "size": 4 },
      "areas": [{ "area": "stereo", "tracks": [{ "track": 1, "title": "Mermaid Boulevard", "file": "Stereo/01 - Mermaid Boulevard.dsf" }] }],
      "verification": { "status": "incomplete", "missing_required_metadata": ["CATALOGNUMBER"] },
      "commit": { "status": "completed_incomplete", "files": [{ "file": "Stereo/01 - Mermaid Boulevard.dsf", "size": {{new FileInfo(priorDsf).Length}} }] }
    }
    """);
    var reusableScan = await new AlbumScanner().ScanAsync(reusable);
    Assert(reusableScan.Mode == WorkflowMode.DsdExtraction && reusableScan.ImageCount == 1 && reusableScan.TrackCount == 0 &&
           reusableScan.Media.Count(item => item.Kind == "Previous Album Fixer output") == 1,
        "An incomplete report-proven SACD extraction must yield to its still-present exact ISO for a safe rerun.");

    var layout = new LocalDsdProcessor.SacdLayout("Mermaid Boulevard", "Kazumi & The Gentle Thoughts", null, null, []);
    var cataloged = LocalDsdProcessor.ApplyFolderCatalog(layout,
        "Kazumi & The Gentle Thoughts - Mermaid Boulevard [SACD ISO] {DYCP-70131} 2011");
    Assert(cataloged.CatalogNumber == "DYCP-70131" &&
           cataloged.IdentitySources!.Contains("catalog number in album folder"),
        "A braced catalog token in the album folder must supply missing SACD catalog metadata.");
    var incorrectExternal = new ExternalAlbumIdentity("Unrelated Album", "Unrelated Artist", "DYCP-70131", "2011",
        "https://musicbrainz.org/release/incorrect", []);
    Assert(LocalDsdProcessor.ReconcileCatalogIdentity(cataloged, incorrectExternal) is null,
        "An external exact-catalog candidate that contradicts complete SACD disc text must be rejected when the catalog came from the folder.");

    var ymoLayout = new LocalDsdProcessor.SacdLayout(
        "UC YMO [Ultimate Collection of Yellow Magic Orchestra]", "YMO", "MHGL1", "2003-06-10", []);
    var ymoCatalogIdentity = new ExternalAlbumIdentity(
        "UC YMO: Ultimate Collection of Yellow Magic Orchestra", "Yellow Magic Orchestra", "MHGL-1", "2003-08-06",
        "https://musicbrainz.org/release/9be3a543-59d9-33e5-8469-057a90d10cc5", []);
    Assert(LocalDsdProcessor.ReconcileCatalogIdentity(ymoLayout, ymoCatalogIdentity) == ymoCatalogIdentity,
        "An exact embedded SACD catalog and equivalent album title must accept the disc-text artist acronym YMO for Yellow Magic Orchestra.");

    var ymoChecksumIdentity = new LocalDsdProcessor.SacdLocalIdentity(
        "Yellow Magic Orchestra", "UC YMO (Disc 1)", null, null);
    var identifiedYmo = LocalDsdProcessor.ApplyAlbumIdentity(ymoLayout, ymoChecksumIdentity, ymoCatalogIdentity);
    Assert(identifiedYmo.AlbumTitle == ymoLayout.AlbumTitle && identifiedYmo.AlbumArtist == "YMO" &&
           identifiedYmo.CatalogNumber == "MHGL-1",
        "A checksum filename may use a shortened disc-qualified title only when its stem agrees with both the exact catalog title and complete SACD disc title.");

    var unrelatedChecksumIdentity = ymoChecksumIdentity with { ChecksumAlbum = "Unrelated Album (Disc 1)" };
    Exception? unrelatedChecksumConflict = null;
    try { LocalDsdProcessor.ApplyAlbumIdentity(ymoLayout, unrelatedChecksumIdentity, ymoCatalogIdentity); }
    catch (Exception error) { unrelatedChecksumConflict = error; }
    Assert(unrelatedChecksumConflict is InvalidDataException,
        "A disc-qualified checksum filename with an unrelated album stem must remain a blocking identity conflict.");

    var nonInitialismIdentity = ymoCatalogIdentity with { Artist = "Young Marble Giants" };
    Exception? nonInitialismConflict = null;
    try { LocalDsdProcessor.ReconcileCatalogIdentity(ymoLayout, nonInitialismIdentity); }
    catch (Exception error) { nonInitialismConflict = error; }
    Assert(nonInitialismConflict is InvalidDataException,
        "A same-length artist name that does not expand the embedded acronym must remain a strict catalog/disc-text conflict.");

    Exception? discCatalogConflict = null;
    try
    {
        LocalDsdProcessor.ReconcileCatalogIdentity(layout with { CatalogNumber = "DYCP-70131" }, incorrectExternal);
    }
    catch (Exception error) { discCatalogConflict = error; }
    Assert(discCatalogConflict is InvalidDataException,
        "A catalog embedded in SACD disc data must retain strict conflict handling when external identity disagrees.");
}

static async Task ArchivedSacdArtifactsAreTransactionallyReplaceable(string root)
{
    var folder = Path.Combine(root, "archived-sacd-artifacts");
    Directory.CreateDirectory(folder);
    var artifacts = new[]
    {
        "sacd_extract-layout.txt",
        "sacd_extract-stereo.log",
        "sacd_extract-stereo-independent.log"
    };
    foreach (var artifact in artifacts)
        await File.WriteAllTextAsync(Path.Combine(folder, artifact), $"report-proven {artifact}");
    var files = string.Join(",", artifacts.Select(artifact =>
        $$"""{"file":"{{artifact}}","size":{{new FileInfo(Path.Combine(folder, artifact)).Length}}}"""));
    var artifactValues = string.Join(",", artifacts.Select(artifact => $"\"{artifact}\""));
    await File.WriteAllTextAsync(Path.Combine(folder, "conversion-report.previous-20260815-000000-test.json"), $$"""
    {
      "workflow_mode": "sacd_iso_extract",
      "artifacts": [{{artifactValues}}],
      "verification": { "status": "incomplete" },
      "commit": { "status": "completed_incomplete", "files": [{{files}}] }
    }
    """);
    await File.WriteAllTextAsync(Path.Combine(folder, "conversion-report.json"), """
    {
      "workflow_mode": "sacd_iso_extract",
      "verification": { "status": "failed" },
      "pipeline": { "status": "failed", "stopped_phase": "CopyingBack" }
    }
    """);

    var plan = PreviousOutputCleanupService.DiscoverArchivedDsdArtifacts(folder);
    Assert(plan is not null && plan.Files.Select(file => file.RelativePath)
               .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
               .SequenceEqual(artifacts.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase),
        "An archived incomplete SACD report must prove its exact existing layout/log artifacts for transactional replacement after a later fallback failure.");
    PreviousOutputCleanupService.VerifyDirectFileSizes(plan);

    await File.AppendAllTextAsync(Path.Combine(folder, artifacts[0]), "changed");
    Assert(PreviousOutputCleanupService.DiscoverArchivedDsdArtifacts(folder) is null,
        "An archived SACD report must not authorize replacement after an existing provenance artifact changes size.");
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

static async Task ScannerRecoversVerifiedMissingSourceFallbackCompletion(string root)
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

    var recovered = await new AlbumScanner().ScanAsync(folder);
    Assert(recovered.Mode == WorkflowMode.Completed && !recovered.RequiresProcessing && recovered.Errors.Count == 0 &&
           recovered.Media.Count(item => item.Kind == "Previous Album Fixer output") == 2 &&
           recovered.Warnings.Any(warning => warning.Contains("PCM equivalence could not be recomputed", StringComparison.OrdinalIgnoreCase)),
        "A missing source image must be recovered when the exact CUE-derived track set passes quick FLAC, tag, and artwork verification.");

    File.Copy(Path.Combine(folder, "02 - Track 2.flac"), Path.Combine(folder, "03 - Unexpected.flac"));
    var unsafeRecovery = await new AlbumScanner().ScanAsync(folder);
    Assert(unsafeRecovery.Mode != WorkflowMode.Completed && unsafeRecovery.Errors.Any(error => error.Contains("missing source", StringComparison.OrdinalIgnoreCase)),
        "Recovery must fail closed when an unexpected or incomplete track set is present.");

    File.Delete(Path.Combine(folder, "03 - Unexpected.flac"));
    await RunToolAsync(ffmpeg, "-y", "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=700:duration=0.1",
        "-c:a", "flac", "-metadata", "TITLE=Track 2", "-metadata", "ALBUM=Recovered Album", "-metadata", "ARTIST=Tester",
        "-metadata", "ALBUMARTIST=Tester", "-metadata", "TRACKNUMBER=2/2", "-metadata", "DISCNUMBER=1/1",
        "-metadata", "DATE=2026", "-metadata", "GENRE=Rock", Path.Combine(folder, "02 - Track 2.flac"));
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
    var missingArtwork = await new AlbumScanner().ScanAsync(folder);
    Assert(missingArtwork.Mode != WorkflowMode.Completed,
        "Missing-source recovery must fail closed when a track does not contain verified embedded artwork.");
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

static void CreateDffFixture(string path, byte sample)
{
    using var chunks = new MemoryStream();
    WriteDffChunk(chunks, "FVER", [0x01, 0x05, 0x00, 0x00]);

    using var properties = new MemoryStream();
    properties.Write("SND "u8);
    var sampleRate = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(sampleRate, 2_822_400);
    WriteDffChunk(properties, "FS  ", sampleRate);
    var channels = new byte[10];
    BinaryPrimitives.WriteUInt16BigEndian(channels, 2);
    "SLFT"u8.CopyTo(channels.AsSpan(2, 4));
    "SRGT"u8.CopyTo(channels.AsSpan(6, 4));
    WriteDffChunk(properties, "CHNL", channels);
    var compression = new byte[20];
    "DSD "u8.CopyTo(compression);
    compression[4] = 14;
    System.Text.Encoding.ASCII.GetBytes("not compressed").CopyTo(compression, 5);
    WriteDffChunk(properties, "CMPR", compression);
    WriteDffChunk(properties, "LSCO", [0x00, 0x00]);
    WriteDffChunk(chunks, "PROP", properties.ToArray());
    WriteDffChunk(chunks, "DSD ", Enumerable.Repeat(sample, 128 * 1024).ToArray());

    using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    output.Write("FRM8"u8);
    var size = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(size, checked((ulong)chunks.Length + 4));
    output.Write(size);
    output.Write("DSD "u8);
    chunks.Position = 0;
    chunks.CopyTo(output);
}

static void WriteDffChunk(Stream output, string id, byte[] payload)
{
    output.Write(System.Text.Encoding.ASCII.GetBytes(id));
    var size = new byte[8];
    BinaryPrimitives.WriteUInt64BigEndian(size, checked((ulong)payload.Length));
    output.Write(size);
    output.Write(payload);
    if ((payload.Length & 1) != 0) output.WriteByte(0);
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

static async Task LocalClassicalCueInfersComposer(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(seed);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "Classical", "Chopin - Piano Concertos - Fliter");
    Directory.CreateDirectory(album);
    var source = Path.Combine(album, "Chopin - Piano Concertos.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=440:duration=0.25", "-c:a", "flac", source);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=blue:s=700x700", "-frames:v", "1", "-update", "1", Path.Combine(album, "Chopin - sleeve.png"));
    await File.WriteAllTextAsync(Path.Combine(album, "Chopin - Piano Concertos.cue"), """
    REM GENRE Classical
    REM DATE 2014
    PERFORMER "Ingrid Fliter, Scottish Chamber Orchestra, Jun Markl"
    TITLE "Chopin - Piano Concertos"
    FILE "Chopin - Piano Concertos.flac" WAVE
      TRACK 01 AUDIO
        TITLE "Piano Concerto No. 1 in E minor, Op. 11 - I. Allegro maestoso"
        INDEX 01 00:00:00
    """);
    var scan = await new AlbumScanner().ScanAsync(album);
    var job = Path.Combine(root, "classical-cue-job");
    var stagedAlbum = Path.Combine(job, "album");
    Directory.CreateDirectory(stagedAlbum);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [],
        SourceAlbumRoot: album, SourceCacheUsed: false);
    var result = await new LocalFlacProcessor().ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(!result.Metadata.RequiresResearch && result.Tracks == 1,
        "A classical FLAC+CUE release whose exact album and folder name the composer must not remain blocked on COMPOSER.");
    using var track = TagLib.File.Create(Path.Combine(stagedAlbum,
        "01 - Piano Concerto No. 1 in E minor, Op. 11 - I. Allegro maestoso.flac"));
    Assert(track.Tag.Composers.SequenceEqual(["Frédéric Chopin"]) && track.Tag.Pictures.Length == 1,
        "The inferred canonical Chopin composer and locally named sleeve artwork must be embedded in the split FLAC.");
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
           reportCover.GetProperty("source").GetString()!.Contains("coverartarchive.org", StringComparison.OrdinalIgnoreCase) &&
           EmbeddedCoverSha256(Path.Combine(stagedAlbum, "01 - Track One.flac")) == reportCover.GetProperty("sha256").GetString(),
        "The report must identify local code and the downloaded in-memory cover used for embedding.");
}

static async Task ExternalArtworkFallbackSupportsSacdAndPreservesLocalPriority(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var preflight = await new PreflightService().CheckAsync(seed);
    if (preflight.Tools["ffmpeg"] is not { } ffmpeg || preflight.Tools["ffprobe"] is not { } ffprobe) return;

    var downloadedFixture = Path.Combine(root, "sacd-external-cover-fixture.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=orange:s=1000x800", "-frames:v", "1", "-update", "1", downloadedFixture);
    var downloadedBytes = await File.ReadAllBytesAsync(downloadedFixture);
    var coverRequests = 0;
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        if (request.RequestUri?.AbsoluteUri.Contains("coverartarchive.org/release/release-sacd-cover", StringComparison.Ordinal) == true)
        {
            coverRequests++;
            return StubHttpHandler.Bytes(downloadedBytes, "image/jpeg");
        }
        throw new InvalidOperationException($"Unexpected SACD cover request: {request.RequestUri}");
    }));
    var external = new ExternalMetadataService(client, requestTimeout: TimeSpan.FromSeconds(1));
    var artwork = new InMemoryArtworkService();
    var albumWithoutArt = Path.Combine(root, "sacd-external-cover");
    Directory.CreateDirectory(albumWithoutArt);
    var fallback = await artwork.PrepareLocalThenExternalAsync(
        albumWithoutArt, ffmpeg, ffprobe, ArtworkSelectionMode.Dsd, external, "release-sacd-cover");

    Assert(fallback.Artwork is not null && fallback.Artwork.Source.Contains("coverartarchive.org", StringComparison.OrdinalIgnoreCase) &&
           fallback.Artwork.Width == 600 && fallback.Artwork.Height == 600 && fallback.Artwork.JpegBytes.Length <= 1024 * 1024 &&
           coverRequests == 1 && !Directory.EnumerateFiles(albumWithoutArt, "*", SearchOption.AllDirectories).Any(),
        "SACD must use an exact external cover when local artwork is absent, normalize it in memory, and create no sidecar image.");

    var albumWithArt = Path.Combine(root, "sacd-local-cover-priority");
    Directory.CreateDirectory(albumWithArt);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=green:s=400x400", "-frames:v", "1", "-update", "1", Path.Combine(albumWithArt, "cover.jpg"));
    var local = await artwork.PrepareLocalThenExternalAsync(
        albumWithArt, ffmpeg, ffprobe, ArtworkSelectionMode.Dsd, external, "release-sacd-cover");
    Assert(local.Artwork is not null && local.Artwork.Source.Contains("cover.jpg", StringComparison.OrdinalIgnoreCase) && coverRequests == 1,
        "A usable local SACD cover must retain priority and suppress external cover download.");

    var albumWithSleeve = Path.Combine(root, "flac-sleeve-cover");
    Directory.CreateDirectory(albumWithSleeve);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=700x700", "-frames:v", "1", "-update", "1", Path.Combine(albumWithSleeve, "album sleeve.png"));
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=gray:s=700x700", "-frames:v", "1", "-update", "1", Path.Combine(albumWithSleeve, "album inlay.png"));
    var sleeve = await artwork.PrepareLocalAsync(albumWithSleeve, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(sleeve.Artwork is not null && sleeve.Artwork.Source.Contains("sleeve.png", StringComparison.OrdinalIgnoreCase),
        "A square sleeve scan must be accepted as front artwork ahead of an inlay scan.");

    var albumWithCatalogPair = Path.Combine(root, "flac-catalog-front-inlay-pair");
    Directory.CreateDirectory(albumWithCatalogPair);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=700x700", "-frames:v", "1", "-update", "1", Path.Combine(albumWithCatalogPair, "CKD 455.jpg"));
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=gray:s=1400x700", "-frames:v", "1", "-update", "1", Path.Combine(albumWithCatalogPair, "CKD 455-inlay.jpg"));
    var catalogPair = await artwork.PrepareLocalAsync(albumWithCatalogPair, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(catalogPair.Artwork is not null && catalogPair.Artwork.Source.Contains("CKD 455.jpg", StringComparison.OrdinalIgnoreCase),
        "A catalog-named scan must be accepted as the front when its same-base companion is explicitly named as an inlay.");

    var albumWithSoleImage = Path.Combine(root, "flac-sole-arbitrary-cover");
    Directory.CreateDirectory(albumWithSoleImage);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=yellow:s=619x625", "-frames:v", "1", "-update", "1", Path.Combine(albumWithSoleImage, "CarminaBuranaJochum.jpg"));
    var soleImage = await artwork.PrepareLocalAsync(albumWithSoleImage, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(soleImage.Artwork is not null && soleImage.Artwork.Source.Contains("CarminaBuranaJochum.jpg", StringComparison.OrdinalIgnoreCase),
        "A sole, near-square, non-negative image must be accepted as unambiguous front artwork.");

    var numberedScans = Path.Combine(root, "flac-subject-delta-scans");
    Directory.CreateDirectory(numberedScans);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=red:s=640x620", "-frames:v", "1", "-update", "1", Path.Combine(numberedScans, "SubjectDelta 01.jpg"));
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=black:s=640x620", "-frames:v", "1", "-update", "1", Path.Combine(numberedScans, "SubjectDelta 02.jpg"));
    var numbered = await artwork.PrepareLocalAsync(numberedScans, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(numbered.Artwork is not null && numbered.Artwork.Source.Contains("SubjectDelta 01.jpg", StringComparison.OrdinalIgnoreCase),
        "A SubjectDelta scan set must use its explicitly numbered first scan instead of rejecting the multi-image folder.");

    var pdfAlbum = Path.Combine(root, "flac-single-image-cover-pdf");
    Directory.CreateDirectory(pdfAlbum);
    var pdfHeader = System.Text.Encoding.ASCII.GetBytes($$"""
        %PDF-1.4
        1 0 obj <</Count 1/Kids[2 0 R]/Type/Pages>> endobj
        2 0 obj <</Type/Page/Parent 1 0 R/Resources<</XObject<</Im0 3 0 R>>>>>> endobj
        3 0 obj <</Type/XObject/Subtype/Image/Filter/DCTDecode/Width 1000/Height 800/Length {{downloadedBytes.Length}}>>stream
        """);
    var pdfFooter = System.Text.Encoding.ASCII.GetBytes("\nendstream\nendobj\n%%EOF\n");
    await using (var pdf = new FileStream(Path.Combine(pdfAlbum, "Album artwork.pdf"), FileMode.CreateNew, FileAccess.Write))
    {
        await pdf.WriteAsync(pdfHeader);
        await pdf.WriteAsync(downloadedBytes);
        await pdf.WriteAsync(pdfFooter);
    }
    var fromPdf = await artwork.PrepareLocalAsync(pdfAlbum, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(fromPdf.Artwork is not null && fromPdf.Artwork.Source.Contains("one-page cover PDF", StringComparison.OrdinalIgnoreCase),
        "A one-page PDF containing exactly one DCT JPEG image must supply cover artwork without a raster sidecar.");

    var volumeParent = Path.Combine(root, "flac-volume-parent");
    var volumeChild = Path.Combine(volumeParent, "Volume 1");
    Directory.CreateDirectory(volumeChild);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=600x600", "-frames:v", "1", "-update", "1", Path.Combine(volumeParent, "album sleeve.jpg"));
    var parentRoot = LocalTrackRepairProcessor.ParentVolumeArtworkRoot(volumeChild);
    var parentArtwork = parentRoot is null
        ? null
        : await artwork.PrepareLocalAsync(parentRoot, ffmpeg, ffprobe, ArtworkSelectionMode.Flac);
    Assert(parentArtwork?.Artwork is not null && parentArtwork.Artwork.Source.Contains("sleeve.jpg", StringComparison.OrdinalIgnoreCase) &&
           LocalTrackRepairProcessor.ParentVolumeArtworkRoot(Path.Combine(volumeParent, "Ordinary Album")) is null,
        "Only explicitly numbered volume/disc subfolders may inherit a dedicated sleeve from their parent album folder.");

    var sacdAreaParent = Path.Combine(root, "sacd-area-parent");
    var sacdAreaArtwork = Path.Combine(sacdAreaParent, "Artwork");
    var stereoArea = Path.Combine(sacdAreaParent, "Album Name");
    var multichannelArea = Path.Combine(sacdAreaParent, "Album Name Multi-ch");
    Directory.CreateDirectory(sacdAreaArtwork);
    Directory.CreateDirectory(stereoArea);
    Directory.CreateDirectory(multichannelArea);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=600x600", "-frames:v", "1", "-update", "1", Path.Combine(sacdAreaArtwork, "01..BookletF.jpg"));
    Assert(LocalTrackRepairProcessor.SiblingSacdAreaRoot(multichannelArea) == stereoArea &&
           LocalTrackRepairProcessor.ParentVolumeArtworkRoot(multichannelArea) == sacdAreaParent &&
           LocalTrackRepairProcessor.ParentVolumeArtworkRoot(Path.Combine(sacdAreaParent, "Unrelated Child")) is null,
        "A named SACD stereo/multichannel area may inherit its exact-base sibling and a dedicated parent Artwork folder; ordinary child albums remain isolated.");

    var albumWithEmbeddedArt = Path.Combine(root, "sacd-embedded-cover");
    Directory.CreateDirectory(albumWithEmbeddedArt);
    var retainedAudio = Path.Combine(albumWithEmbeddedArt, "retained.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=880:duration=0.25", "-c:a", "flac", retainedAudio);
    using (var file = TagLib.File.Create(retainedAudio))
    {
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(downloadedBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Front cover"
            }
        ];
        file.Save();
    }
    var embedded = await artwork.PrepareLocalThenExternalAsync(
        albumWithEmbeddedArt, ffmpeg, ffprobe, ArtworkSelectionMode.Dsd, external, "release-sacd-cover");
    Assert(embedded.Artwork is not null && embedded.Artwork.Source.Contains("embedded artwork", StringComparison.OrdinalIgnoreCase) && coverRequests == 1,
        "SACD must reuse embedded art from retained local audio before requesting an external cover.");
}

static async Task ExistingTracksRepairUsesPrioritizedEvidenceAndTransactionalCommit(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "Rock", "Priority Artist - Priority Album");
    Directory.CreateDirectory(album);
    var first = Path.Combine(album, "1-Wrong Filename.flac");
    var second = Path.Combine(album, "2-Canonical Second.flac");
    var embeddedFixture = Path.Combine(root, "repair-embedded.jpg");
    var folderCover = Path.Combine(album, "folder.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=700:duration=0.25", "-c:a", "flac", first);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=900:duration=0.25", "-c:a", "flac", second);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=96x96", "-frames:v", "1", "-update", "1", embeddedFixture);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=96x96", "-frames:v", "1", "-update", "1", folderCover);
    var embeddedBytes = await File.ReadAllBytesAsync(embeddedFixture);
    using (var file = TagLib.File.Create(first))
    {
        file.Tag.Title = "Trusted Existing Title";
        file.Tag.Album = "Priority Album";
        file.Tag.Performers = ["The Priority Artist"];
        file.Tag.AlbumArtists = ["The Priority Artist"];
        file.Tag.Track = 1;
        file.Tag.Disc = 1;
        file.Tag.Year = 1999;
        file.Tag.Genres = ["Rock"];
        file.Tag.Pictures =
        [
            new TagLib.Picture(new TagLib.ByteVector(embeddedBytes))
            {
                Type = TagLib.PictureType.FrontCover,
                MimeType = "image/jpeg",
                Description = "Trusted embedded front cover"
            }
        ];
        file.Save();
    }
    using (var file = TagLib.File.Create(second))
    {
        file.Tag.Album = "Priority Album";
        file.Tag.Performers = ["The Priority Artist"];
        file.Tag.AlbumArtists = ["The Priority Artist"];
        file.Tag.Track = 2;
        file.Tag.Disc = 1;
        file.Save();
    }

    var beforeFirstPayload = await FlacAudioPayload.Sha256Async(first);
    var beforeSecondPayload = await FlacAudioPayload.Sha256Async(second);
    var originalFolderCover = FileSha256(folderCover);
    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.CueCount == 0 && scan.ImageCount == 0 && scan.TrackCount == 2,
        "Standalone FLAC tracks without a CUE must classify as existing-track repair.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart && preflight.Checks.Any(check => check.Name == "Verified write-back" &&
        check.State == CheckState.Passed && check.Detail.Contains("audio payload", StringComparison.OrdinalIgnoreCase)),
        "Standalone existing FLAC tracks must pass verified write-back preflight.");

    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[{"id":"release-priority","score":100,"title":"Priority Album","artist-credit":[{"name":"Priority Artist"}],"release-group":{"id":"group-priority"},"date":"2000-01-01","track-count":2,"media":[{"format":"CD","track-count":2}],"label-info":[]}]}""");
        if (uri.Contains("/ws/2/release-group/group-priority", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"first-release-date":"2000-01-01","genres":[{"name":"rock","count":10}],"tags":[],"relations":[]}""");
        if (uri.Contains("/ws/2/release/release-priority", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"media":[{"tracks":[{"title":"Trusted Existing Title"},{"title":"Canonical Second"}]}]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"artistName":"Priority Artist","collectionName":"Priority Album","primaryGenreName":"Rock","releaseDate":"2000-01-01T00:00:00Z","trackCount":2,"collectionViewUrl":"https://music.apple.com/album/priority"}]}""");
        throw new InvalidOperationException($"Unexpected existing-track metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(local.Tracks == 2 && !local.Metadata.RequiresResearch,
        "Existing tags, filename evidence, external metadata, and embedded artwork should complete the repair fixture.");
    using (var untouched = TagLib.File.Create(second))
        Assert(string.IsNullOrWhiteSpace(untouched.Tag.Title), "Local repair must not edit original tracks before transactional commit.");
    using (var repairedFirst = TagLib.File.Create(Path.Combine(staged.AlbumRoot, "1-Wrong Filename.flac")))
        Assert(repairedFirst.Tag.Title == "Trusted Existing Title" && repairedFirst.Tag.Year == 1999,
            "Existing nonempty tags must outrank contradictory filename and external fallback evidence.");
    using (var repairedSecond = TagLib.File.Create(Path.Combine(staged.AlbumRoot, "2-Canonical Second.flac")))
        Assert(repairedSecond.Tag.Title == "Canonical Second" && repairedSecond.Tag.Year == 1999 && repairedSecond.Tag.FirstGenre == "Rock",
            "Missing tags must be filled from the exact external tracklist and album-level existing evidence.");

    using (var localReport = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(local.ReportPath)))
    {
        var reportRoot = localReport.RootElement;
        var tracks = reportRoot.GetProperty("discs")[0].GetProperty("tracks");
        Assert(reportRoot.GetProperty("workflow_mode").GetString() == "existing_track_repair" &&
               reportRoot.GetProperty("cover").GetProperty("source").GetString()!.Contains("existing embedded artwork", StringComparison.OrdinalIgnoreCase) &&
               tracks[0].GetProperty("title_source").GetString() == "existing_tag" &&
               tracks[1].GetProperty("title_source").GetString() == "external_tracklist",
            "The repair report must prove existing-tag/art priority and external filename-matched fallback usage.");
    }

    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);
    Assert(committed.Tracks == 2 && !committed.SourcesDeleted && File.Exists(first) && File.Exists(second),
        "Existing-track repair must replace tracks transactionally without applying source deletion.");
    Assert(await FlacAudioPayload.Sha256Async(first) == beforeFirstPayload &&
           await FlacAudioPayload.Sha256Async(second) == beforeSecondPayload,
        "Final repaired FLAC files must preserve the exact compressed audio-frame payloads.");
    Assert(FileSha256(folderCover) == originalFolderCover,
        "A lower-priority folder cover must remain byte-identical while embedded track artwork is preferred.");
    using (var repaired = TagLib.File.Create(second))
        Assert(repaired.Tag.Title == "Canonical Second" && repaired.Tag.Pictures.Length == 1,
            "The final repaired track must contain resolved tags and the prioritized embedded artwork.");
    using (var finalReport = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(committed.ReportPath)))
        Assert(finalReport.RootElement.GetProperty("verification").GetProperty("audio_payload_equivalence").GetString() == "passed" &&
               finalReport.RootElement.GetProperty("deletion").GetProperty("policy").GetString() == "transactional_existing_track_replacement_without_source_deletion",
            "The final report must record audio-payload equality and non-deletion transactional replacement.");
    var completed = await new AlbumScanner().ScanAsync(album);
    Assert(completed.Mode == WorkflowMode.Completed && !completed.RequiresProcessing,
        "A successfully committed existing-track repair must inventory as already completed.");
}

static async Task ExistingTrackCorruptionNamesTheExactFile(string root)
{
    var album = Path.Combine(root, "corrupt-existing-flac");
    Directory.CreateDirectory(album);
    await File.WriteAllBytesAsync(Path.Combine(album, "01 - Corrupt Source.flac"), new byte[4096]);
    await File.WriteAllBytesAsync(Path.Combine(album, "02 - Also Corrupt.flac"), new byte[4096]);
    var scan = await new AlbumScanner().ScanAsync(album);
    var preflight = await new PreflightService().CheckAsync(scan);
    if (!preflight.CanStart) return;
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    Exception? failure = null;
    try
    {
        await new LocalTrackRepairProcessor(new ExternalMetadataService()).ProcessAsync(
            scan, staged, new Progress<ProgressSnapshot>());
    }
    catch (Exception error) { failure = error; }
    Assert(failure is InvalidDataException &&
           failure.Message.Contains("01 - Corrupt Source.flac", StringComparison.OrdinalIgnoreCase) &&
           failure.Message.Contains("corrupt", StringComparison.OrdinalIgnoreCase),
        "A malformed existing FLAC must stop safely and identify the exact corrupt source filename.");
}

static async Task ExistingCompilationUsesDiscogsTrackArtistsAndPrimaryCover(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "Jazz", "Various Artists - Discogs Compilation");
    Directory.CreateDirectory(album);
    var paths = new[] { Path.Combine(album, "01 - First.flac"), Path.Combine(album, "02 - Second.flac") };
    for (var index = 0; index < paths.Length; index++)
    {
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            $"sine=frequency={700 + index * 100}:duration=0.15", "-c:a", "flac", paths[index]);
        using var file = TagLib.File.Create(paths[index]);
        file.Tag.Title = index == 0 ? "First" : "Second";
        file.Tag.Album = "Discogs Compilation";
        file.Tag.Performers = ["Various Artists"];
        file.Tag.AlbumArtists = ["Various Artists"];
        file.Tag.Track = (uint)(index + 1);
        file.Tag.TrackCount = 2;
        file.Tag.Disc = 1;
        file.Tag.DiscCount = 1;
        file.Tag.Year = 2020;
        file.Tag.Genres = ["Jazz"];
        file.Save();
    }
    var coverFixture = Path.Combine(root, "discogs-primary-cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=96x96", "-frames:v", "1", "-update", "1", coverFixture);
    var coverBytes = await File.ReadAllBytesAsync(coverFixture);

    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("api.discogs.com/database/search", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"id":4242,"title":"Various - Discogs Compilation","year":"2020","format":["Compilation"]}]}""");
        if (uri.Contains("api.discogs.com/releases/4242", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
            {
              "title":"Discogs Compilation",
              "artists":[{"name":"Various"}],
              "genres":["Jazz"],
              "images":[{"type":"primary","uri":"https://i.discogs.com/primary-cover.jpeg"}],
              "tracklist":[
                {"position":"1","type_":"track","title":"First","artists":[{"name":"First Artist"}]},
                {"position":"2","type_":"track","title":"Second","artists":[{"name":"Second Artist (2)"}]}
              ]
            }
            """);
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        if (request.RequestUri?.Host.Equals("i.discogs.com", StringComparison.OrdinalIgnoreCase) == true)
            return StubHttpHandler.Bytes(coverBytes, "image/jpeg");
        throw new InvalidOperationException($"Unexpected compilation repair request: {uri}");
    }));
    var external = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero,
        discogsMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var scan = await new AlbumScanner().ScanAsync(album);
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "The Discogs compilation repair fixture must pass preflight.");
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    using (var original = TagLib.File.Create(paths[0]))
        Assert(original.Tag.FirstPerformer == "Various Artists" && original.Tag.Pictures.Length == 0,
            "The local repair transaction must not modify the original compilation track before commit.");
    using (var first = TagLib.File.Create(Path.Combine(staged.AlbumRoot, "01 - First.flac")))
        Assert(first.Tag.FirstPerformer == "First Artist" && first.Tag.FirstAlbumArtist == "Various Artists" && first.Tag.Pictures.Length == 1,
            "A verified Discogs track artist must replace the Various Artists track placeholder while preserving the compilation album artist and embedding the primary cover.");
    using (var second = TagLib.File.Create(Path.Combine(staged.AlbumRoot, "02 - Second.flac")))
        Assert(second.Tag.FirstPerformer == "Second Artist" && second.Tag.FirstAlbumArtist == "Various Artists" && second.Tag.Pictures.Length == 1,
            "Discogs numeric artist disambiguators must be removed from the repaired track credit.");
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(local.ReportPath));
    var evidence = report.RootElement.GetProperty("metadata_sources").EnumerateArray()
        .Select(value => value.GetString() ?? string.Empty).ToArray();
    var reportTracks = report.RootElement.GetProperty("discs")[0].GetProperty("tracks");
    Assert(evidence.Any(value => value.Contains("per-track artist credits", StringComparison.OrdinalIgnoreCase)) &&
           reportTracks[0].GetProperty("artist").GetString() == "First Artist" &&
           reportTracks[0].GetProperty("artist_source").GetString() == "discogs_exact_release_track_artist_credit" &&
           reportTracks[0].GetProperty("album_artist").GetString() == "Various Artists" &&
           report.RootElement.GetProperty("cover").GetProperty("source").GetString()!
               .Contains("i.discogs.com", StringComparison.OrdinalIgnoreCase),
        "The repair report must identify Discogs track-artist evidence and the in-memory Discogs cover source.");
}

static async Task TrackPerFileCueRepairsTransactionally(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is null) return;

    var album = Path.Combine(root, "Rock", "Track Per File Artist - Track Per File Album");
    Directory.CreateDirectory(album);
    var paths = new[]
    {
        Path.Combine(album, "01 - First.flac"),
        Path.Combine(album, "02 - Second.flac"),
        Path.Combine(album, "03 - Third.flac")
    };
    for (var index = 0; index < paths.Length; index++)
    {
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            $"sine=frequency={650 + index * 100}:duration=0.15", "-c:a", "flac", paths[index]);
        using var file = TagLib.File.Create(paths[index]);
        file.Tag.Title = index switch { 0 => "First", 1 => "Second", _ => "Third" };
        file.Tag.Album = "Track Per File Album";
        file.Tag.Performers = ["Track Per File Artist"];
        file.Tag.AlbumArtists = ["Track Per File Artist"];
        file.Tag.Track = (uint)(index + 1);
        file.Tag.TrackCount = 3;
        file.Tag.Disc = 1;
        file.Tag.DiscCount = 1;
        file.Tag.Year = 2020;
        file.Tag.Genres = ["Rock"];
        file.Save();
    }
    await File.WriteAllTextAsync(Path.Combine(album, "album.cue"), """
    FILE "01 - First.flac" WAVE
      TRACK 01 AUDIO
        INDEX 01 00:00:00
    FILE "02 - Second.flac" WAVE
      TRACK 02 AUDIO
        INDEX 01 00:00:00
    FILE "03 - Third.flac" WAVE
      TRACK 03 AUDIO
        INDEX 01 00:00:00
    """);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=green:s=96x96", "-frames:v", "1", "-update", "1", Path.Combine(album, "folder.jpg"));

    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.CueCount == 1 && scan.TrackCount == 3,
        "A one-file-per-track CUE must route to verified existing-track repair.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "A verified one-file-per-track CUE repair must pass preflight.");
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected track-per-file metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);

    Assert(local.Tracks == 3 && committed.Tracks == 3 && File.Exists(Path.Combine(album, "album.cue")) &&
           paths.All(File.Exists),
        "Track-per-file repair must replace only the FLACs transactionally and retain the CUE as provenance.");
    var completed = await new AlbumScanner().ScanAsync(album);
    Assert(completed.Mode == WorkflowMode.Completed && !completed.RequiresProcessing,
        "A committed one-file-per-track-CUE repair must inventory as complete.");
}

static async Task StandaloneDsfRepairPreservesNativePayloadAndDeletesRetainedIso(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is null) return;

    var album = Path.Combine(root, "Jazz", "Tester - Standalone DSF Album [DSD64]");
    Directory.CreateDirectory(album);
    var paths = Enumerable.Range(1, 3)
        .Select(index => Path.Combine(album, $"{index:00} - Track {index}.dsf"))
        .ToArray();
    for (var index = 0; index < paths.Length; index++)
    {
        CreateTaggedDsfFixture(paths[index], $"Track {index + 1}", (uint)(index + 1), 3, (byte)(0x30 + index));
        using var file = TagLib.File.Create(paths[index]);
        file.Tag.Album = "Standalone DSF Album";
        file.Tag.Performers = ["Tester"];
        file.Tag.AlbumArtists = [];
        file.Tag.Genres = [];
        file.Tag.Pictures = [];
        file.Save();
    }
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=navy:s=900x700", "-frames:v", "1", "-update", "1", Path.Combine(album, "front.jpg"));
    var retainedIso = Path.Combine(album, "retained-sacd.iso");
    await File.WriteAllBytesAsync(retainedIso, Enumerable.Range(0, 2048).Select(index => (byte)(index % 241)).ToArray());
    var payloadsBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in paths) payloadsBefore[path] = await DsfAudioPayload.Sha256Async(path);

    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.TrackCount == 3 && scan.ImageCount == 1 && scan.HasDsd,
        "A standalone DSF album with one retained SACD ISO must enter existing-track repair.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "A same-format standalone DSF album must pass verified write-back preflight.");
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected standalone-DSF metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);

    Assert(local.Tracks == 3 && committed.Tracks == 3 && committed.SourcesDeleted && !committed.Incomplete,
        "Standalone DSF repair must commit all tracks and delete only the verified retained ISO when requested.");
    Assert(!File.Exists(retainedIso),
        "A coexisting SACD ISO must be deleted after complete final-path DSF payload, tag, and artwork verification.");
    foreach (var path in paths)
    {
        Assert(await DsfAudioPayload.Sha256Async(path) == payloadsBefore[path],
            "Standalone DSF repair must preserve the exact native DSD data chunk.");
        using var file = TagLib.File.Create(path);
        Assert(file.Tag.FirstAlbumArtist == "Tester" && file.Tag.FirstGenre == "Jazz" && file.Tag.Disc == 1 &&
               file.Tag.Pictures.Length > 0,
            "Standalone DSF repair must fill album artist, library genre, disc numbering, and embedded artwork.");
    }
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(committed.ReportPath));
    Assert(report.RootElement.GetProperty("format").GetString() == "dsf" &&
           report.RootElement.GetProperty("verification").GetProperty("audio_payload_equivalence").GetString() == "passed" &&
           report.RootElement.GetProperty("verification").GetProperty("sources_deleted").GetBoolean() &&
           report.RootElement.GetProperty("deletion").GetProperty("files").GetArrayLength() == 1,
        "The standalone DSF repair report must record payload equivalence and the exact retained-ISO deletion.");
    var completed = await new AlbumScanner().ScanAsync(album);
    Assert(completed.Mode == WorkflowMode.Completed && !completed.RequiresProcessing,
        "A verified standalone DSF repair must inventory as complete on the next scan.");
}

static async Task StandaloneDffRepairPreservesNativePayloadAndDeletesRetainedIso(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is not { } ffprobe) return;

    var album = Path.Combine(root, "Jazz", "Tester - Standalone DFF Album (SACD-R)");
    var covers = Path.Combine(album, "covers");
    Directory.CreateDirectory(covers);
    var paths = Enumerable.Range(1, 3)
        .Select(index => Path.Combine(album, $"{index:00} - Tester - Track {index}.dff"))
        .ToArray();
    for (var index = 0; index < paths.Length; index++)
    {
        CreateDffFixture(paths[index], (byte)(0x40 + index));
        await DffMetadata.SaveAsync(paths[index], new(
            $"Track {index + 1}", "Standalone DFF Album", "Tester", null,
            (uint)(index + 1), 3, 0, 0, 2006, "Jazz", null, null));
    }
    var iso = Path.Combine(album, "retained-sacd.iso");
    await File.WriteAllBytesAsync(iso, Enumerable.Range(0, 4096).Select(index => (byte)(index % 251)).ToArray());
    var coverPath = Path.Combine(covers, "01.tif");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=teal:s=900x700", "-frames:v", "1", "-c:v", "tiff", coverPath);
    var missingRowsPerStrip = CreateRgbTiffWithoutRowsPerStrip();
    var repairedRowsPerStrip = InMemoryArtworkService.RepairMissingTiffRowsPerStrip(missingRowsPerStrip);
    Assert(repairedRowsPerStrip is not null &&
           InMemoryArtworkService.RepairMissingTiffRowsPerStrip(repairedRowsPerStrip) is null,
        "The TIFF fallback must add one RowsPerStrip directory entry in memory without altering the source scan.");

    var payloadsBefore = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var path in paths) payloadsBefore[path] = await DffMetadata.AudioSha256Async(path);

    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.TrackCount == 3 && scan.ImageCount == 1 &&
           scan.Media.Count(item => item.Kind == "Existing DFF") == 3,
        "Standalone DFF tracks with one retained SACD ISO must enter existing-track repair.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "Same-format standalone DFF tracks with one retained ISO must pass preflight.");
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal)) return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected standalone-DFF metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var retainedLocal = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    var retainedCommit = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: false);

    Assert(retainedLocal.Tracks == 3 && retainedCommit.Tracks == 3 && !retainedCommit.SourcesDeleted && File.Exists(iso),
        "Clearing Delete originals must retain the coexisting SACD ISO after DFF repair.");
    using (var retainedReport = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(retainedCommit.ReportPath)))
        Assert(retainedReport.RootElement.GetProperty("deletion").GetProperty("policy").GetString() ==
               "retained_sacd_iso_by_user_request_after_existing_dsd_track_repair",
            "A user-retained ISO must receive an explicit terminal policy instead of the legacy non-deletion policy.");
    Assert((await new AlbumScanner().ScanAsync(album)).Mode == WorkflowMode.Completed,
        "A new report that explicitly retains the ISO by user request must remain completed.");

    var legacyReport = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(retainedCommit.ReportPath))!.AsObject();
    legacyReport["deletion"]!["policy"] = "transactional_existing_track_replacement_without_source_deletion";
    await File.WriteAllTextAsync(retainedCommit.ReportPath,
        legacyReport.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair,
        "A legacy completed DFF repair with one still-retained ISO must be readmitted for verified source disposition.");
    preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "A readmitted legacy retained-ISO repair must pass preflight.");
    job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());
    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);

    Assert(local.Tracks == 3 && committed.Tracks == 3 && committed.SourcesDeleted && !committed.Incomplete,
        "Standalone DFF repair must commit all tracks and delete only the verified retained ISO when requested.");
    Assert(!File.Exists(iso),
        "A coexisting SACD ISO must be deleted after complete final-path DFF payload, tag, and artwork verification.");
    foreach (var path in paths)
    {
        Assert(await DffMetadata.AudioSha256Async(path) == payloadsBefore[path],
            "Standalone DFF repair must preserve the exact native DSD chunk.");
        var tag = DffMetadata.Read(path);
        Assert(tag.AlbumArtist == "Tester" && tag.Genre == "Jazz" && tag.Disc == 1 &&
               tag.Picture is { Length: > 0 } && tag.SampleRate == 2_822_400 && tag.Channels == 2,
            "Standalone DFF repair must fill tags and embed normalized TIFF-derived artwork.");
    }
    using var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(committed.ReportPath));
    Assert(report.RootElement.GetProperty("format").GetString() == "dff" &&
           report.RootElement.GetProperty("verification").GetProperty("audio_payload_equivalence").GetString() == "passed" &&
           report.RootElement.GetProperty("verification").GetProperty("sources_deleted").GetBoolean() &&
           report.RootElement.GetProperty("deletion").GetProperty("files").GetArrayLength() == 1 &&
           report.RootElement.GetProperty("deletion").GetProperty("files")[0].GetString() == "retained-sacd.iso",
        "The standalone DFF repair report must record payload equivalence and only the retained-ISO deletion target.");
    var completed = await new AlbumScanner().ScanAsync(album);
    Assert(completed.Mode == WorkflowMode.Completed && !completed.RequiresProcessing,
        "A verified DFF repair after retained-ISO deletion must inventory as complete on the next scan.");
}

static async Task ExactDuplicateExistingTracksAreCollapsedTransactionally(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg || toolCheck.Tools["ffprobe"] is null) return;

    var album = Path.Combine(root, "Rock", "Exact Duplicate Artist - Exact Duplicate Album");
    Directory.CreateDirectory(album);
    var first = Path.Combine(album, "01 - First.flac");
    var second = Path.Combine(album, "02 - Second.flac");
    var duplicate = Path.Combine(album, "02.Second duplicate.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=720:duration=0.15", "-c:a", "flac", first);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "sine=frequency=860:duration=0.15", "-c:a", "flac", second);
    for (var index = 0; index < 2; index++)
    {
        var path = index == 0 ? first : second;
        using var file = TagLib.File.Create(path);
        file.Tag.Title = index == 0 ? "First" : "Second";
        file.Tag.Album = "Exact Duplicate Album";
        file.Tag.Performers = ["Exact Duplicate Artist"];
        file.Tag.AlbumArtists = ["Exact Duplicate Artist"];
        file.Tag.Track = (uint)(index + 1);
        file.Tag.TrackCount = 2;
        file.Tag.Disc = 1;
        file.Tag.DiscCount = 1;
        file.Tag.Year = 2020;
        file.Tag.Genres = ["Rock"];
        file.Save();
    }
    File.Copy(second, duplicate);
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=96x96", "-frames:v", "1", "-update", "1", Path.Combine(album, "folder.jpg"));

    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.TrackCount == 3,
        "Three physical FLAC entries must remain visible to the scanner before exact duplicate validation.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "An existing-track album containing an exact duplicate must pass repair preflight.");

    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected exact-duplicate metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(local.Tracks == 2 && !local.Metadata.RequiresResearch,
        "A byte-identical same-coordinate duplicate must collapse to the logical two-track album before metadata resolution.");
    using (var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(local.ReportPath)))
    {
        var deduplicated = report.RootElement.GetProperty("deduplicated_tracks");
        Assert(deduplicated.GetArrayLength() == 1 &&
               deduplicated[0].GetProperty("removed_file").GetString() == "02.Second duplicate.flac" &&
               deduplicated[0].GetProperty("retained_file").GetString() == "02 - Second.flac" &&
               deduplicated[0].GetProperty("source_file_sha256").GetString()?.Length == 64,
            "The repair report must identify the exact duplicate, retained file, and source full-file hash.");
    }
    Assert(File.Exists(duplicate), "Local processing must not remove a duplicate from the source album before commit.");

    var changedDuplicate = await File.ReadAllBytesAsync(duplicate);
    changedDuplicate[changedDuplicate.Length / 2] ^= 0x01;
    await File.WriteAllBytesAsync(duplicate, changedDuplicate);
    Exception? changedSourceError = null;
    try
    {
        await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);
    }
    catch (Exception error)
    {
        changedSourceError = error;
    }
    Assert(changedSourceError is IOException &&
           changedSourceError.Message.Contains("changed after inventory", StringComparison.OrdinalIgnoreCase) &&
           File.Exists(first) && File.Exists(second) && File.Exists(duplicate),
        "A same-size source change after local validation must fail closed before any album file is moved.");
    File.Copy(second, duplicate, overwrite: true);

    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);
    Assert(committed.Tracks == 2 && File.Exists(first) && File.Exists(second) && !File.Exists(duplicate),
        "Commit must transactionally retain the canonical files and remove only the revalidated byte-identical duplicate entry.");
    var completed = await new AlbumScanner().ScanAsync(album);
    Assert(completed.Mode == WorkflowMode.Completed && !completed.RequiresProcessing &&
           completed.Media.Count(item => item.Kind == "Previous Album Fixer output") == 2,
        "The committed exact-duplicate repair must inventory as a completed logical two-track album.");
}

static void DuplicateTaggedBonusTrackUsesFilenameAnchor()
{
    var evidence = Enumerable.Range(1, 14)
        .Select(number => (1u, (uint?)number, (uint?)number, $"{number:00} - album track"))
        .Append((1u, (uint?)14, null, "Get Lucky (Club Mix)"))
        .ToArray();
    var resolved = LocalTrackRepairProcessor.ResolveTrackNumbers(evidence);
    Assert(resolved.SequenceEqual(Enumerable.Range(1, 15).Select(number => (uint)number)),
        "When exactly one duplicate tagged coordinate has matching filename evidence, it must retain that number and the unnumbered bonus track must receive the next unused number.");

    var stillAmbiguous = LocalTrackRepairProcessor.ResolveTrackNumbers(
        [(1u, (uint?)14, null, "Bonus A"), (1u, (uint?)14, null, "Bonus B")]);
    Assert(stillAmbiguous[0] == 14 && stillAmbiguous[1] == 14,
        "Duplicate tagged coordinates without a unique filename anchor must remain ambiguous instead of being silently reordered.");

    var repeatedMiddleTags = Enumerable.Range(1, 12)
        .Select(number => (1u, (uint?)(number switch { 9 => 7, 10 => 8, 11 => 9, 12 => 10, _ => number }),
            (uint?)number, $"aurelio-darandi-{number:00}-track-{number:00}"))
        .ToArray();
    var repairedSequence = LocalTrackRepairProcessor.ResolveTrackNumbers(repeatedMiddleTags);
    Assert(repairedSequence.SequenceEqual(Enumerable.Range(1, 12).Select(number => (uint)number)),
        "A complete unique 1..N filename sequence must repair contradictory repeated embedded track tags as one album-level decision.");

    var parsedSceneName = LocalTrackRepairProcessor.ParseFileName("aurelio-darandi-07-sananaru");
    Assert(parsedSceneName.Number == 7 && parsedSceneName.Title == "sananaru",
        "Scene-style filenames may carry the track number after artist and album text.");
}

static void RecognizedGenreFoldersAreNotArtistEvidence()
{
    var identity = LocalTrackRepairProcessor.ParseFolderIdentity("Random Access Memories [Limited Box Set Edition]");
    Assert(identity.Artist is null && identity.Album == "Random Access Memories",
        "A containing library category must not become artist metadata; only identity encoded in the album folder itself may be parsed.");

    var encodedIdentity = LocalTrackRepairProcessor.ParseFolderIdentity("Daft Punk - Random Access Memories [Limited Box Set Edition]");
    Assert(encodedIdentity.Artist == "Daft Punk" && encodedIdentity.Album == "Random Access Memories",
        "Artist metadata may still be parsed when it is explicitly encoded in the album folder itself.");

    var albumRoot = Path.Combine(Path.GetTempPath(), "Music", "Alternative", "Random Access Memories [Limited Box Set Edition]");
    Assert(LibraryFolderMetadata.InferGenre(albumRoot) == "Alternative",
        "A recognized Alternative library folder may supply GENRE while remaining ineligible as ARTIST or ALBUMARTIST evidence.");
}

static void ClassicalComposerUsesCorroboratedAlbumIdentity()
{
    var composer = LocalTrackRepairProcessor.InferClassicalComposer(
        "A. Dvořák - String Quintet Op. 77",
        "Dvorak String Quintet - Berlin Philharmonic - SoS Bonus - FLAC 5CH",
        "Classical",
        ["Berlin Philharmonic String Quintet"]);
    Assert(composer == "A. Dvořák",
        "A missing classical COMPOSER must be resolved when the album tag names it, the album folder independently corroborates its surname, and it differs from the performer.");

    var attachedInitialComposer = LocalTrackRepairProcessor.InferClassicalComposer(
        "A.Dvorák - String Quintet Op.77",
        "Dvorak String Quintet - Berlin Philharmonic - SoS Bonus - FLAC24",
        "Classical",
        ["Berlin Philharmonic String Quintet"]);
    Assert(attachedInitialComposer == "A. Dvorák",
        "A composer initial attached to the surname must be normalized before surname corroboration so equivalent FLAC editions resolve consistently.");

    Assert(LocalTrackRepairProcessor.InferClassicalComposer(
               "A. Dvořák - String Quintet Op. 77",
               "Unrelated Album Folder",
               "Classical",
               ["Berlin Philharmonic String Quintet"]) is null &&
           LocalTrackRepairProcessor.InferClassicalComposer(
               "Berlin Philharmonic String Quintet - String Quintet Op. 77",
               "Berlin Philharmonic String Quintet - Bonus",
               "Classical",
               ["Berlin Philharmonic String Quintet"]) is null &&
           LocalTrackRepairProcessor.InferClassicalComposer(
               "A. Dvořák - String Quintet Op. 77",
               "Dvorak String Quintet - Bonus",
               "Rock",
               ["Berlin Philharmonic String Quintet"]) is null,
        "Composer inference must stop when folder corroboration is absent, the candidate is the performer, or the release is not classical/opera.");
}

static void ClassicalTrackComposersUseReviewedAlbumAndWorkEvidence()
{
    var beethoven = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "BEETHOVEN Piano Concerto No 2",
        "BEETHOVEN-Piano-Concerto-No-2-FLAC24",
        "Classical",
        [("Piano Concerto No 2 i Allegro", "Piano Concerto No 2 i Allegro"),
         ("Piano Concerto No 2 ii Adagio", "Piano Concerto No 2 ii Adagio")]);
    Assert(beethoven.SequenceEqual(["Ludwig van Beethoven", "Ludwig van Beethoven"]),
        "A sole reviewed composer surname in a classical album identity must fill every track without treating performers as composers.");

    var rachmaninoffBalakirev = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "RACHMANINOV Symphony No 1 & BALAKIREV Tamara",
        "RACHMANINOV-Symphony-No-1-&-BALAKIREV-Tamara-FLAC24",
        "Classical",
        [("Symphony No 1 in D minor i Grave", "Symphony No 1 in D minor i Grave"),
         ("Symphony No 1 in D minor ii Allegro", "Symphony No 1 in D minor ii Allegro"),
         ("Tamara", "Tamara")]);
    Assert(rachmaninoffBalakirev.SequenceEqual(["Sergei Rachmaninoff", "Sergei Rachmaninoff", "Mily Balakirev"]),
        "Multiple album composers must be assigned only when each track matches the work identity associated with that composer.");

    var mixedStrings = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "VAUGHAN WILLIAMS Fantasia on a Theme by Thomas Tallis, BRITTEN Variations on a Theme of Frank Bridge",
        "VAUGHAN-WILLIAMS-BRITTEN-FLAC24",
        "Classical",
        [("Introduction and Allegro", "ELGAR Introduction and Allegro for Strings"),
         ("Fantasia on a Theme by Thomas Tallis", "VAUGHAN WILLIAMS Fantasia on a Theme by Thomas Tallis"),
         ("Variations: Introduction", "BRITTEN Variations on a Theme of Frank Bridge")]);
    Assert(mixedStrings.SequenceEqual(["Edward Elgar", "Ralph Vaughan Williams", "Benjamin Britten"]),
        "An explicit reviewed surname in each filename must support track-specific composers, including a composer absent from the album title.");

    var mixedRomantics = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "MENDELSSOHN Symphony No 3, Hebrides Overture SCHUMANN Piano Concerto",
        "Mendelssohn-Schumann-FLAC24",
        "Classical",
        [("The Hebrides Overture", "MENDELSSOHN The Hebrides Overture"),
         ("Piano Concerto i Allegro", "SCHUMANN Piano Concerto i Allegro"),
         ("Symphony No 3 i Andante", "MENDELSSOHN Symphony No 3 i Andante")]);
    Assert(mixedRomantics.SequenceEqual(["Felix Mendelssohn", "Robert Schumann", "Felix Mendelssohn"]),
        "Explicit composer surnames in a mixed classical track list must override ambiguous album-wide ordering.");

    var singleComposerAlbums = new[]
    {
        ("Brahms-Clarinet Trio & Quintet", "Brahms-Clarinet Trio & Quintet", "Johannes Brahms"),
        ("BERLIOZ Roméo et Juliette", "BERLIOZ-Romeo-et-Juliette-FLAC24", "Hector Berlioz"),
        ("Arcangelo Corelli - Opus 6: Concerti Grossi", "Corelli - Concerti Grossi Op.6, The Avison Ensemble (2012)", "Arcangelo Corelli"),
        ("DVORAK Symphony No 7", "DVORAK-Symphony-No-7-FLAC24", "Antonín Dvořák"),
        ("SCRIABIN Symphony No 1", "SCRIABIN-SYMPHONY-No1-FLAC24", "Alexander Scriabin"),
        ("SIBELIUS Symphonies Nos 1 & 4", "SIBELIUS-Symphonies-Nos-1-and-4-FLAC24", "Jean Sibelius"),
        ("STRAUSS Elektra", "STRAUSS-Elektra-FLAC24", "Richard Strauss"),
        ("WALTON Belshazzar's Feast & Symphony No 1", "WALTON-Belshazzar's-Feast-&-Symphony-No-1-FLAC24", "William Walton")
    };
    Assert(singleComposerAlbums.All(value => LocalTrackRepairProcessor.InferClassicalTrackComposers(
               value.Item1, value.Item2, "Classical", [("First movement", "First movement")]).Single() == value.Item3),
        "Each reviewed single-composer album identity from the failed batch must resolve its canonical composer deterministically.");

    var nielsenMozart = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "Nielsen & Mozart: Clarinet Concertos", "Neilsen-Mozart-Clarinet-Concertos", "Classical",
        [("Clarinet Concerto - I. Allegretto un poco", "01 - Clarinet Concerto - I. Allegretto un poco"),
         ("Clarinet Concerto - IV. Allegro vivace", "04 - Clarinet Concerto - IV. Allegro vivace"),
         ("Non che non sei capace, K. 419", "05 - Non che non sei capace K 419"),
         ("Clarinet Concerto in A major, K. 622", "07 - Clarinet Concerto in A major K622")]);
    Assert(nielsenMozart.SequenceEqual(["Carl Nielsen", "Carl Nielsen", "Wolfgang Amadeus Mozart", "Wolfgang Amadeus Mozart"]),
        "Mozart K-catalog evidence must identify its tracks and conservatively assign the remaining two-composer album tracks to Nielsen.");

    var mozartVieuxtemps = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "Violin Concertos: Mozart 5 & Vieuxtemps 4", "Mozart-Vieuxtemps-Violin-Concertos", "Classical",
        [("Violin Concerto No. 5 in A, K. 219", "01 Violin Concerto K219"),
         ("Violin Concerto No. 4 in D minor, Op. 31", "04 Violin Concerto Op 31"),
         ("Hilary Hahn and Paavo Järvi in Conversation, Pt. 1", "08 Conversation Pt 1")]);
    Assert(mozartVieuxtemps.SequenceEqual(["Wolfgang Amadeus Mozart", "Henri Vieuxtemps", null]),
        "Two-composer catalog inference must resolve the musical works while leaving interview or conversation tracks without an invented composer.");

    var explicitCompilation = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "Piano", "Myung Whun Chung - Piano", "Classical",
        [("Debussy: Clair de Lune", "01 - Debussy- Clair de Lune"),
         ("Chopin: Nocturne", "02 - Chopin- Nocturne"),
         ("Schubert: Impromptu", "03 - Schubert- Impromptu"),
         ("Tchaikovsky: Autumn Song", "04 - Tchaikovsky- Autumn Song")]);
    Assert(explicitCompilation.SequenceEqual(["Claude Debussy", "Frédéric Chopin", "Franz Schubert", "Pyotr Ilyich Tchaikovsky"]),
        "Per-track surnames must resolve a recital compilation without inventing an album-wide composer.");

    var shostakovichShchedrin = LocalTrackRepairProcessor.InferClassicalTrackComposers(
        "MAR0509 SHOSTAKOVICH & SHCHEDRIN Piano Concertos",
        "Shostakovich & Shchedrin Piano Concertos - Marinsky - FLAC24", "Classical",
        [("Piano Concerto No 1", "01 Shostakovich Piano Concerto No 1"),
         ("Piano Concerto No 5", "08 Shchedrin Piano Concerto No 5")]);
    Assert(shostakovichShchedrin.SequenceEqual(["Dmitri Shostakovich", "Rodion Shchedrin"]) &&
           LocalTrackRepairProcessor.InferReviewedReleaseYear(
               "MAR0509 SHOSTAKOVICH & SHCHEDRIN Piano Concertos",
               "Shostakovich & Shchedrin Piano Concertos - Marinsky - FLAC24") == 2012 &&
           LocalTrackRepairProcessor.InferReviewedReleaseYear("Other Piano Concertos", "Other Album") is null,
        "The exact MAR0509 identity must supply its reviewed 2012 release year and track-specific composers without affecting unrelated releases.");

    Assert(LocalTrackRepairProcessor.InferClassicalTrackComposers(
               "Wagner / Dressler: The Symphonic Ring", "Wagner Dressler The Symphonic Ring", "Classical",
               [("Prelude Rhinegold", "01 - Prelude Rhinegold")]).Single() == "Richard Wagner",
        "The Symphonic Ring must credit Richard Wagner as composer; Dressler's name must not be misclassified from the arrangement title.");

    Assert(LocalTrackRepairProcessor.InferClassicalTrackComposers(
               "Unknown Symphony", "Unknown-Symphony-FLAC24", "Classical",
               [("Symphony i", "Symphony i")]).Single() is null,
        "Unknown classical text must remain unresolved instead of inventing a composer.");

    var opus3Titles = new (string? Title, string FileTitle)[]
    {
        ("1.Here's that rainy day", "Here's that rainy day"),
        ("2.Teach me Tonight", "Teach me Tonight"),
        (" 3.Black Beauty", "Black Beauty"),
        ("4.Where the green grass grows", "Where the green grass grows"),
        ("5.Vaquero", "Vaquero"),
        ("6.La Maja de Goya", "La Maja de Goya"),
        ("7.Nun komm der Heiden Heiland", "Nun komm der Heiden Heiland"),
        ("8.Scherzo from Symphony no.2 in D Major", "Scherzo"),
        ("9.Overture to Carmen", "Overture to Carmen")
    };
    var opus3Composers = LocalTrackRepairProcessor.InferReviewedTrackComposers(
        "Opus3 DSD Showcase1", "Opus3 DSD Showcase 1", opus3Titles);
    Assert(opus3Composers.SequenceEqual([
               "Jimmy Van Heusen", "Gene de Paul", "Duke Ellington", "Eric Bibb", "Göran Wennerbrandt",
               "Enrique Granados", "Johann Sebastian Bach", "Ludwig van Beethoven", "Georges Bizet"]),
        "The exact Opus3 DSD Showcase 1 title sequence must receive its reviewed per-track composer credits.");

    var alteredOpus3Titles = opus3Titles.ToArray();
    alteredOpus3Titles[4] = ("Different Vaquero", "05 - Different Vaquero");
    Assert(LocalTrackRepairProcessor.InferReviewedTrackComposers(
               "Opus3 DSD Showcase1", "Opus3 DSD Showcase 1", alteredOpus3Titles).All(value => value is null) &&
           LocalTrackRepairProcessor.InferReviewedTrackComposers(
               "Opus3 DSD Showcase1", "Other Showcase", opus3Titles).All(value => value is null),
        "Reviewed compilation credits must not leak to a release with a changed title sequence or folder identity.");

    var linnVolume3Titles = new (string? Title, string FileTitle)[]
    {
        ("He never mentioned love", "01 - He never mentioned love"),
        ("Painting by Numbers", "02 - Painting by Numbers"),
        ("Beautiful Life", "03 - Beautiful Life"),
        ("Yes I Know When I ve Had It", "04 - Yes I Know When I ve Had It"),
        ("Love At Last", "05 - Love At Last"),
        ("A Case of You", "06 - A Case of You"),
        ("Grandes Etudes de Paganini Etude III", "07 - Grandes Etudes de Paganini Etude III"),
        ("March, (K.189)", "08 - March K 189"),
        ("Capriccio Espagnol Op.34 Scena e canto gitano", "09 - Capriccio Espagnol Op 34 Scena e canto gitano"),
        ("Ludlow and Teme - When I was one and twenty", "10 - Ludlow and Teme - When I was one and twenty"),
        ("Messiah: And the Glory of the Lord (chorus)", "11 - Messiah And the Glory of the Lord chorus"),
        ("A Chloris", "12 - A Chloris"),
        ("Missa Brevis - Kyrie", "13 - Missa Brevis - Kyrie"),
        ("Sonata for Clarinet and Piano Allegro Con Fuoco", "14 - Sonata for Clarinet and Piano Allegro Con Fuoco"),
        ("Sonata ‘Graz’ (No.3) for violin & continuo in D, RV 11 Allegro", "15 - Sonata Graz No 3 for violin continuo in D RV 11 Allegro"),
        ("Cumbees", "16 - Cumbees")
    };
    var linnVolume3Composers = LocalTrackRepairProcessor.InferReviewedTrackComposers(
        "Super Audio Surround Collection Vol 3 sampler",
        "Linn Records - The Super Audio Collection Volume 3", linnVolume3Titles);
    Assert(linnVolume3Composers.Take(6).All(value => value is null) &&
           linnVolume3Composers.Skip(6).SequenceEqual([
               "Franz Liszt", "Wolfgang Amadeus Mozart", "Nikolai Rimsky-Korsakov", "Ivor Gurney",
               "George Frideric Handel", "Reynaldo Hahn", "James MacMillan", "Francis Poulenc",
               "Antonio Vivaldi", "Santiago de Murcia"]),
        "The exact Linn Volume 3 sequence must distinguish its six nonclassical selections from ten reviewed classical work credits.");

    var linnXmasTitles = new[]
    {
        "Almost Like Being In Love", "24 Preludes, Op. 28: No. 15 in D flat Major 'Raindrop'",
        "The Man Who Sold The World", "Brandenburg Concerto No. 2 in F Major, BWV 1047 - III. Allegro",
        "Many Rivers To Cross", "Symphony No. 2 in C major, Op. 61 - III. Adagio espressivo", "Secret Love",
        "The Well-Tempered Clavier Book I: Prelude & Fugue No. 21 in B flat Major, BWV 866", "Old Greenwich Time",
        "Recorder Concerto in F Major: III. Allegro", "Twitter and Bisted", "Flute Concerto: II. Alla Marcia",
        "Forty-two", "Symphonie No. 2 in C minor: III. Scherzo: Massig schnell", "We'll Never Have Manhattan",
        "Sonnerie de Sainte Genevieve du Mont de Paris", "Giant Steps", "The End Of A Love Affair",
        "Nicholas Drake", "Requiem in D minor, K. 626: I. Requiem aeternam", "Pause", "L'Envie: Vocalise No. 28",
        "Toccata and Fugue in A minor, after Bach BWV 565", "Both Sides Now", "Caledonia"
    }.Select((title, index) => ((string?)title, $"{index + 1:D2} - {title}")).ToArray();
    var linnXmasComposers = LocalTrackRepairProcessor.InferReviewedTrackComposers(
        "24-bits of Christmas 2014", "Linn Records - Xmas Gifts 2014", linnXmasTitles);
    Assert(linnXmasComposers[1] == "Frédéric Chopin" &&
           linnXmasComposers[9] == "Johann Friedrich Fasch" &&
           linnXmasComposers[21] == "Gabriel Fauré" &&
           linnXmasComposers[0] is null && linnXmasComposers[23] is null,
        "The exact Linn 2014 Christmas sequence must add reviewed composer credits only to its classical selections.");
}

static void CompilationIdentityAndClassicalTrackEvidenceAreConservative()
{
    var commonLead = LocalTrackRepairProcessor.ResolveTrackAlbumArtist(
    [
        "Maria Callas, Orchestra del Teatro alla Scala di Milano, Tullio Serafin",
        "Maria Callas, Philharmonia Orchestra, Alceo Galliera",
        "Maria Callas, Orchestre National de la Radiodiffusion Française, Georges Prêtre"
    ]);
    Assert(commonLead == "Maria Callas",
        "A common leading performer on every track must become the album artist without copying one long track-specific credit.");

    var compilationArtist = LocalTrackRepairProcessor.ResolveTrackAlbumArtist(
        ["Plaid", "John Metcalfe", "Cara Dillon", "Hannah Peel"]);
    Assert(compilationArtist == "Various Artists",
        "Three or more distinct track artists must establish a Various Artists compilation instead of choosing one arbitrarily.");

    Assert(!ClassicalMetadataPolicy.RequiresComposer("Classical, pop, jazz", "Makin' Whoopee") &&
           !ClassicalMetadataPolicy.RequiresComposer("Jazz, Rock, Pop, Classical, Folk, World, & Country", "Blackwood") &&
           !ClassicalMetadataPolicy.RequiresComposer("Classical", "Makin' Whoopee", isCompilation: true) &&
           ClassicalMetadataPolicy.RequiresComposer("Classical", "Recorder Concerto in F Major", isCompilation: true) &&
           ClassicalMetadataPolicy.RequiresComposer("Classical, pop, jazz", "Symphony No. 40 in G minor, K 550, I. Molto Allegro") &&
           ClassicalMetadataPolicy.RequiresComposer("Classical, pop, jazz", "Grandes Etudes de Paganini – Etude III") &&
           ClassicalMetadataPolicy.RequiresComposer("Classical", "Ave Maria"),
        "Mixed-genre compilations must require COMPOSER only for titles with explicit classical-work evidence, while a purely classical album retains the requirement for every musical track.");

    Assert(LocalTrackRepairProcessor.InferKnownComposerFromFileTitle(
               "14 - Concerto Caledonia - Thomas Erskine - Overture in C (Erskine)") == "Thomas Erskine" &&
           LocalTrackRepairProcessor.InferKnownComposerFromFileTitle(
               "15 - Francesco Geminiani - Sonata Op.5 No.4 (Geminiani)") == "Francesco Geminiani" &&
           ClassicalMetadataPolicy.IsCompilationArtist("Various Artists"),
        "DFF filename evidence and the Various Artists album credit must support mixed Linn compilation repair without inventing composers for jazz tracks.");

    var operaEvidence = new (string? Title, string FileTitle)[]
    {
        ("Norma, Act 1: Casta diva", "Casta Diva (Bellini Norma)"),
        ("Orphée et Eurydice, Act 4", "J'ai perdu mon Eurydice (Gluck Orphee et Eurydice)"),
        ("Il barbiere di Siviglia, Act 1", "Una voce poco fa (Rossini Il barbiere di Siviglia)"),
        ("Carmen, Act 1", "L'amour est un oiseau rebelle (Bizet Carmen)")
    };
    Assert(LocalTrackRepairProcessor.InferGenreFromTrackEvidence(operaEvidence) == "Opera" &&
           LocalTrackRepairProcessor.InferKnownComposerFromFileTitle(operaEvidence[0].FileTitle) == "Vincenzo Bellini" &&
           LocalTrackRepairProcessor.InferKnownComposerFromFileTitle("Ordinary Song (Unknown Person)") is null,
        "Recognized composer surnames across most tracks may establish classical/opera metadata, while unknown parenthetical text must not be treated as a composer.");
}

static void ExternalAlbumTitleQualifiersRemainEquivalent()
{
    using var featuredRelease = System.Text.Json.JsonDocument.Parse("""
        {"artist-credit":[
          {"name":"The Mystery Of The Bulgarian Voices","joinphrase":" featuring "},
          {"name":"Lisa Gerrard"}
        ]}
        """);
    Assert(ExternalMetadataService.AlbumTitlesEquivalent(
               "Maria Callas Remastered – A selection", "Maria Callas Remastered (Society of Sound)") &&
           ExternalMetadataService.AlbumTitlesEquivalent(
               "Bowers & Wilkins - Live", "Bowers & Wilkins - Live (WOMAD 2015)") &&
           ExternalMetadataService.TrackTitlesEquivalent(
               "Teil I", "Der Klang der Offenbarung des Göttlichen: Teil I") &&
           ExternalMetadataService.ArtistCreditsEquivalent(
               "The Mystery Of The Bulgarian Voices featuring Lisa Gerrard",
               "The Mystery Of The Bulgarian Voices") &&
           ExternalMetadataService.AlbumLookupTitle(
               "The Mystery Of The Bulgarian Voices - BooCheeMish",
               "The Mystery Of The Bulgarian Voices") == "BooCheeMish" &&
           ExternalMetadataService.FeaturedCreditContainsArtist(
               featuredRelease.RootElement, "The Mystery Of The Bulgarian Voices") &&
           !ExternalMetadataService.ArtistCreditsEquivalent(
               "The Mystery Of The Bulgarian Voices & Lisa Gerrard",
               "The Mystery Of The Bulgarian Voices") &&
           !ExternalMetadataService.AlbumTitlesEquivalent("Bowers & Wilkins - Live", "Bowers & Wilkins - Studio"),
        "Known album qualifiers, fully qualified external track titles, and an explicit trailing featured-artist credit may differ without weakening the core identity comparison.");
}

static async Task ExistingTracksRepairSupportsMultipleDiscs(string root)
{
    var seed = await new AlbumScanner().ScanAsync(Path.Combine(root, "flac-cue"));
    var toolCheck = await new PreflightService().CheckAsync(seed);
    if (toolCheck.Tools["ffmpeg"] is not { } ffmpeg) return;

    var album = Path.Combine(root, "Rock", "Two Disc Artist - Two Disc Album");
    var discOne = Path.Combine(album, "Disc 1");
    var discTwo = Path.Combine(album, "Disc 2");
    Directory.CreateDirectory(discOne);
    Directory.CreateDirectory(discTwo);
    var fixtures = new[]
    {
        (Disc: 1u, Track: 1u, Path: Path.Combine(discOne, "01 - Disc One First.flac"), Title: "Disc One First"),
        (Disc: 1u, Track: 2u, Path: Path.Combine(discOne, "02 - Disc One Second.flac"), Title: "Disc One Second"),
        (Disc: 2u, Track: 1u, Path: Path.Combine(discTwo, "01 - Disc Two First.flac"), Title: "Disc Two First"),
        (Disc: 2u, Track: 2u, Path: Path.Combine(discTwo, "02 - Disc Two Second.flac"), Title: "Disc Two Second")
    };
    for (var index = 0; index < fixtures.Length; index++)
    {
        var fixture = fixtures[index];
        await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
            $"sine=frequency={500 + index * 100}:duration=0.15", "-c:a", "flac", fixture.Path);
        using var file = TagLib.File.Create(fixture.Path);
        file.Tag.Album = "Two Disc Album";
        file.Tag.Performers = ["Two Disc Artist"];
        file.Tag.AlbumArtists = ["Two Disc Artist"];
        file.Tag.Track = fixture.Disc == 1 ? fixture.Track : 0;
        file.Tag.TrackCount = fixture.Disc == 1 ? 2u : 0;
        file.Tag.Disc = fixture.Disc == 1 ? fixture.Disc : 0;
        file.Tag.DiscCount = fixture.Disc == 1 ? 2u : 0;
        file.Tag.Year = 1979;
        file.Tag.Genres = ["Rock"];
        file.Save();
    }
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i",
        "color=c=purple:s=96x96", "-frames:v", "1", "-update", "1", Path.Combine(album, "cover.jpg"));

    var scan = await new AlbumScanner().ScanAsync(album);
    Assert(scan.Mode == WorkflowMode.ExistingTrackRepair && scan.TrackCount == 4,
        "A nested two-disc FLAC album must classify as one existing-track repair job.");
    var preflight = await new PreflightService().CheckAsync(scan);
    Assert(preflight.CanStart, "A nested two-disc FLAC album must pass existing-track repair preflight.");

    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[{"id":"release-multidisc","score":100,"title":"Two Disc Album","artist-credit":[{"name":"Two Disc Artist"}],"release-group":{"id":"group-multidisc"},"date":"1979-01-01","track-count":4,"media":[{"format":"CD","track-count":2},{"format":"CD","track-count":2}],"label-info":[]}]}""");
        if (uri.Contains("/ws/2/release-group/group-multidisc", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"first-release-date":"1979-01-01","genres":[{"name":"rock","count":10}],"tags":[],"relations":[]}""");
        if (uri.Contains("/ws/2/release/release-multidisc", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"media":[{"position":2,"tracks":[{"position":2,"title":"Disc Two Second"},{"position":1,"title":"Disc Two First"}]},{"position":1,"tracks":[{"position":2,"title":"Disc One Second"},{"position":1,"title":"Disc One First"}]}]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[{"artistName":"Two Disc Artist","collectionName":"Two Disc Album","primaryGenreName":"Rock","releaseDate":"1979-01-01T00:00:00Z","trackCount":4,"collectionViewUrl":"https://music.apple.com/album/two-disc"}]}""");
        throw new InvalidOperationException($"Unexpected multi-disc metadata request: {uri}");
    }));
    var external = new ExternalMetadataService(client, discogsToken: null,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var job = PreflightService.CreateJobDirectory(preflight.TempRoot);
    var staged = await new HostStagingService().StageAsync(scan, preflight, job, new Progress<ProgressSnapshot>());
    var local = await new LocalTrackRepairProcessor(external).ProcessAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(local.Tracks == 4 && !local.Metadata.RequiresResearch,
        "Repeated track numbers on different discs must not be treated as ambiguous.");
    foreach (var fixture in fixtures)
    {
        var relative = Path.GetRelativePath(album, fixture.Path);
        using var repaired = TagLib.File.Create(Path.Combine(staged.AlbumRoot, relative));
        Assert(repaired.Tag.Title == fixture.Title && repaired.Tag.Track == fixture.Track && repaired.Tag.TrackCount == 2 &&
               repaired.Tag.Disc == fixture.Disc && repaired.Tag.DiscCount == 2,
            "Multi-disc repair must map external titles in disc/track order and write per-disc totals.");
    }
    using (var report = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(local.ReportPath)))
    {
        var discs = report.RootElement.GetProperty("discs");
        Assert(discs.GetArrayLength() == 2 &&
               discs[0].GetProperty("disc").GetUInt32() == 1 && discs[0].GetProperty("tracks").GetArrayLength() == 2 &&
               discs[1].GetProperty("disc").GetUInt32() == 2 && discs[1].GetProperty("tracks").GetArrayLength() == 2,
            "The repair report must preserve the two-disc hierarchy.");
    }

    var committed = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>(), deleteOriginals: true);
    Assert(committed.Tracks == 4 && fixtures.All(fixture => File.Exists(fixture.Path)),
        "Transactional commit must replace nested multi-disc tracks without deleting them as source images.");
    using (var finalDiscTwoTrackOne = TagLib.File.Create(fixtures[2].Path))
        Assert(finalDiscTwoTrackOne.Tag.Title == "Disc Two First" && finalDiscTwoTrackOne.Tag.Disc == 2 &&
               finalDiscTwoTrackOne.Tag.Track == 1 && finalDiscTwoTrackOne.Tag.TrackCount == 2,
            "Committed Disc 2 tags must retain disc-aware numbering and totals.");
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

static async Task HostCommitsVerifiedFlac(
    string root,
    bool deleteOriginals,
    bool optionalMetadataMissing = false,
    bool requiredMetadataMissing = false)
{
    var destination = Path.Combine(root, requiredMetadataMissing
        ? "commit-required-metadata"
        : optionalMetadataMissing ? "commit-optional-metadata"
        : deleteOriginals ? "commit-destination" : "commit-retain-original");
    Directory.CreateDirectory(destination);
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
    var missingMetadata = requiredMetadataMissing
        ? "[\"CATALOGNUMBER\"]"
        : optionalMetadataMissing ? "[\"LABEL\", \"BARCODE\", \"RELEASECOUNTRY\"]" : "[]";
    var report = $$"""
    {
      "schema_version": "2.0",
      "album": "Test Album",
      "edition": "Synthetic transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": {{coverDescriptor}},
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Test", "file": "01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [], "missing_metadata": {{missingMetadata}} }
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
    var expectedDeletion = deleteOriginals && !requiredMetadataMissing;
    Assert(result.Tracks == 1 && result.SourcesDeleted == expectedDeletion,
        expectedDeletion ? "The exact source must be deleted after final quick checks." : "The source must be retained when deletion is not requested or required metadata is missing.");
    Assert(File.Exists(Path.Combine(destination, "01 - Test.flac")) && FileSha256(cover) == originalCoverHash,
        "The verified track must be committed while the existing user cover remains byte-identical.");
    Assert(File.Exists(source) != expectedDeletion && File.Exists(cue) && File.Exists(result.ReportPath),
        "The source disposition did not match the requested delete-originals option; the CUE and final report must remain.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == (requiredMetadataMissing ? "incomplete" : "passed") &&
           summary.Tracks == 1 && summary.Deleted == expectedDeletion,
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
    if (optionalMetadataMissing)
    {
        var verification = finalReport.RootElement.GetProperty("verification");
        Assert(verification.GetProperty("status").GetString() == "passed" &&
               finalReport.RootElement.GetProperty("work_status").GetString() == "complete" &&
               verification.GetProperty("warnings").EnumerateArray().Count() == 3,
            "Missing LABEL, BARCODE, and RELEASECOUNTRY must remain informational warnings without blocking complete status or source deletion.");
    }
    if (requiredMetadataMissing)
    {
        var verification = finalReport.RootElement.GetProperty("verification");
        Assert(result.Incomplete && result.IncompleteKind == CompletionIssueKind.RequiredMetadata &&
               verification.GetProperty("incomplete_kind").GetString() == "required_metadata_missing" &&
               summary.Headline.Contains("required metadata missing", StringComparison.OrdinalIgnoreCase),
            "Missing required metadata must retain the source and expose a specific required-metadata label instead of a generic incomplete label.");
    }
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
      "verification": { "status": "pending", "sources_deleted": false, "errors": [], "missing_metadata": ["COVER"] }
    }
    """);
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length);
    var staged = new StagedJob(job, stagedAlbum, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());

    Assert(result.Tracks == 1 && result.Incomplete && !result.SourcesDeleted &&
           result.IncompleteKind == CompletionIssueKind.CoverArtwork,
        "Missing artwork must use the explicit cover-artwork issue kind, deliver tracks as incomplete work, and retain the source.");
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
    var stereoArguments = LocalDsdProcessor.ExtractionArguments(true, "album.iso", "stereo-output");
    var multichannelArguments = LocalDsdProcessor.ExtractionArguments(false, "album.iso", "multichannel-output");
    Assert(stereoArguments.SequenceEqual(["-2", "-s", "-c", "-i", "album.iso", "-y", "stereo-output"]) &&
           multichannelArguments.SequenceEqual(["-m", "-s", "-c", "-i", "album.iso", "-y", "multichannel-output"]),
        "DSF extraction must use sacd_extract's -y DSF output-directory option for both stereo and multichannel areas; -o produces an incorrect single output for some non-DST SACDs.");

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

    const string missingDiscText = """
    Album Information:
        Album Catalog Number: 5914552
    Disc Information:
        Disc Catalog Number: 5914552
        Creation date: 2003-07-14
    Area count: 1
        Area Information [0]:
        Track Count: 2
        Total play time: 15:44:62 [mins:secs:frames]
        Speaker config: 2 Channel
        Duration: 08:02:73 [mins:secs:frames]
        Duration: 07:39:64 [mins:secs:frames]
    """;
    var structural = LocalDsdProcessor.ParseLayout(missingDiscText);
    Assert(structural.AlbumTitle.Length == 0 && structural.AlbumArtist.Length == 0 &&
           structural.Areas[0].Tracks.All(track => track.Title.Length == 0),
        "A structurally valid SACD without disc text must reach fallback resolution instead of failing during layout parsing.");
    var albumRoot = Path.Combine("X:\\Rock", "1988 - Spirit Of Eden (2003 Remaster SACD-R)");
    var scan = new ScanResult(albumRoot, Path.GetFileName(albumRoot), WorkflowMode.DsdExtraction,
        [new(Path.Combine(albumRoot, "Talk Talk - Spirit Of Eden.md5"), "Talk Talk - Spirit Of Eden.md5", "Provenance", 100, "checksum")],
        [], [], 0, 1, 0, 0, false, true);
    var localIdentity = LocalDsdProcessor.ResolveLocalIdentity(scan, structural);
    Assert(localIdentity.ChecksumArtist == "Talk Talk" && localIdentity.ChecksumAlbum == "Spirit Of Eden" &&
           localIdentity.FolderAlbum == "Spirit Of Eden",
        "Checksum filename must outrank the folder as the strongest local artist/title fallback while the folder supplies corroborating title evidence.");
    var catalogIdentity = new ExternalAlbumIdentity("Spirit of Eden", "Talk Talk", "5914552", "2003-07-14",
        "https://musicbrainz.org/release/spirit-release", ["The Rainbow", "Eden"]);
    var identified = LocalDsdProcessor.ApplyAlbumIdentity(structural, localIdentity, catalogIdentity);
    var emptyExternal = new ExternalAlbumMetadata(null, null, null, null, null, null, null, null, null, null,
        [], [], null, null, []);
    var completed = LocalDsdProcessor.ApplyExternalTrackListing(identified, catalogIdentity, emptyExternal);
    Assert(completed.AlbumArtist == "Talk Talk" && completed.AlbumTitle == "Spirit of Eden" &&
           completed.Areas[0].Tracks.Select(track => track.Title).SequenceEqual(["The Rainbow", "Eden"]) &&
           completed.Areas[0].Tracks.All(track => track.Performer == "Talk Talk"),
        "Exact catalog identity and its count-matching external track listing must complete missing SACD disc text deterministically.");
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

static async Task ExternalCatalogIdentityResolvesMissingSacdDiscText()
{
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
            {"releases":[{"id":"spirit-release","score":100,"title":"Spirit of Eden","artist-credit":[{"name":"Talk Talk"}],"release-group":{"id":"spirit-group"},"date":"2003-07-14","track-count":4,"media":[{"position":1,"format":"Hybrid SACD (CD layer)","track-count":2},{"position":2,"format":"Hybrid SACD (SACD layer, 2 channels)","track-count":2}],"label-info":[{"catalog-number":"7243 5 91455 2 8","label":{"name":"Parlophone"}}]}]}
            """);
        if (uri.Contains("/ws/2/release/spirit-release?inc=recordings", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
            {"media":[{"position":1,"format":"Hybrid SACD (CD layer)","track-count":2,"tracks":[{"title":"CD-layer Rainbow"},{"title":"CD-layer Eden"}]},{"position":2,"format":"Hybrid SACD (SACD layer, 2 channels)","track-count":2,"tracks":[{"title":"The Rainbow"},{"title":"Eden"}]}]}
            """);
        if (uri.Contains("/ws/2/release-group/spirit-group", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"first-release-date":"1988-09-16","genres":[],"tags":[],"relations":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected catalog identity request: {uri}");
    }));
    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var identity = await service.ResolveIdentityByCatalogAsync("5914552", 2, 2003);
    Assert(identity is not null && identity.Album == "Spirit of Eden" && identity.Artist == "Talk Talk" &&
           identity.CatalogNumber == "7243 5 91455 2 8" && identity.TrackTitles.SequenceEqual(["The Rainbow", "Eden"]) &&
           identity.MusicBrainzReleaseId == "spirit-release",
        "An unambiguous physical-disc catalog substring and matching hybrid-SACD medium must resolve the full release identity, SACD-layer track titles, and cover-release identifier without rejecting the release-level combined track count.");
    var metadata = await service.ResolveAsync(
        new("Spirit of Eden", "Talk Talk", 2, 1988, 2003, "5914552"),
        includeTrackTitles: true);
    Assert(metadata.MusicBrainzReleaseId == "spirit-release" &&
           metadata.TrackTitles.SequenceEqual(["The Rainbow", "Eden"]),
        "General external matching must retain the exact hybrid-SACD release ID for cover lookup and select only its SACD medium track list.");
}

static async Task ExternalMetadataImportsExactTrackComposersAndCuratedGenre()
{
    using (var client = new HttpClient(new StubHttpHandler((request, _) =>
           {
               var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
               if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""
                   {"releases":[{"id":"4e5f4f05-8a3c-4bcd-8bee-848180cbbeeb","score":100,"title":"Der Klang der Offenbarung des Göttlichen","artist-credit":[{"name":"Kjartan Sveinsson"}],"release-group":{"id":"klang-group"},"date":"2016-10-20","country":"XW","media":[{"position":1,"format":"Digital Media","track-count":4}]}]}
                   """);
               if (uri.Contains("/ws/2/release-group/klang-group", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"first-release-date":"2016-10-20","genres":[],"tags":[],"relations":[]}""");
               if (uri.Contains("/ws/2/release/4e5f4f05-8a3c-4bcd-8bee-848180cbbeeb", StringComparison.Ordinal) &&
                   uri.Contains("recording-level-rels", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""
                   {"media":[{"position":1,"format":"Digital Media","track-count":4,"tracks":[
                     {"position":1,"title":"Der Klang der Offenbarung des Göttlichen: Teil I","recording":{"relations":[{"type":"performance","work":{"relations":[{"type":"composer","artist":{"name":"Kjartan Sveinsson"}}]}}]}},
                     {"position":2,"title":"Der Klang der Offenbarung des Göttlichen: Teil II","recording":{"relations":[{"type":"performance","work":{"relations":[{"type":"composer","artist":{"name":"Kjartan Sveinsson"}}]}}]}},
                     {"position":3,"title":"Der Klang der Offenbarung des Göttlichen: Teil III","recording":{"relations":[{"type":"performance","work":{"relations":[{"type":"composer","artist":{"name":"Kjartan Sveinsson"}}]}}]}},
                     {"position":4,"title":"Der Klang der Offenbarung des Göttlichen: Teil IV","recording":{"relations":[{"type":"performance","work":{"relations":[{"type":"composer","artist":{"name":"Kjartan Sveinsson"}}]}}]}}
                   ]}]}
                   """);
               if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"results":[]}""");
               throw new InvalidOperationException($"Unexpected composer metadata request: {uri}");
           })))
    {
        var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
        var metadata = await service.ResolveAsync(
            new("Der Klang Der Offenbarung Des Göttlichen", "Kjartan Sveinsson", 4, RequireSacd: false,
                TrackTitleHints: ["Teil I", "Teil II", "Teil III", "Teil IV"]),
            includeTrackTitles: true);
        Assert(metadata.TrackComposers.SequenceEqual(Enumerable.Repeat("Kjartan Sveinsson", 4)) &&
               metadata.TrackTitles.Count == 4,
            $"An exact MusicBrainz release must import per-track composers through recording-to-work relationships, while qualified external titles remain aligned with short local titles. Titles={string.Join("|", metadata.TrackTitles)}; composers={string.Join("|", metadata.TrackComposers)}; warnings={string.Join("|", metadata.Warnings)}");
    }

    using (var client = new HttpClient(new StubHttpHandler((request, _) =>
           {
               var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
               if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""
                   {"releases":[{"id":"6996d8df-7ab3-4b33-b3c2-67af7d45955f","score":100,"title":"Cash Is King","artist-credit":[{"name":"Bee MC"}],"release-group":{"id":"cash-group"},"date":"2016-01-21","country":"XW","media":[{"format":"Digital Media","track-count":15}]}]}
                   """);
               if (uri.Contains("/ws/2/release-group/cash-group", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"first-release-date":"2016-01-21","genres":[],"tags":[],"relations":[]}""");
               if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"results":[]}""");
               throw new InvalidOperationException($"Unexpected curated-genre metadata request: {uri}");
           })))
    {
        var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
        var metadata = await service.ResolveAsync(new("Cash Is King", "Bee MC", 15, RequireSacd: false));
        Assert(metadata.Genre == "Blues" && metadata.GenreSourceType == "curated_exact_release" &&
               metadata.Sources.Contains("https://musify.club/release/bee-mc-cash-is-king-2016-793083"),
            "The reviewed Bee MC genre may apply only after the exact MusicBrainz release ID is established, with its public-catalog provenance retained.");
    }

    using (var client = new HttpClient(new StubHttpHandler((request, _) =>
           {
               var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
               if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"releases":[{"id":"d1e3684c-549d-441d-a188-ec4e06ba12f0","score":100,"title":"Hollie Stephenson","artist-credit":[{"name":"Hollie Stephenson"}],"release-group":{"id":"hollie-group"},"date":"2016-04-21","country":"XW","media":[{"format":"Digital Media","track-count":12}]}]}""");
               if (uri.Contains("/ws/2/release-group/hollie-group", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"first-release-date":"2016-04-21","genres":[],"tags":[],"relations":[]}""");
               if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
                   return StubHttpHandler.Json("""{"results":[{"artistName":"Hollie Stephenson","collectionName":"Hollie Stephenson","primaryGenreName":"Vocal","releaseDate":"2016-05-06T00:00:00Z","trackCount":13,"collectionViewUrl":"https://music.apple.com/album/hollie"}]}""");
               throw new InvalidOperationException($"Unexpected Hollie Stephenson metadata request: {uri}");
           })))
    {
        var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
        var metadata = await service.ResolveAsync(new("Hollie Stephenson", "Hollie Stephenson", 12, RequireSacd: false));
        Assert(metadata.Genre == "Pop" && metadata.GenreSourceType == "curated_exact_release" &&
               metadata.Sources.Contains("https://www.muziekweb.nl/en/Link/JK207876/Hollie-Stephenson-bonus-track"),
            "The exact 12-track Society of Sound release needs reviewed genre provenance because the 13-track Apple edition must remain ineligible.");
    }
}

static async Task ExternalMetadataReadsEmbeddedDiscogsComposerCredits()
{
    var localTitles = new[]
    {
        "Turandot: Act 3 - \"Nessun dorma!\"",
        "La Bohème: Act 1 - \"Che gelida manina\"",
        "Rigoletto: Act 2 - \"Parmi veder le lagrime\"",
        "Rigoletto: Act 3 - \"La donna e mobile\"",
        "L'elisir d'amore: Act 2 - \"Una furtiva lagrima\"",
        "Carmen: Act 2 - \"La fleur que tu m'avais jetee\"",
        "Les Pecheurs des Perles: Act 1 - \"Au fond du temple saint\"",
        "Pagliacci: Act 1 - \"Vesti la giubba\"",
        "Tosca: Act 1 - \"Recondita armonia\"",
        "Tosca: Act 3 - \"E lucevan le stelle\"",
        "Il Trovatore: Act 3 - \"Di quella pira\"",
        "Aida: Act 1 - \"Celeste Aida\"",
        "La Bohème: Act 1 - \"O soave fanciulla\"",
        "Martha: Act 3 - \"M'appari\"",
        "Messa da Requiem - Ingemisco",
        "'O sole mio",
        "Funiculì, funicula",
        "Torna a Surriento",
        "Mattinata",
        "Caro mio ben",
        "Soirees musicales: La Danza",
        "Malinconia, ninfa gentile",
        "Ma rendi pur contento",
        "La Serenata"
    };
    var discogsTitles = new[]
    {
        "Turandot / Act 3 (Giacomo Puccini) Nessun Dorma!",
        "La Bohème / Act 1 (Giacomo Puccini) Che Gelida Manina",
        "Rigoletto / Act 2 (Giuseppe Verdi) Parmi Veder Le Lagrime",
        "Rigoletto / Act 3 (Giuseppe Verdi) La Donna è Mobile",
        "L'elisir D'amore / Act 2 (Gaetano Donizetti) Una Furtiva Lagrima",
        "Carmen / Act 2 (Georges Bizet) La Fleur Que Tu M'avais Jetee",
        "Les Pecheurs des Perles / Act 1 (Georges Bizet) Au Fond Du Temple Saint",
        "Pagliacci / Act 1 (Ruggiero Leoncavallo) Vesti La Giubba",
        "Tosca / Act 1 (Giacomo Puccini) Recondita Armonia",
        "Tosca / Act 3 (Giacomo Puccini) E Lucevan Le Stelle",
        "Il Trovatore / Act 3 (Giuseppe Verdi) Di Quella Pira",
        "Aida / Act 1 (Giuseppe Verdi) Celeste Aida",
        "La Bohème / Act 1 (Giacomo Puccini) O Soave Fanciulla",
        "Martha / Act 3 (Friedrich Von Flotow) M'appari",
        "Messa Da Requiem (Giuseppe Verdi) 2h. Ingemisco",
        "Di Capua, Mazzucchi: 'O Sole Mio",
        "Denza: Funiculì, Funiculà",
        "Curtis: Torna A Surriento",
        "Leoncavallo: Mattinata",
        "Giordani: Caro Mio Ben",
        "Soirées Musicales (Gioachino Rossini) La Danza",
        "Bellini: Malinconia, Ninfa Gentile",
        "Bellini: Ma Rendi Pur Contento",
        "Tosti: La Serenata"
    };
    var expectedComposers = new[]
    {
        "Giacomo Puccini", "Giacomo Puccini", "Giuseppe Verdi", "Giuseppe Verdi",
        "Gaetano Donizetti", "Georges Bizet", "Georges Bizet", "Ruggiero Leoncavallo",
        "Giacomo Puccini", "Giacomo Puccini", "Giuseppe Verdi", "Giuseppe Verdi",
        "Giacomo Puccini", "Friedrich Von Flotow", "Giuseppe Verdi", "Di Capua, Mazzucchi",
        "Denza", "Curtis", "Leoncavallo", "Giordani", "Gioachino Rossini", "Bellini", "Bellini", "Tosti"
    };
    Assert(localTitles.Zip(discogsTitles).All(pair =>
            ExternalMetadataService.TrackTitlesEquivalent(pair.First, pair.Second)),
        "Composer text embedded inside the exact Discogs title must not make the ordered Pavarotti tracklist appear different from the local tagged titles.");

    var details = System.Text.Json.JsonSerializer.Serialize(new
    {
        title = "Pavarotti 24 Greatest HD Tracks",
        artists = new[] { new { name = "Luciano Pavarotti" } },
        released = "2013",
        genres = new[] { "Classical" },
        styles = new[] { "Opera" },
        labels = new[] { new { name = "Decca music Group", catno = "none" } },
        tracklist = discogsTitles.Select((title, index) => new
        {
            position = (index + 1).ToString(),
            type_ = "track",
            title
        }).ToArray()
    });
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("api.discogs.com/database/search", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
                {"results":[{"id":17330749,"title":"Luciano Pavarotti - Pavarotti 24 Greatest HD Tracks","year":2013,"format":["File","AIFF","Compilation"]}]}
                """);
        if (uri.Contains("api.discogs.com/releases/17330749", StringComparison.Ordinal))
            return StubHttpHandler.Json(details);
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected Pavarotti metadata request: {uri}");
    }));
    var service = new ExternalMetadataService(client, discogsMinimumInterval: TimeSpan.Zero,
        musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var metadata = await service.ResolveAsync(
        new("Pavarotti 24 Greatest HD Tracks", "Luciano Pavarotti", 24, OriginalYear: 2013,
            RequireSacd: false, TrackTitleHints: localTitles),
        includeTrackTitles: true);

    Assert(metadata.TrackTitles.SequenceEqual(discogsTitles) &&
           metadata.TrackComposers.SequenceEqual(expectedComposers) &&
           metadata.TrackComposerSourceType == "discogs_exact_release_tracklist_credit" &&
           metadata.Sources.Contains("https://www.discogs.com/release/17330749"),
        $"The exact verified Discogs release must supply all 24 embedded and leading composer credits. " +
        $"Tracks={metadata.TrackTitles.Count}; composers={string.Join("|", metadata.TrackComposers)}; warnings={string.Join("|", metadata.Warnings)}");
}

static async Task ExternalMetadataAlignsAbbreviatedOperaTitlesWithCredits()
{
    var localTitles = new[]
    {
        "Carmen, Act 1: \"L'amour est un oiseau rebelle\" (Carmen, Chorus) [Habanera]",
        "Norma, Act 1: \"Casta diva\" (Norma, Chorus)",
        "Gianni Schicchi, Act 1: \"O mio babbino caro\" (Lauretta)",
        "La Wally, Act 1: \"Ebben?...Ne andrò lontana\" (Wally)",
        "La Traviata, Act 1: \"Ah fors'e lui\" (Violetta)",
        "La Traviata, Act 1: \"Sempre libera\" (Violetta, Alfredo)",
        "Tosca, Act 2: \"Vissi d'arte\" (Tosca)",
        "Madama Butterfly, Act 2: \"Un bel di vedremo\" (Butterfly)",
        "Andrea Chénier, Act 3: \"La mamma morta\" (Maddalena)",
        "La Bohème, Act 3: \"Donde lieta uscì al tuo grido d'amore\" (Mimì)",
        "Adriana Lecouvreur, Act 1: \"Ecco: respiro appena...Io son l'umile ancella\" (Adriana Lecouvreur)",
        "Lucia di Lammermoor, Act 3: \"Il dolce suono ... Ardon gli incensi\" (Lucia, Raimondo, Normanno, Chorus)",
        "Il Trovatore, Act 4: \"D'amor sull'ali rosee\" (Leonora)",
        "Otello, Act 4: \"Ave Maria\" (Desdemona)",
        "Il Barbiere di Siviglia, Act 1: \"Una voce poco fa\" (Rosina)",
        "Orphée et Eurydice, Act 4: \"J'ai perdu mon Eurydice\" (Orfeo)",
        "Samson et Dalila, Act 2: \"Mon coeur s'ouvre à ta voix\" (Dalila)",
        "Carmen, Act 2: \"Les tringles des sistres tintaient\" (Camen, Frasquita, Mercédès)"
    };
    var discogsTitles = new[]
    {
        "Habanera (Carmen)",
        "Casta Diva (Norma)",
        "O Mio Babbino Caro (Gianni Schicchi)",
        "Ebben? Ne Andrò Lontana (La Wally)",
        "Ah, Fors'è Lui (La Traviata)",
        "Sempre Libera (La Traviata)",
        "Viddi D'Arte (Tosca)",
        "Un Bel Di Vedremo (Madame Butterfly)",
        "La Mamma Morta (Andrea Chénier)",
        "Donde Lieta Usci (La Bohème)",
        "Io Son L'Umile Ancella (Adriana Lecouvreur)",
        "Il Dolce Suono (Luca Di Lammermoor)",
        "D'Amor Sull'ali Rosee (Il Trovatore)",
        "Ave Maria (Otello)",
        "Una Voce Poco Fa (Il Barbiere Di Siviglia)",
        "J'Ai Perdu Mon Eurydice (Orphée Et Eurydice)",
        "Mon Coeur S'Ouvre A Ta Voix (Samson Et Delila)",
        "Chanson Boheme (Carmen)"
    };
    var composers = new[]
    {
        "Georges Bizet", "Vincenzo Bellini", "Giacomo Puccini", "Alfredo Catalani",
        "Giuseppe Verdi", "Giuseppe Verdi", "Giacomo Puccini", "Giacomo Puccini",
        "Umberto Giordano", "Giacomo Puccini", "Francesco Cilea", "Gaetano Donizetti",
        "Giuseppe Verdi", "Giuseppe Verdi", "Gioacchino Rossini", "Christoph Willibald Gluck",
        "Camille Saint-Saëns", "Georges Bizet"
    };
    var matchingTracks = localTitles.Zip(discogsTitles)
        .Select((pair, index) => new
        {
            Index = index + 1,
            Matches = ExternalMetadataService.TrackTitlesEquivalent(pair.First, pair.Second)
        })
        .Where(value => value.Matches)
        .Select(value => value.Index)
        .ToArray();
    Assert(matchingTracks.SequenceEqual(Enumerable.Range(1, 17)),
        $"The rich local opera titles must align with the 17 abbreviated Discogs titles while preserving the one genuine alias mismatch. Matches={string.Join(",", matchingTracks)}");

    string Details(IReadOnlyList<string> titles) => System.Text.Json.JsonSerializer.Serialize(new
    {
        title = "Pure Maria Callas",
        artists = new[] { new { name = "Maria Callas" } },
        released = "2014",
        genres = new[] { "Classical" },
        styles = new[] { "Opera" },
        labels = new[] { new { name = "Warner Classics", catno = "none" } },
        tracklist = titles.Select((title, index) => new
        {
            position = (index + 1).ToString(),
            type_ = "track",
            title,
            extraartists = new[] { new { name = composers[index], role = "Composed By" } }
        }).ToArray()
    });

    ExternalMetadataService Service(string details)
    {
        var client = new HttpClient(new StubHttpHandler((request, _) =>
        {
            var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
            if (uri.Contains("api.discogs.com/database/search", StringComparison.Ordinal))
                return StubHttpHandler.Json("""
                    {"results":[{"id":32440695,"title":"Maria Callas - Pure Maria Callas","year":2014,"format":["File","FLAC","Reissue","Remastered"]}]}
                    """);
            if (uri.Contains("api.discogs.com/releases/32440695", StringComparison.Ordinal))
                return StubHttpHandler.Json(details);
            if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
                return StubHttpHandler.Json("""{"releases":[]}""");
            if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
                return StubHttpHandler.Json("""{"results":[]}""");
            throw new InvalidOperationException($"Unexpected Maria Callas metadata request: {uri}");
        }));
        return new(client, discogsMinimumInterval: TimeSpan.Zero,
            musicBrainzMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    }

    var metadata = await Service(Details(discogsTitles)).ResolveAsync(
        new("Pure - Maria Callas", "Maria Callas", 18, OriginalYear: 2014, RequireSacd: false,
            TrackTitleHints: localTitles, AlbumTitleHints: ["Maria Callas - Pure (2014) [96-24]"]),
        includeTrackTitles: true);
    Assert(metadata.TrackTitles.SequenceEqual(discogsTitles) &&
           metadata.TrackComposers.SequenceEqual(composers) &&
           metadata.TrackComposerSourceType == "discogs_exact_release_tracklist_credit" &&
           metadata.Sources.Contains("https://www.discogs.com/release/32440695"),
        $"The exact artist/title/year/count match plus 17 ordered titles must admit the single catalog alias and import all composer credits. " +
        $"Tracks={metadata.TrackTitles.Count}; composers={string.Join("|", metadata.TrackComposers)}; warnings={string.Join("|", metadata.Warnings)}");

    var twoMismatches = discogsTitles.ToArray();
    twoMismatches[16] = "Unrelated Selection";
    var rejected = await Service(Details(twoMismatches)).ResolveAsync(
        new("Pure - Maria Callas", "Maria Callas", 18, OriginalYear: 2014, RequireSacd: false,
            TrackTitleHints: localTitles), includeTrackTitles: true);
    Assert(rejected.TrackComposers.Count == 0 &&
           !rejected.Sources.Contains("https://www.discogs.com/release/32440695"),
        "An exact album identity must still fail closed when more than one ordered Discogs title disagrees with local evidence.");
}

static async Task ExternalMetadataFallsBackFromFolderTitleToLinkedDiscogsTrackCredits()
{
    var localTitles = new[]
    {
        "Brandenburg Concerto No 1 in F Major BWV 1046 I",
        "Les nuits d ete Op 7 I Villanelle",
        "Piano Concerto No 2 in F Minor Op 21 III Allegro vivace",
        "Requiem in D Minor K 626 IX Domine Jesu",
        "Sonata in D Minor Op 5 No 7 III Sarabanda Largo",
        "La vera costanza Hob 28 8 Gia la morte in mante nero",
        "7 Fantasien Op 116 No 2 Intermezzo in A Minor",
        "L enfance du Christ Partie II La fuite en Egypte L adieu des bergers",
        "Johannes Passion BWV 245 Aria Ich folge dir gleichfalls mit freudigen Schritten",
        "Les Gentils Airs - ou Airs Connus ajustee en duo pour bassoon seul accompagne d un clavecin Les Sauvages",
        "Pargoletta che non sai",
        "Set a5 in g II On the Playnsong a5",
        "Ave Maria",
        "Flute Concerto IV Scherzo",
        "Holding Back",
        "Your Very Soul",
        "What ll I Do",
        "Sleeping Horses"
    };
    var composers = new[]
    {
        "J. S. Bach", "Berlioz", "Chopin", "Mozart", "Corelli", "Haydn", "Brahms", "Berlioz",
        "J. S. Bach", "Anonymous", "Rossi", "Lawes", "Parsons", "Rouse", "MacLean", "Duncan", "Berlin", "Barker"
    };
    var externalTitles = new[]
    {
        "J. S. Bach: Brandenburg Concerto No. 1 in F major, BWV 1046",
        "Berlioz: Les Nuits D'été",
        "Chopin: Piano Concerto No. 2 in F minor, Op. 21 - III. Allegro Vivace",
        "Mozart: Requiem in D minor, K. 626 - Domine Jesu",
        "Corelli: Sonata in D minor, Op. 5 No. 7 - III. Sarabanda - Largo",
        "Haydn: La Vera Costanza, Hob. 28/8 - Già La Morte In Manto Nero",
        "Brahms: 7 Fantasien, Op. 116 No. 2 - Intermezzo In A Minor",
        "Berlioz: L'enfance Du Christ - Partie II, La Fuite En Ègypte - L'adieu Des Bergers",
        "J. S. Bach: Johannes Passion, BWV 245 - Aria: Ich Folge Dir Gleichfalls Mit Freudigen Schritten",
        "Anon: Les Gentils Aírs - Ou Airs Connus, Ajustée En Duo, Pour Basson Seul Accompagné D'un Clavecin - Les Sauvages",
        "Rossi: Pargoletta, Che Non Sai",
        "Lawes: Set a5 In g - II. On the Playnsong: a5",
        "Parsons: Ave Maria",
        "Rouse: Flute Concerto - IV. Scherzo",
        "MacLean: Holding Back",
        "Duncan: Your Very Soul",
        "Berlin: What'll I Do?",
        "Barker: Sleeping Horses"
    };
    var titleMismatches = localTitles.Zip(externalTitles)
        .Select((pair, index) => new { Index = index + 1, Matches = ExternalMetadataService.TrackTitlesEquivalent(pair.First, pair.Second) })
        .Where(value => !value.Matches)
        .Select(value => value.Index)
        .ToArray();
    Assert(titleMismatches.Length == 0,
        $"The real Discogs Volume 7 titles must align conservatively with the local filename titles. Mismatched tracks={string.Join(",", titleMismatches)}");
    var discogsDetails = System.Text.Json.JsonSerializer.Serialize(new
    {
        title = "Super Audio Collection Vol. 7",
        artists = new[] { new { name = "Various" } },
        genres = new[] { "Classical" },
        labels = new[] { new { name = "Linn Records", catno = "AKP 459" } },
        tracklist = externalTitles.Select((title, index) => new { position = (index + 1).ToString(), type_ = "track", title }).ToArray()
    });
    var musicBrainzTracks = System.Text.Json.JsonSerializer.Serialize(new
    {
        media = new[]
        {
            new
            {
                position = 1,
                format = "Digital Media",
                track_count = 18,
                tracks = externalTitles.Select((title, index) => new
                {
                    position = index + 1,
                    title,
                    recording = new { relations = Array.Empty<object>() }
                }).ToArray()
            }
        }
    }).Replace("track_count", "track-count", StringComparison.Ordinal);
    var titleOnlyFallbackRequested = false;
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var decoded = Uri.UnescapeDataString(uri);
        if (uri.Contains("api.discogs.com/database/search", StringComparison.Ordinal))
        {
            if (!decoded.Contains("Super Audio Collection Vol. 7", StringComparison.OrdinalIgnoreCase))
                return StubHttpHandler.Json("""{"results":[]}""");
            return StubHttpHandler.Json("""
            {"results":[{"id":8900116,"title":"Various - Super Audio Collection Vol. 7","year":"2014","format":["SACD","Hybrid","Compilation"]}]}
            """);
        }
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
        {
            var correctTitle = decoded.Contains("Super Audio Collection Vol. 7", StringComparison.OrdinalIgnoreCase);
            var hasArtistConstraint = decoded.Contains(" AND artist:", StringComparison.OrdinalIgnoreCase);
            if (!correctTitle || hasArtistConstraint) return StubHttpHandler.Json("""{"releases":[]}""");
            titleOnlyFallbackRequested = true;
            return StubHttpHandler.Json("""
            {"releases":[{"id":"linn-vol7","score":100,"title":"Super Audio Collection Vol. 7","artist-credit":[{"name":"Various Artists"}],"release-group":{"id":"linn-vol7-group"},"date":"2014","country":"XE","barcode":"691062045926","media":[{"position":1,"format":"Digital Media","track-count":18}],"label-info":[{"catalog-number":"AKP 459","label":{"name":"Linn Records"}}]}]}
            """);
        }
        if (uri.Contains("/ws/2/release-group/linn-vol7-group", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"first-release-date":"2014","genres":[{"name":"classical","count":10}],"tags":[],"relations":[{"type":"discogs","url":{"resource":"https://www.discogs.com/release/8900116"}}]}""");
        if (uri.Contains("/ws/2/release/linn-vol7", StringComparison.Ordinal) &&
            uri.Contains("recording-level-rels", StringComparison.Ordinal))
            return StubHttpHandler.Json(musicBrainzTracks);
        if (uri.Contains("api.discogs.com/releases/8900116", StringComparison.Ordinal))
            return StubHttpHandler.Json(discogsDetails);
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected Linn fallback request: {uri}");
    }));

    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero,
        requestTimeout: TimeSpan.FromSeconds(1));
    var metadata = await service.ResolveAsync(
        new("Incorrect Embedded Album", "Linn Records", 18, RequireSacd: false,
            TrackTitleHints: localTitles,
            AlbumTitleHints: ["Linn Records - The Super Audio Collection Volume 7"]),
        includeTrackTitles: true);

    Assert(titleOnlyFallbackRequested && metadata.MusicBrainzReleaseId == "linn-vol7" &&
           metadata.CatalogNumber == "AKP 459" && metadata.TrackTitles.Count == 18,
        $"Folder-derived title-only fallback must identify the exact release only after full ordered-track verification. " +
        $"Requested={titleOnlyFallbackRequested}; MBID={metadata.MusicBrainzReleaseId}; catalog={metadata.CatalogNumber}; " +
        $"tracks={metadata.TrackTitles.Count}; sources={string.Join("|", metadata.Sources)}; warnings={string.Join("|", metadata.Warnings)}");
    Assert(metadata.TrackComposers.SequenceEqual(composers) &&
           metadata.TrackComposerSourceType == "discogs_exact_release_tracklist_credit" &&
           metadata.Sources.Contains("https://www.discogs.com/release/8900116"),
        $"The verified public Discogs tracklist must supply aligned composer prefixes and provenance without requiring a token. Composers={string.Join("|", metadata.TrackComposers)}");

    var mismatchedTitles = externalTitles.ToArray();
    mismatchedTitles[7] = "Berlioz: A different selection";
    var mismatchedTracks = System.Text.Json.JsonSerializer.Serialize(new
    {
        media = new[]
        {
            new
            {
                position = 1,
                format = "Digital Media",
                track_count = 18,
                tracks = mismatchedTitles.Select((title, index) => new
                {
                    position = index + 1,
                    title,
                    recording = new { relations = Array.Empty<object>() }
                }).ToArray()
            }
        }
    }).Replace("track_count", "track-count", StringComparison.Ordinal);
    using var mismatchClient = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        var decoded = Uri.UnescapeDataString(uri);
        if (uri.Contains("/ws/2/release/?", StringComparison.Ordinal))
        {
            if (decoded.Contains(" AND artist:", StringComparison.OrdinalIgnoreCase) ||
                !decoded.Contains("Super Audio Collection", StringComparison.OrdinalIgnoreCase))
                return StubHttpHandler.Json("""{"releases":[]}""");
            return StubHttpHandler.Json("""
            {"releases":[{"id":"wrong-selection","score":100,"title":"Super Audio Collection Vol. 7","artist-credit":[{"name":"Various Artists"}],"release-group":{"id":"wrong-selection-group"},"media":[{"position":1,"format":"Digital Media","track-count":18}]}]}
            """);
        }
        if (uri.Contains("/ws/2/release-group/wrong-selection-group", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"genres":[],"tags":[],"relations":[]}""");
        if (uri.Contains("/ws/2/release/wrong-selection", StringComparison.Ordinal))
            return StubHttpHandler.Json(mismatchedTracks);
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected mismatched Linn fallback request: {uri}");
    }));
    var mismatchService = new ExternalMetadataService(mismatchClient, musicBrainzMinimumInterval: TimeSpan.Zero,
        requestTimeout: TimeSpan.FromSeconds(1));
    var rejected = await mismatchService.ResolveAsync(
        new("Incorrect Embedded Album", "Linn Records", 18, RequireSacd: false,
            TrackTitleHints: localTitles,
            AlbumTitleHints: ["Linn Records - The Super Audio Collection Volume 7"]),
        includeTrackTitles: true);
    Assert(rejected.MusicBrainzReleaseId is null && rejected.TrackTitles.Count == 0 && rejected.TrackComposers.Count == 0,
        "A folder-name-only album candidate must fail closed when even one ordered external track title disagrees with local tag/filename evidence.");
}

static async Task ExternalMetadataAlignsPartialMixedCompilation()
{
    var localTitles = new[]
    {
        "Blackwood", "Man in The Station", "Makin' Whoopee", "Haven't We Met", "Johnny and Mary",
        "She’s Turning", "Trouble In Mind", "Bad News On The Mountain", "Navigating", "Ca’ The Yowes",
        "When The Sunny Sky Has Gone", "With Every Breath I Take", "Certain Smile", "Johnny Come Lately",
        "Sittin’ and a Rockin’", "I Thought About You", "Happy This Way", "A Case of You", "No Surrender",
        "Love Go Round", "That's Amore", "Symphony No. 40 in G minor, K 550, I. Molto Allegro",
        "Grandes Etudes de Paganini – Etude III"
    };
    var fullTitles = localTitles.Concat(Enumerable.Range(24, 17).Select(number => $"Classical selection {number}"))
        .ToArray();
    var fullComposers = Enumerable.Repeat(string.Empty, fullTitles.Length).ToArray();
    fullComposers[21] = "Wolfgang Amadeus Mozart";
    fullComposers[22] = "Franz Liszt";
    string[][] localArtists =
    [
        ["Emily Barker & The Red Clay Halo"], ["Claire Martin"], ["Hue & Cry"], ["Carol Kidd"],
        ["Martin Taylor"], ["The McCluskey Brothers"], ["Barb Jungr"], ["Jon Strong"], ["Amy Duncan"],
        ["Ian Bruce"], ["Fiona MacKenzie (2)"], ["Claire Martin", "Richard Rodney Bennett"], ["Martin Taylor"],
        ["Tommy Smith"], ["Gill Manly"], ["Martin Taylor", "Stéphane Grappelli"], ["Judith Owen"],
        ["Ian Shaw (2)"], ["Maeve O'Boyle"], ["Sarah Moule"], ["Ray Gelato Giants"],
        ["Sir Charles Mackerras", "Scottish Chamber Orchestra"], ["George-Emmanuel Lazaridis"]
    ];
    var fullArtists = localArtists.Concat(Enumerable.Range(24, 17).Select(number => new[] { $"Classical Artist {number}" }))
        .ToArray();
    static object Track(string title, int index, string composer, IReadOnlyList<string> artists) => new
    {
        position = index < 21 ? $"1-{index + 1}" : $"2-{index - 20}",
        type_ = "track",
        title,
        artists = artists.Select(name => new { name }).ToArray(),
        extraartists = string.IsNullOrWhiteSpace(composer)
            ? Array.Empty<object>()
            : new object[] { new { name = composer, role = "Composed By" } }
    };
    var vinylDetails = System.Text.Json.JsonSerializer.Serialize(new
    {
        title = "Linn 40th Anniversary Collection",
        artists = new[] { new { name = "Various" } },
        genres = new[] { "Jazz", "Classical" },
        tracklist = localTitles.Take(21).Select((title, index) => Track(title, index, string.Empty, localArtists[index])).ToArray()
    });
    const string primaryImage = "https://i.discogs.com/test-primary-image.jpeg";
    var sacdDetails = System.Text.Json.JsonSerializer.Serialize(new
    {
        title = "Linn 40th Anniversary Collection",
        artists = new[] { new { name = "Various" } },
        genres = new[] { "Jazz", "Rock", "Pop", "Classical", "Folk, World, & Country" },
        labels = new[] { new { name = "Linn Records", catno = "AKD 425" } },
        images = new[] { new { type = "primary", uri = primaryImage, resource_url = "https://api.discogs.com/images/1" } },
        tracklist = fullTitles.Select((title, index) => Track(title, index, fullComposers[index], fullArtists[index])).ToArray()
    });
    using var client = new HttpClient(new StubHttpHandler((request, _) =>
    {
        var uri = request.RequestUri?.AbsoluteUri ?? string.Empty;
        if (uri.Contains("api.discogs.com/database/search", StringComparison.Ordinal))
            return StubHttpHandler.Json("""
            {"results":[
              {"id":5060214,"title":"Various - Linn 40th Anniversary Collection","year":"2013","format":["Vinyl","LP"]},
              {"id":25359721,"title":"Various - Linn 40th Anniversary Collection","year":"2013","format":["SACD","Compilation"]}
            ]}
            """);
        if (uri.Contains("api.discogs.com/releases/5060214", StringComparison.Ordinal))
            return StubHttpHandler.Json(vinylDetails);
        if (uri.Contains("api.discogs.com/releases/25359721", StringComparison.Ordinal))
            return StubHttpHandler.Json(sacdDetails);
        if (uri.Contains("musicbrainz.org", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"releases":[]}""");
        if (uri.Contains("itunes.apple.com", StringComparison.Ordinal))
            return StubHttpHandler.Json("""{"results":[]}""");
        throw new InvalidOperationException($"Unexpected partial-compilation request: {uri}");
    }));
    var service = new ExternalMetadataService(client, musicBrainzMinimumInterval: TimeSpan.Zero,
        discogsMinimumInterval: TimeSpan.Zero, requestTimeout: TimeSpan.FromSeconds(1));
    var metadata = await service.ResolveAsync(
        new("Linn 40th Anniversary Collection", "Various Artists", localTitles.Length, OriginalYear: 2013,
            RequireSacd: false, TrackTitleHints: localTitles,
            AlbumTitleHints: ["Linn 40th Anniversary Collection (2013) [192-24]"]),
        includeTrackTitles: true);

    Assert(metadata.Sources.Contains("https://www.discogs.com/release/25359721") &&
           !metadata.Sources.Contains("https://www.discogs.com/release/5060214") &&
           metadata.TrackTitles.Count == 23 && metadata.TrackComposers.Count == 23 &&
           metadata.TrackComposers[21] == "Wolfgang Amadeus Mozart" &&
           metadata.TrackComposers[22] == "Franz Liszt" &&
           metadata.TrackComposerSourceType == "discogs_exact_release_tracklist_credit" &&
           metadata.TrackArtists.Count == 23 &&
           metadata.TrackArtists[0] == "Emily Barker & The Red Clay Halo" &&
           metadata.TrackArtists[10] == "Fiona MacKenzie" &&
           metadata.TrackArtists[11] == "Claire Martin & Richard Rodney Bennett" &&
           metadata.TrackArtists[21] == "Sir Charles Mackerras & Scottish Chamber Orchestra" &&
           metadata.TrackArtistSourceType == "discogs_exact_release_track_artist_credit" &&
           metadata.ArtworkUrl == primaryImage &&
           metadata.ArtworkSourceType == "discogs_exact_release_primary_image",
        $"A unique 23-track prefix must select the 40-track SACD, retain aligned composers and track artists, and expose its primary cover. Sources={string.Join("|", metadata.Sources)}; composers={string.Join("|", metadata.TrackComposers)}; artists={string.Join("|", metadata.TrackArtists)}; artwork={metadata.ArtworkUrl}");
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

    using (var client = new HttpClient(new StubHttpHandler((request, _) =>
           request.RequestUri?.Host.Equals("i.discogs.com", StringComparison.OrdinalIgnoreCase) == true
               ? StubHttpHandler.Bytes(expected, "image/jpeg")
               : throw new InvalidOperationException("Unexpected Discogs cover request."))))
    {
        var service = new ExternalMetadataService(client, requestTimeout: TimeSpan.FromSeconds(1));
        var downloaded = await service.DownloadArtworkAsync("https://i.discogs.com/release-primary.jpeg");
        Assert(downloaded.Data.SequenceEqual(expected) && downloaded.MimeType == "image/jpeg" &&
               downloaded.Source.Contains("i.discogs.com", StringComparison.Ordinal),
            "A verified Discogs primary image must be returned in memory with MIME type and provenance.");
        Exception? untrusted = null;
        try { await service.DownloadArtworkAsync("https://example.com/not-discogs.jpeg"); }
        catch (Exception error) { untrusted = error; }
        Assert(untrusted is InvalidDataException, "External artwork must be restricted to trusted Discogs image URLs.");
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

static byte[] CreateRgbTiffWithoutRowsPerStrip()
{
    const ushort width = 2;
    const ushort height = 2;
    const int pixelOffset = 8;
    const int pixelBytes = width * height * 3;
    const int ifdOffset = pixelOffset + pixelBytes;
    const ushort entryCount = 9;
    const int bitsPerSampleOffset = ifdOffset + 2 + entryCount * 12 + 4;
    var bytes = new byte[bitsPerSampleOffset + 6];
    bytes[0] = (byte)'I';
    bytes[1] = (byte)'I';
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2, 2), 42);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), ifdOffset);
    bytes.AsSpan(pixelOffset, pixelBytes).Fill(0x70);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(ifdOffset, 2), entryCount);
    var entryOffset = ifdOffset + 2;
    WriteEntry(256, 3, 1, width);
    WriteEntry(257, 3, 1, height);
    WriteEntry(258, 3, 3, bitsPerSampleOffset);
    WriteEntry(259, 3, 1, 1);
    WriteEntry(262, 3, 1, 2);
    WriteEntry(273, 4, 1, pixelOffset);
    WriteEntry(277, 3, 1, 3);
    WriteEntry(279, 4, 1, pixelBytes);
    WriteEntry(284, 3, 1, 1);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsPerSampleOffset, 2), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsPerSampleOffset + 2, 2), 8);
    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bitsPerSampleOffset + 4, 2), 8);
    return bytes;

    void WriteEntry(ushort tag, ushort type, uint count, uint value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset, 2), tag);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryOffset + 2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 4, 4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(entryOffset + 8, 4), value);
        entryOffset += 12;
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

sealed class TestProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
