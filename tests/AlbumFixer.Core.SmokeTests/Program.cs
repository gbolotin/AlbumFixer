using AlbumFixer.Core;

var root = Path.Combine(Path.GetTempPath(), "album-fixer-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    await ScannerClassifiesFlacCue(root);
    await ScannerPrefersExistingTracks(root);
    ProgressContractParses();
    await ReportSummaryLoads(root);
    CommandContractIsSandboxed(root);
    Console.WriteLine("AlbumFixer.Core smoke tests passed (5/5).");
    return 0;
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

static void ProgressContractParses()
{
    var json = "{\"phase\":\"Final-path verification passed\",\"percent\":92,\"status\":\"running\",\"detail\":\"Network copy verified\"}";
    Assert(CodexRunner.TryProgress(json, out var snapshot), "Progress JSON should parse.");
    Assert(snapshot.Phase == JobPhase.FinalVerificationPassed && snapshot.Percent == 92, "Progress phase mapping is wrong.");
}

static async Task ReportSummaryLoads(string root)
{
    var path = Path.Combine(root, "conversion-report.json");
    await File.WriteAllTextAsync(path, """
    {"album":"Test Album","edition":"Label CAT-1","workflow_mode":"flac_cue_split","discs":[{"tracks":[{"file":"Tracks/01.flac"},{"file":"Tracks/02.flac"}]}],"verification":{"status":"passed","method":"PCM MD5 and byte count","sources_deleted":true,"errors":[]}}
    """);
    var summary = await ReportReader.LoadAsync(path);
    Assert(summary.Status == "passed" && summary.Tracks == 2 && summary.Sections == 1 && summary.Deleted, "Report summary is wrong.");
}

static void CommandContractIsSandboxed(string root)
{
    var options = new RunOptions("codex.exe", root, Path.Combine(root, "job"), false, "SKILL.md");
    var args = CodexContract.Arguments(options);
    Assert(args.Contains("workspace-write") && args.Contains("never") && !args.Any(value => value.Contains("yolo", StringComparison.OrdinalIgnoreCase)), "Unsafe Codex command flags detected.");
    Assert(CodexContract.Prompt(options).Contains("retain every original", StringComparison.OrdinalIgnoreCase), "Keep-original override is missing.");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
