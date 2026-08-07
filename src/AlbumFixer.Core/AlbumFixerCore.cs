using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

public enum WorkflowMode { FlacCueSplit, DsdExtraction, ExistingTrackRepair, NeedsInspection, Unsupported }
public enum CheckState { Passed, Warning, Failed }
public enum JobPhase
{
    Ready = 0, Inventoried = 1, CopyingIn = 2, SourceCopyVerified = 3, Processing = 4,
    Tagging = 5, LocalVerificationPassed = 6, CopyingBack = 7, NetworkHashesVerified = 8,
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
        WorkflowMode.NeedsInspection => "Needs inspection",
        _ => "No supported source found"
    };
}

public sealed record PreflightCheck(string Name, CheckState State, string Detail, bool BlocksRun = false);
public sealed record PreflightResult(
    IReadOnlyList<PreflightCheck> Checks, string TempRoot, long RequiredBytes, long AvailableBytes,
    IReadOnlyDictionary<string, string?> Tools)
{
    public bool CanStart => Checks.All(item => !item.BlocksRun || item.State == CheckState.Passed);
}

public sealed record ProgressSnapshot(JobPhase Phase, int Percent, string Status, string Detail, DateTimeOffset UpdatedAt);
public sealed record RunOptions(string CodexPath, string AlbumRoot, string JobDirectory, bool DeleteOriginals, string SkillPath);
public sealed record RunEvent(string Kind, string Message, ProgressSnapshot? Progress = null, string? ThreadId = null);
public sealed record RunResult(int ExitCode, bool Canceled, string? ThreadId, string FinalMessagePath, string EventLogPath);
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
                ".flac" when referenced => ("FLAC image", "Referenced by CUE"),
                ".flac" => ("Existing FLAC", "Individual track candidate"),
                ".dsf" when referenced => ("DSF image", "Large DSD source referenced by CUE"),
                ".dsf" => ("Existing DSF", "Individual track candidate"),
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

        foreach (var missing in references.Where(path => !File.Exists(path)))
            errors.Add($"CUE references a missing source: {Path.GetRelativePath(root, missing)}");

        var images = media.Where(item => item.Kind.Contains("image", StringComparison.OrdinalIgnoreCase) || item.Kind is "DST stream" or "Raw DSD").ToArray();
        var tracks = media.Where(item => item.Kind.StartsWith("Existing", StringComparison.OrdinalIgnoreCase)).ToArray();
        WorkflowMode mode;
        if (tracks.Length >= 2)
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

    [GeneratedRegex("^\\s*FILE\\s+(?:\"(?<q>[^\"]+)\"|(?<u>\\S+))\\s+\\S+", RegexOptions.IgnoreCase)]
    private static partial Regex CueFile();
}

public sealed class PreflightService
{
    public async Task<PreflightResult> CheckAsync(ScanResult scan, string skillPath, CancellationToken token = default)
    {
        var names = new[] { "codex", "ffmpeg", "ffprobe", "sacd_extract" };
        var tasks = names.ToDictionary(name => name, name => FindToolAsync(name, token));
        await Task.WhenAll(tasks.Values);
        var tools = tasks.ToDictionary(pair => pair.Key, pair => pair.Value.Result, StringComparer.OrdinalIgnoreCase);
        var checks = new List<PreflightCheck>
        {
            tools["codex"] is { } codex ? new("Codex runner", CheckState.Passed, codex) : new("Codex runner", CheckState.Failed, "Codex is not available on PATH.", true),
            File.Exists(skillPath) ? new("Album Fixer skill", CheckState.Passed, skillPath) : new("Album Fixer skill", CheckState.Failed, "Installed skill was not found.", true)
        };
        if (scan.HasFlac)
        {
            Require(checks, "ffmpeg", tools["ffmpeg"], "FLAC decode and equivalence verification");
            Require(checks, "ffprobe", tools["ffprobe"], "stream, tag, and artwork verification");
        }
        else if (scan.HasDsd) Require(checks, "ffprobe", tools["ffprobe"], "DSD container verification");
        if (scan.Media.Any(item => item.Kind == "SACD / DSD image"))
            checks.Add(tools["sacd_extract"] is { } sacd ? new("sacd_extract", CheckState.Passed, sacd) : new("sacd_extract", CheckState.Warning, "Not found. SACD images will stop safely; Sony DSD Disc images may still be copied after probing."));

        var tempRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "album-fixer"));
        long available = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(tempRoot)!);
            available = drive.AvailableFreeSpace;
            checks.Add(drive.DriveType == DriveType.Fixed ? new("Windows Temp", CheckState.Passed, $"Local fixed staging volume: {tempRoot}") : new("Windows Temp", CheckState.Failed, "Windows Temp is not on a local fixed drive.", true));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { checks.Add(new("Windows Temp", CheckState.Failed, error.Message, true)); }
        var factor = scan.Mode == WorkflowMode.DsdExtraction ? 7.2 : scan.Mode == WorkflowMode.ExistingTrackRepair ? 3.0 : 3.84;
        var required = (long)(Math.Max(scan.SourceBytes, 512L * 1024 * 1024) * factor);
        checks.Add(available >= required ? new("Local staging capacity", CheckState.Passed, $"{SizeText.Format(available)} available; {SizeText.Format(required)} estimated.") : new("Local staging capacity", CheckState.Failed, $"{SizeText.Format(required)} estimated; only {SizeText.Format(available)} available.", true));
        checks.Add(scan.Mode switch
        {
            WorkflowMode.Unsupported => new("Album classification", CheckState.Failed, scan.WorkflowLabel, true),
            WorkflowMode.NeedsInspection => new("Album classification", CheckState.Warning, "Codex must resolve the source type before processing; uncertainty retains the original."),
            _ => new("Album classification", CheckState.Passed, scan.WorkflowLabel)
        });
        checks.AddRange(scan.Errors.Select(error => new PreflightCheck("Inventory", CheckState.Failed, error, true)));
        return new(checks, tempRoot, required, available, tools);
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

    private static async Task<string?> FindToolAsync(string name, CancellationToken token)
    {
        var exe = Path.HasExtension(name) ? name : name + ".exe";
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try { var path = Path.Combine(directory.Trim('"'), exe); if (File.Exists(path)) return Path.GetFullPath(path); }
            catch (ArgumentException) { }
        }
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "where.exe")) { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.ArgumentList.Add(name);
        try
        {
            using var process = Process.Start(info); if (process is null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(token); await process.WaitForExitAsync(token);
            return process.ExitCode == 0 ? output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(File.Exists) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }
}

public static class CodexContract
{
    public static IReadOnlyList<string> Arguments(RunOptions options) =>
    ["--ask-for-approval", "never", "--add-dir", options.JobDirectory, "exec", "--json", "--sandbox", "workspace-write", "--skip-git-repo-check", "--cd", options.AlbumRoot, "--output-last-message", Path.Combine(options.JobDirectory, "final-message.txt"), "-"];

    public static string Prompt(RunOptions options)
    {
        var deletion = options.DeleteOriginals
            ? "The user confirms default deletion of each exact inventoried source only after every final verification gate passes."
            : "The user explicitly overrides deletion: retain every original source after verification.";
        return $"""
Use the installed $album-fixer skill faithfully for this album.
Album root: {options.AlbumRoot}
Skill file: {options.SkillPath}
Approved unique Windows Temp job directory: {options.JobDirectory}
{deletion}

This is a non-interactive desktop-app run. Never guess and never wait for an answer. If a required tool, exact release match, tag value, artwork, or signal-equivalence proof is missing or uncertain, stop safely, retain every original, record the reason, and return a concise blocked/failed result. Do not modify anything outside the album root and approved Temp job directory. Preserve all provenance and unrelated files. Create or update conversion-report.json at the album root before any allowed deletion.

Atomically replace {Path.Combine(options.JobDirectory, "ui-progress.json")} after every state transition. Use compact UTF-8 JSON with phase, phase_index, phase_count (12), percent, status, detail, updated_at_utc. Use these ordered phase names: Inventoried; Copying in; Source copy verified; Splitting or extracting; Tagging; Local verification passed; Copying back; Network-side hashes verified; Final commit; Final-path verification passed; Source deleted or retained; Local cleanup completed. On failure or cancellation write status=failed or canceled, retain sources, and state the exact stopping point. Begin each user-visible update with ALBUM_FIXER_PROGRESS followed by the same one-line object.

At completion summarize workflow mode, outputs and areas/discs, verification method/status, report path, originals deleted or retained, recovery implications, and errors.
""";
    }
}

public sealed partial class CodexRunner : IDisposable
{
    private Process? _active;
    public async Task<RunResult> RunAsync(RunOptions options, IProgress<RunEvent> progress, CancellationToken token)
    {
        Directory.CreateDirectory(options.JobDirectory);
        var finalPath = Path.Combine(options.JobDirectory, "final-message.txt");
        var logPath = Path.Combine(options.JobDirectory, "codex-events.jsonl");
        var info = new ProcessStartInfo(options.CodexPath) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = options.AlbumRoot, StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false) };
        foreach (var argument in CodexContract.Arguments(options)) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        _active = process;
        var canceled = false;
        string? threadId = null;
        if (!process.Start()) throw new InvalidOperationException("Could not start Codex.");
        using var registration = token.Register(() => { canceled = true; Kill(process); });
        await using var log = new StreamWriter(logPath, false, new UTF8Encoding(false));
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                await log.WriteLineAsync(line); await log.FlushAsync();
                var parsed = ParseEvent(line); if (parsed is null) continue;
                if (parsed.ThreadId is not null) threadId = parsed.ThreadId;
                progress.Report(parsed);
            }
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line) progress.Report(new("error", line));
        });
        await process.StandardInput.WriteAsync(CodexContract.Prompt(options).AsMemory(), token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(CancellationToken.None);
        await Task.WhenAll(stdout, stderr);
        _active = null;
        return new(process.ExitCode, canceled, threadId, finalPath, logPath);
    }

    public void Cancel() { if (_active is { HasExited: false } process) Kill(process); }
    public void Dispose() { Cancel(); GC.SuppressFinalize(this); }

    public static bool TryProgress(string json, out ProgressSnapshot snapshot)
    {
        try
        {
            using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
            var phaseText = Text(root, "phase") ?? "Ready"; var phase = Phase(phaseText);
            var status = Text(root, "status") ?? "running";
            if (status.Equals("failed", StringComparison.OrdinalIgnoreCase)) phase = JobPhase.Failed;
            if (status.Equals("canceled", StringComparison.OrdinalIgnoreCase)) phase = JobPhase.Canceled;
            var percent = root.TryGetProperty("percent", out var p) && p.TryGetInt32(out var number) ? Math.Clamp(number, 0, 100) : DefaultPercent(phase);
            snapshot = new(phase, percent, status, Text(root, "detail") ?? phaseText, DateTimeOffset.UtcNow); return true;
        }
        catch (JsonException) { snapshot = new(JobPhase.Ready, 0, "invalid", "Invalid progress data", DateTimeOffset.UtcNow); return false; }
    }

    private static RunEvent? ParseEvent(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line); var root = doc.RootElement; var type = Text(root, "type") ?? "event";
            var strings = Strings(root).ToArray();
            foreach (var value in strings)
            {
                var marker = Marker().Match(value);
                if (marker.Success && TryProgress(marker.Groups["j"].Value, out var snapshot)) return new("progress", snapshot.Detail, snapshot);
            }
            if (type == "thread.started") return new("thread", "Codex session started.", ThreadId: Text(root, "thread_id"));
            if (type.Contains("failed") || type.Contains("error")) return new("error", Useful(strings, "Codex reported a failure."));
            if (type == "turn.started") return new("status", "Codex is inspecting the album.");
            if (type == "turn.completed") return new("status", "Codex finished and is finalizing the report.");
            if (type.StartsWith("item."))
            {
                var item = root.TryGetProperty("item", out var i) ? i : root; var itemType = Text(item, "type") ?? "activity";
                var message = itemType switch { "reasoning" => "Planning the next safe step…", "web_search" => "Researching the exact release and artwork…", "command_execution" => "Running an album workflow check…", _ => Useful(Strings(item), "Codex activity…") };
                return new("activity", message);
            }
        }
        catch (JsonException) { return new("activity", line.Length <= 280 ? line : line[..279] + "…"); }
        return null;
    }

    public static JobPhase Phase(string value)
    {
        var text = value.ToLowerInvariant();
        if (text.Contains("fail")) return JobPhase.Failed; if (text.Contains("cancel")) return JobPhase.Canceled; if (text.Contains("cleanup")) return JobPhase.CleanupCompleted;
        if (text.Contains("source deleted") || text.Contains("source retained")) return JobPhase.SourceDisposition; if (text.Contains("final-path") || text.Contains("final path")) return JobPhase.FinalVerificationPassed;
        if (text.Contains("final commit")) return JobPhase.FinalCommit; if (text.Contains("network")) return JobPhase.NetworkHashesVerified; if (text.Contains("copying back")) return JobPhase.CopyingBack;
        if (text.Contains("local verification")) return JobPhase.LocalVerificationPassed; if (text.Contains("tag")) return JobPhase.Tagging; if (text.Contains("split") || text.Contains("extract")) return JobPhase.Processing;
        if (text.Contains("source copy verified")) return JobPhase.SourceCopyVerified; if (text.Contains("copying in")) return JobPhase.CopyingIn; if (text.Contains("inventor")) return JobPhase.Inventoried; return JobPhase.Ready;
    }

    public static int DefaultPercent(JobPhase phase) => phase is >= JobPhase.Inventoried and <= JobPhase.CleanupCompleted ? Math.Min(100, (int)phase * 8) : 0;
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString() : null;
    private static IEnumerable<string> Strings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString() is { } value) yield return value;
        else if (element.ValueKind == JsonValueKind.Object) foreach (var property in element.EnumerateObject()) foreach (var child in Strings(property.Value)) yield return child;
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) foreach (var child in Strings(item)) yield return child;
    }
    private static string Useful(IEnumerable<string> values, string fallback) { var value = values.Where(x => x.Length > 2 && !Guid.TryParse(x, out _)).OrderByDescending(x => x.Length).FirstOrDefault(); return value is null ? fallback : value.ReplaceLineEndings(" ")[..Math.Min(value.Length, 360)]; }
    private static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    [GeneratedRegex("ALBUM_FIXER_PROGRESS\\s*(?<j>\\{.*?\\})", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex Marker();
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
        var tracks = new HashSet<string>(StringComparer.OrdinalIgnoreCase); Files(root, tracks);
        var sections = Count(root, "discs") + Count(root, "areas") + Count(root, "audio_areas");
        var label = status.ToLowerInvariant() switch { "passed" => "Verification passed", "failed" => "Verification failed", "blocked" => "Run blocked safely", _ => "Report pending" };
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
