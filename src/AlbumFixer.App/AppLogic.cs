using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using AlbumFixer.Core;

namespace AlbumFixer.App;

public abstract class NotifyBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class Command(Action action, Func<bool>? allowed = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => allowed?.Invoke() ?? true;
    public void Execute(object? parameter) => action();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncCommand(Func<Task> action, Func<bool>? allowed = null) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && (allowed?.Invoke() ?? true);
    public async void Execute(object? parameter) { if (!CanExecute(parameter)) return; _running = true; Refresh(); try { await action(); } finally { _running = false; Refresh(); } }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record ActivityRow(string Time, string Kind, string Message);
public sealed record MediaRow(string Path, string Kind, string Size, string Note);
public sealed record CheckRow(string Name, string State, string Detail, CheckState RawState);

public sealed class TimelineRow : NotifyBase
{
    private string _state = "Pending";
    public required int Number { get; init; }
    public required JobPhase Phase { get; init; }
    public required string Title { get; init; }
    public string NumberText => Number.ToString("00");
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class MainViewModel : NotifyBase, IDisposable
{
    private const string SkillPath = @"C:\Users\gbolotin\.codex\skills\album-fixer\SKILL.md";
    private readonly AlbumScanner _scanner = new();
    private readonly PreflightService _preflightService = new();
    private readonly CodexRunner _runner = new();
    private readonly HostStagingService _staging = new();
    private readonly LocalFlacProcessor _localProcessor = new();
    private readonly HostCommitService _commit = new();
    private CancellationTokenSource? _cancel;
    private ScanResult? _scan;
    private PreflightResult? _preflight;
    private string _albumPath = "";
    private string _albumName = "No album selected";
    private string _workflow = "Choose one album folder to begin";
    private string _inventory = "—";
    private string _sourceSize = "—";
    private string _statusTitle = "Ready for a safe run";
    private string _statusDetail = "Inventory first. Originals stay in place whenever a proof is missing.";
    private double _progress;
    private bool _busy;
    private string _reportHeadline = "No conversion report yet";
    private string _reportDetail = "A validated summary and JSON report will appear here.";
    private string _reportJson = "";
    private string _reportStatus = "Pending";
    private string _reportTracks = "—";
    private string _reportSections = "—";
    private string _reportDisposition = "—";
    private string _jobDirectory = "—";
    private string _threadId = "—";
    private JobPhase _lastPhase = JobPhase.Ready;
    private string _lastRunStatus = "pending";
    private string _lastRunDetail = "No run has started.";
    private DateTimeOffset _runStartedAt;
    private DateTimeOffset _lastActivityAt;
    private DateTimeOffset _lastProgressAt;
    private DateTimeOffset _lastHeartbeatAt;
    private bool _startupNoticeLogged;

    public MainViewModel()
    {
        ScanCommand = new(ScanAsync, () => !Busy && !string.IsNullOrWhiteSpace(AlbumPath));
        StartCommand = new(StartAsync, () => CanStart);
        CancelCommand = new(Cancel, () => Busy);
        RefreshReportCommand = new(LoadReportAsync, () => !string.IsNullOrWhiteSpace(AlbumPath));
        OpenAlbumCommand = new(OpenAlbum, () => Directory.Exists(AlbumPath));
        CopyReportCommand = new(CopyReport, () => ReportJson.Length > 0);
        var titles = new[] { "Inventoried", "Copying in", "Source copy verified", "Split / extract", "Tagging", "Local verification", "Copying back", "Network hashes", "Final commit", "Final verification", "Source disposition", "Cleanup" };
        for (var i = 0; i < titles.Length; i++) Timeline.Add(new() { Number = i + 1, Phase = (JobPhase)(i + 1), Title = titles[i] });
    }

    public ObservableCollection<CheckRow> Checks { get; } = [];
    public ObservableCollection<MediaRow> Media { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];
    public ObservableCollection<string> ReportErrors { get; } = [];
    public AsyncCommand ScanCommand { get; }
    public AsyncCommand StartCommand { get; }
    public Command CancelCommand { get; }
    public AsyncCommand RefreshReportCommand { get; }
    public Command OpenAlbumCommand { get; }
    public Command CopyReportCommand { get; }
    public Func<bool>? ConfirmStart { get; set; }

    public string AlbumPath { get => _albumPath; set { if (!Set(ref _albumPath, value)) return; Invalidate(); ScanCommand.Refresh(); OpenAlbumCommand.Refresh(); } }
    public string AlbumName { get => _albumName; private set => Set(ref _albumName, value); }
    public string Workflow { get => _workflow; private set => Set(ref _workflow, value); }
    public string Inventory { get => _inventory; private set => Set(ref _inventory, value); }
    public string SourceSize { get => _sourceSize; private set => Set(ref _sourceSize, value); }
    public string StatusTitle { get => _statusTitle; private set => Set(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool Busy { get => _busy; private set { if (!Set(ref _busy, value)) return; Raise(nameof(CanStart)); Raise(nameof(CanBrowse)); ScanCommand.Refresh(); StartCommand.Refresh(); CancelCommand.Refresh(); } }
    public bool CanBrowse => !Busy;
    public bool CanStart => !Busy && _scan is not null && _preflight?.CanStart == true;
    public string ReportHeadline { get => _reportHeadline; private set => Set(ref _reportHeadline, value); }
    public string ReportDetail { get => _reportDetail; private set => Set(ref _reportDetail, value); }
    public string ReportJson { get => _reportJson; private set { if (Set(ref _reportJson, value)) CopyReportCommand.Refresh(); } }
    public string ReportStatus { get => _reportStatus; private set => Set(ref _reportStatus, value); }
    public string ReportTracks { get => _reportTracks; private set => Set(ref _reportTracks, value); }
    public string ReportSections { get => _reportSections; private set => Set(ref _reportSections, value); }
    public string ReportDisposition { get => _reportDisposition; private set => Set(ref _reportDisposition, value); }
    public string JobDirectory { get => _jobDirectory; private set => Set(ref _jobDirectory, value); }
    public string ThreadId { get => _threadId; private set => Set(ref _threadId, value); }

    private async Task ScanAsync()
    {
        Busy = true; StatusTitle = "Inventorying the album…"; StatusDetail = "Reading media, CUE references, artwork, and provenance without changing files."; Log("SCAN", "Read-only inventory started.");
        try
        {
            _scan = await _scanner.ScanAsync(AlbumPath); AlbumName = _scan.AlbumName; Workflow = _scan.WorkflowLabel;
            Inventory = $"{_scan.ImageCount} image{S(_scan.ImageCount)}  •  {_scan.TrackCount} track{S(_scan.TrackCount)}  •  {_scan.CueCount} CUE"; SourceSize = SizeText.Format(_scan.SourceBytes);
            Replace(Media, _scan.Media.Select(item => new MediaRow(item.RelativePath, item.Kind, item.Size > 0 ? SizeText.Format(item.Size) : "—", item.Note)));
            StatusTitle = "Checking safe-run prerequisites…"; _preflight = await _preflightService.CheckAsync(_scan, SkillPath);
            Replace(Checks, _preflight.Checks.Select(item => new CheckRow(item.Name, item.State switch { CheckState.Passed => "Ready", CheckState.Warning => "Review", _ => "Blocked" }, item.Detail, item.State)));
            if (_scan.Mode == WorkflowMode.MultipleAlbums)
            {
                StatusTitle = "Choose one album folder";
                StatusDetail = _scan.Errors.First(error => error.Contains("independent albums", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                StatusTitle = _preflight.CanStart ? "Ready to process" : "Run blocked safely";
                StatusDetail = _preflight.CanStart ? "Every blocking preflight check passed. The source remains until final verification." : "Resolve the blocked checks below. No album files were changed.";
            }
            Log(_preflight.CanStart ? "READY" : "BLOCKED", StatusDetail); await LoadReportAsync();
        }
        catch (Exception error) { _scan = null; _preflight = null; StatusTitle = "Could not inventory this folder"; StatusDetail = error.Message; Log("ERROR", error.Message); }
        finally { Busy = false; Raise(nameof(CanStart)); StartCommand.Refresh(); }
    }

    private async Task StartAsync()
    {
        if (!CanStart || _scan is null || _preflight is null || ConfirmStart is not null && !ConfirmStart()) return;
        Busy = true; Progress = 1; foreach (var item in Timeline) item.State = "Pending"; _cancel = new();
        _lastPhase = JobPhase.Ready; _lastRunStatus = "running"; _lastRunDetail = "Starting the safe run.";
        _runStartedAt = _lastActivityAt = _lastProgressAt = _lastHeartbeatAt = DateTimeOffset.UtcNow; _startupNoticeLogged = false;
        ReportStatus = "Pending"; ReportTracks = ReportSections = ReportDisposition = "—"; ReportJson = "";
        ReportHeadline = "Run in progress"; ReportDetail = "A terminal report will be preserved even if the run stops.";
        JobDirectory = PreflightService.CreateJobDirectory(_preflight.TempRoot);
        StatusTitle = "Starting the fast workflow…"; StatusDetail = "PCM/MD5 comparison is skipped; the original FLAC image will be deleted only after successful final quick checks."; Log("START", JobDirectory);
        using var monitorCancel = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        Task monitor = Task.CompletedTask;
        try
        {
            var staged = await _staging.StageAsync(_scan, _preflight, SkillPath, JobDirectory, new Progress<ProgressSnapshot>(Apply), _cancel.Token);
            Log("SPLIT", "Starting the deterministic local CUE/FFmpeg splitter. No Codex process is running.");
            var localResult = await _localProcessor.ProcessAsync(_scan, staged, new Progress<ProgressSnapshot>(Apply), _cancel.Token);
            Log("SPLIT", $"Local split completed: {localResult.Tracks} tracks.");

            var gaps = localResult.Metadata;
            if (gaps.RequiresResearch)
            {
                var fields = string.Join(", ", gaps.MissingFields);
                var codexPath = await PreflightService.FindOptionalCodexAsync(_cancel.Token);
                if (codexPath is null || !File.Exists(codexPath))
                    throw new InvalidOperationException($"The local split completed, but required metadata is missing ({fields}) and the optional Codex metadata agent is unavailable. Local results are preserved at {staged.AlbumRoot}.");
                if (!File.Exists(SkillPath))
                    throw new InvalidOperationException($"The local split completed, but required metadata is missing ({fields}) and the Album Fixer skill is unavailable. Local results are preserved at {staged.AlbumRoot}.");

                Apply(new(JobPhase.Tagging, Math.Max((int)Progress, 44), "running", $"Only missing metadata is being deferred to Codex: {fields}.", DateTimeOffset.UtcNow));
                Log("METADATA", $"Starting one optional metadata-only agent for: {fields}.");
                var stagedSkillPath = await _staging.StageSkillAsync(SkillPath, JobDirectory, _cancel.Token);
                var metadataOptions = new RunOptions(codexPath, staged.AlbumRoot, JobDirectory, stagedSkillPath, staged.FfmpegPath, staged.FfprobePath, CodexWorkKind.MetadataEnrichment);
                monitor = MonitorAsync(Path.Combine(JobDirectory, "ui-progress.json"), monitorCancel.Token);
                var metadataResult = await _runner.RunAsync(metadataOptions, new Progress<RunEvent>(OnRunEvent), _cancel.Token);
                ThreadId = metadataResult.ThreadId ?? "—";
                if (metadataResult.LastProgress is not null) Apply(metadataResult.LastProgress);
                await EnsureWorkerSucceededAsync(metadataResult, "Metadata agent");
            }
            else
            {
                ThreadId = "Not required";
                Log("METADATA", "All required metadata and artwork were found locally. Codex was not started.");
            }
            monitorCancel.Cancel(); await IgnoreCancel(monitor);
            var committed = await _commit.CommitAsync(_scan, staged, new Progress<ProgressSnapshot>(Apply), _cancel.Token);
            await LoadReportAsync();
            if (!ReportStatus.Equals("passed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Final quick verification did not produce a passed conversion report. The source was not authorized for deletion.");
            Progress = 100; StatusTitle = "Album completed with quick checks";
            StatusDetail = $"{committed.Tracks} track{S(committed.Tracks)} passed quick checks. The original FLAC image was deleted.";
            Log("DONE", $"Report status: passed; {committed.Tracks} tracks.");
        }
        catch (OperationCanceledException)
        {
            monitorCancel.Cancel(); await IgnoreCancel(monitor);
            Apply(new(JobPhase.Canceled, (int)Progress, "canceled", "Run canceled. Inspect preserved staging before resuming; every original was retained.", DateTimeOffset.UtcNow));
            await EnsureTerminalReportAsync(null, true, ThreadId == "—" ? null : ThreadId); await LoadReportAsync();
        }
        catch (Exception error)
        {
            monitorCancel.Cancel(); await IgnoreCancel(monitor);
            Apply(new(JobPhase.Failed, (int)Progress, "failed", error.Message, DateTimeOffset.UtcNow)); Log("ERROR", error.Message);
            await EnsureTerminalReportAsync(null, false, ThreadId == "—" ? null : ThreadId); await LoadReportAsync();
        }
        finally { _cancel.Dispose(); _cancel = null; Busy = false; }
    }
    private async Task MonitorAsync(string path, CancellationToken token)
    {
        var last = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try { if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > last) { var text = await File.ReadAllTextAsync(path, token); if (CodexRunner.TryProgress(text, out var progress)) { Apply(progress); last = File.GetLastWriteTimeUtc(path); await LoadReportAsync(); } } }
            catch (IOException error) { Log("WAIT", error.Message); }
            var now = DateTimeOffset.UtcNow;
            if (_runStartedAt != default && now - _lastHeartbeatAt >= TimeSpan.FromSeconds(10))
            {
                var elapsed = Clock(now - _runStartedAt);
                var quiet = Clock(now - _lastActivityAt);
                StatusDetail = $"{_lastRunDetail} Still working locally — elapsed {elapsed}; latest runner activity {quiet} ago.";
                if (now - _lastHeartbeatAt >= TimeSpan.FromMinutes(1)) Log("WORKING", $"Local phase {_lastPhase}: elapsed {elapsed}; latest runner activity {quiet} ago.");
                _lastHeartbeatAt = now;
            }
            await Task.Delay(600, token);
        }
    }

    private async Task LoadReportAsync()
    {
        var candidates = new[]
        {
            Path.Combine(AlbumPath, "conversion-report.json"),
            Directory.Exists(JobDirectory) ? Path.Combine(JobDirectory, "album", "conversion-report.json") : "",
            Directory.Exists(JobDirectory) ? Path.Combine(JobDirectory, "conversion-report.json") : ""
        }.Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        if (candidates.Length == 0) return;
        try { var report = await ReportReader.LoadAsync(candidates[0]); ReportHeadline = report.Headline; ReportDetail = report.Detail; ReportJson = report.Json; ReportStatus = report.Status; ReportTracks = report.Tracks.ToString(); ReportSections = report.Sections.ToString(); ReportDisposition = report.Deleted ? "Deleted after quick checks" : "Retained"; Replace(ReportErrors, report.Errors); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { ReportHeadline = "Report is not readable yet"; ReportDetail = error.Message; }
    }

    private async Task EnsureTerminalReportAsync(int? exitCode, bool canceled, string? threadId)
    {
        if (_scan is null || _preflight is null || !Directory.Exists(JobDirectory)) return;
        try
        {
            await HostReportWriter.EnsureTerminalReportAsync(_scan, _preflight, JobDirectory,
                canceled ? "canceled" : "failed", _lastPhase, (int)Progress, _lastRunDetail,
                exitCode, threadId);
            Log("REPORT", "A terminal report was preserved; every original remains in place.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log("REPORT", $"Could not preserve the fallback report: {error.Message}");
        }
    }

    private void OnRunEvent(RunEvent item)
    {
        var now = DateTimeOffset.UtcNow;
        _lastActivityAt = now;
        if (item.ThreadId is not null) ThreadId = item.ThreadId;
        if (item.Progress is not null) Apply(item.Progress);
        if (item.Message.Length == 0) return;
        if (item.Kind.Equals("warning", StringComparison.OrdinalIgnoreCase) && IsStartupNoise(item.Message))
        {
            if (!_startupNoticeLogged)
            {
                Log("NOTICE", "Codex startup/plugin warnings were collapsed; album processing is unaffected.");
                _startupNoticeLogged = true;
            }
            return;
        }
        if (item.Kind is "activity" or "status") StatusDetail = item.Message;
        Log(item.Kind.ToUpperInvariant(), item.Message);
    }
    private void Apply(ProgressSnapshot snapshot)
    {
        _lastPhase = snapshot.Phase; _lastRunStatus = snapshot.Status; _lastRunDetail = snapshot.Detail;
        _lastProgressAt = _lastActivityAt = DateTimeOffset.UtcNow;
        Progress = Math.Max(Progress, snapshot.Percent); StatusTitle = PhaseTitle(snapshot.Phase); StatusDetail = snapshot.Detail;
        var number = snapshot.Phase is >= JobPhase.Inventoried and <= JobPhase.CleanupCompleted ? (int)snapshot.Phase : Timeline.FirstOrDefault(x => x.State == "Active")?.Number ?? 1;
        foreach (var item in Timeline) item.State = snapshot.Phase == JobPhase.Failed && item.Number == number ? "Failed" : snapshot.Phase == JobPhase.Canceled && item.Number == number ? "Canceled" : item.Number < number ? "Complete" : item.Number == number ? snapshot.Phase == JobPhase.CleanupCompleted ? "Complete" : "Active" : "Pending";
    }

    private static bool IsStartupNoise(string message) =>
        message.Contains("codex_core::plugins::manifest", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("codex_core::skills::loader", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("codex_core::shell_snapshot", StringComparison.OrdinalIgnoreCase);

    private static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
    }
    private void Cancel() { if (!Busy) return; StatusTitle = "Stopping at the next safe boundary…"; StatusDetail = "An incomplete job cannot authorize source deletion."; Log("CANCEL", "Cancellation requested."); _cancel?.Cancel(); _runner.Cancel(); }
    private void OpenAlbum() { if (Directory.Exists(AlbumPath)) Process.Start(new ProcessStartInfo(AlbumPath) { UseShellExecute = true }); }
    private void CopyReport() { if (ReportJson.Length > 0) Clipboard.SetText(ReportJson); }
    private void Invalidate() { if (Busy) return; _scan = null; _preflight = null; Checks.Clear(); Media.Clear(); AlbumName = string.IsNullOrWhiteSpace(AlbumPath) ? "No album selected" : Path.GetFileName(AlbumPath.TrimEnd(Path.DirectorySeparatorChar)); Workflow = "Scan to classify this album"; Inventory = SourceSize = "—"; StatusTitle = "Ready to scan"; StatusDetail = "Inventory is read-only."; Raise(nameof(CanStart)); StartCommand.Refresh(); }
    private void Log(string kind, string message) { Activity.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), kind, message.ReplaceLineEndings(" "))); while (Activity.Count > 250) Activity.RemoveAt(Activity.Count - 1); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
    private static string S(int count) => count == 1 ? "" : "s";
    private static string PhaseTitle(JobPhase phase) => phase switch { JobPhase.Inventoried => "Inventory complete", JobPhase.CopyingIn => "Copying into local staging…", JobPhase.SourceCopyVerified => "Source copy verified", JobPhase.Processing => "Splitting or extracting…", JobPhase.Tagging => "Writing metadata and artwork…", JobPhase.LocalVerificationPassed => "Local verification passed", JobPhase.CopyingBack => "Copying verified output back…", JobPhase.NetworkHashesVerified => "Network-side hashes verified", JobPhase.FinalCommit => "Committing final files…", JobPhase.FinalVerificationPassed => "Final-path verification passed", JobPhase.SourceDisposition => "Recording source disposition…", JobPhase.CleanupCompleted => "Album completed", JobPhase.Failed => "Run stopped safely", JobPhase.Canceled => "Run canceled", _ => "Preparing the job…" };
    private static async Task EnsureWorkerSucceededAsync(RunResult result, string workerName)
    {
        var finalMessage = await FinalMessage(result.FinalMessagePath);
        if (result.Canceled)
            throw new OperationCanceledException($"{workerName} canceled. Staging and every original were retained for review.");
        if (result.LastProgress?.Phase == JobPhase.Failed || string.Equals(result.LastProgress?.Status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(finalMessage ?? result.LastProgress?.Detail ?? $"{workerName} failed.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"{workerName} exited with code {result.ExitCode}. Incomplete jobs retain originals.");
    }
    private static async Task<string?> FinalMessage(string path) { if (!File.Exists(path)) return null; var text = (await File.ReadAllTextAsync(path, Encoding.UTF8)).Trim(); return text.Length <= 800 ? text : text[..799] + "…"; }
    private static async Task IgnoreCancel(Task task) { try { await task; } catch (OperationCanceledException) { } }
    public void Dispose() { _cancel?.Cancel(); _cancel?.Dispose(); _runner.Dispose(); GC.SuppressFinalize(this); }
}
