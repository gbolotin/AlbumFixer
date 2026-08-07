using System.Diagnostics;
using AlbumFixer.Core;

var root = Path.Combine(Path.GetTempPath(), "album-fixer-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ScannerClassifiesFlacCue(root);
    await ScannerPrefersExistingTracks(root);
    await ScannerBlocksMultipleAlbums(root);
    await PreflightFindsRunningCodex(root);
    await HostStagesAndVerifiesSource(root);
    await LocalSplitterRunsWithoutCodex(root);
    await HostCommitsVerifiedFlac(root);
    await HostCommitFailureRetainsSource(root);
    await FailureReportIsAlwaysWritten(root);
    await MetadataHandoffIsConditional(root);
    ProgressContractParses();
    DiagnosticContractClassifies();
    await ReportSummaryLoads(root);
    CommandContractIsSandboxed(root);
    Console.WriteLine("AlbumFixer.Core smoke tests passed (14/14).");
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

static async Task ScannerBlocksMultipleAlbums(string root)
{
    var folder = Path.Combine(root, "artist");
    var first = Path.Combine(folder, "Album One");
    var second = Path.Combine(folder, "Album Two");
    Directory.CreateDirectory(first); Directory.CreateDirectory(second);
    await File.WriteAllBytesAsync(Path.Combine(first, "album.flac"), [1]);
    await File.WriteAllTextAsync(Path.Combine(first, "album.cue"), "FILE \"album.flac\" WAVE");
    await File.WriteAllBytesAsync(Path.Combine(second, "album.flac"), [2]);
    await File.WriteAllTextAsync(Path.Combine(second, "album.cue"), "FILE \"album.flac\" WAVE");
    var result = await new AlbumScanner().ScanAsync(folder);
    Assert(result.Mode == WorkflowMode.MultipleAlbums, "An artist folder must not be processed as one album.");
    Assert(result.Errors.Any(value => value.Contains("2 independent albums", StringComparison.OrdinalIgnoreCase)), "Multiple-album guidance is missing.");
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
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=blue:s=96x96", "-frames:v", "1", "-update", "1", cover);
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
    Assert(File.Exists(Path.Combine(stagedAlbum, "Tracks", "CD1", "01 - First.flac")) &&
           File.Exists(Path.Combine(stagedAlbum, "Tracks", "CD1", "02 - Second.flac")), "The local splitter did not create both CUE tracks.");
    Assert(File.Exists(Path.Combine(stagedAlbum, "cover.jpg")) && File.Exists(result.ReportPath), "The local cover or conversion report is missing.");
    var handoff = await MetadataGapService.LoadAsync(job);
    Assert(!handoff.RequiresResearch && handoff.MissingFields.Count == 0, "Complete local evidence must produce an empty metadata handoff.");
    Assert(!File.Exists(Path.Combine(job, "metadata-agent-events.jsonl")) && !File.Exists(Path.Combine(job, "metadata-agent-final-message.txt")), "The complete local path must not start Codex.");

    var probeJson = await RunToolOutputAsync(ffprobe, "-v", "error", "-show_streams", "-show_format", "-of", "json", Path.Combine(stagedAlbum, "Tracks", "CD1", "01 - First.flac"));
    using var document = System.Text.Json.JsonDocument.Parse(probeJson);
    var streams = document.RootElement.GetProperty("streams");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "audio" && stream.GetProperty("codec_name").GetString() == "flac"), "The local output has no FLAC stream.");
    Assert(streams.EnumerateArray().Any(stream => stream.GetProperty("codec_type").GetString() == "video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32() == 1), "The local output has no embedded front cover.");
    var tags = document.RootElement.GetProperty("format").GetProperty("tags");
    Assert(tags.EnumerateObject().Any(tag => tag.Name.Equals("TITLE", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() == "First"), "The local track title tag is missing.");
    Assert(tags.EnumerateObject().Any(tag => (tag.Name.Equals("ALBUMARTIST", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("ALBUM_ARTIST", StringComparison.OrdinalIgnoreCase)) && tag.Value.GetString() == "Test Artist"), "The local album-artist tag is missing.");
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
    var tracks = Path.Combine(stagedAlbum, "Tracks", "CD1"); Directory.CreateDirectory(tracks);
    var track = Path.Combine(tracks, "01 - Test.flac");
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
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Test", "file": "Tracks/CD1/01 - Test.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """;
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), report);
    var manifest = Path.Combine(job, "host-manifest.json"); await File.WriteAllTextAsync(manifest, "{}");
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length, await HostStagingService.Sha256Async(source));
    var staged = new StagedJob(job, stagedAlbum, Path.Combine(stagedSkill, "SKILL.md"), ffmpeg, ffprobe, manifest, [stagedSource]);
    var result = await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>());
    Assert(result.Tracks == 1 && result.SourcesDeleted, "The exact source must be deleted after final quick checks.");
    Assert(File.Exists(Path.Combine(destination, "Tracks", "CD1", "01 - Test.flac")) && File.Exists(Path.Combine(destination, "cover.jpg")), "Verified outputs were not committed to final paths.");
    Assert(!File.Exists(source) && File.Exists(cue) && File.Exists(result.ReportPath), "Only the source FLAC should be deleted; the CUE and final report must remain.");
    var summary = await ReportReader.LoadAsync(result.ReportPath);
    Assert(summary.Status == "passed" && summary.Tracks == 1 && summary.Deleted, "The final report did not record quick-check source deletion.");
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
    var trackDirectory = Path.Combine(stagedAlbum, "Tracks", "CD1"); Directory.CreateDirectory(trackDirectory);
    var track = Path.Combine(trackDirectory, "01 - Invalid.flac");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-i", Path.Combine(stagedAlbum, "source.flac"), "-c:a", "copy", track);
    var cover = Path.Combine(stagedAlbum, "cover.jpg");
    await RunToolAsync(ffmpeg, "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", "-update", "1", cover);
    await File.WriteAllTextAsync(Path.Combine(stagedAlbum, "conversion-report.json"), """
    {
      "album": "Invalid transaction test",
      "workflow_mode": "flac_cue_split",
      "genre": { "value": "Rock", "source_type": "inferred", "confidence": "high", "rationale": "test" },
      "cover": { "file": "cover.jpg" },
      "discs": [{ "disc": 1, "source": "source.flac", "tracks": [{ "disc": 1, "track": 1, "title": "Invalid", "file": "Tracks/CD1/01 - Invalid.flac" }] }],
      "verification": { "status": "pending", "sources_deleted": false, "errors": [] }
    }
    """);
    var stagedSource = new StagedSource("source.flac", new FileInfo(source).Length, await HostStagingService.Sha256Async(source));
    var staged = new StagedJob(job, stagedAlbum, string.Empty, ffmpeg, ffprobe, Path.Combine(job, "host-manifest.json"), [stagedSource]);

    Exception? failure = null;
    try { await new HostCommitService().CommitAsync(scan, staged, new Progress<ProgressSnapshot>()); }
    catch (Exception error) { failure = error; }

    Assert(failure is InvalidOperationException && failure.Message.Contains("cover", StringComparison.OrdinalIgnoreCase),
        "The invalid track must fail the quick embedded-cover check.");
    Assert(File.Exists(source), "A failed commit must retain the exact source FLAC.");
    Assert(!Directory.Exists(Path.Combine(destination, "Tracks")), "A failed local check must not commit output tracks.");
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
    Assert(prompt.Contains("deletes the exact inventoried FLAC image", StringComparison.OrdinalIgnoreCase), "The single source-deletion policy is missing.");
    Assert(prompt.Contains("do not fully decode", StringComparison.OrdinalIgnoreCase) && prompt.Contains("do not run verify-flac-split.ps1", StringComparison.OrdinalIgnoreCase), "Fast mode must prohibit full PCM/MD5 verification.");
    Assert(prompt.Contains("Do not probe, map, or access any UNC/network path", StringComparison.OrdinalIgnoreCase), "The local-only runner boundary is missing.");
    Assert(prompt.Contains("already", StringComparison.OrdinalIgnoreCase) && prompt.Contains("split every track locally", StringComparison.OrdinalIgnoreCase), "The metadata agent must receive already-split tracks.");
    Assert(prompt.Contains("Research only those explicitly listed fields", StringComparison.OrdinalIgnoreCase) && prompt.Contains("Never split, extract, or re-encode", StringComparison.OrdinalIgnoreCase), "Codex must be metadata-only and limited to named gaps.");
    Assert(!prompt.Contains("split-first local worker", StringComparison.OrdinalIgnoreCase), "The obsolete Codex split worker is still present.");
    Assert(CodexContract.WorkerStem(options) == "metadata-agent", "Codex may only run as the optional metadata agent.");
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
