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
    private bool _deleteOriginals = true;
    private string _reportHeadline = "No conversion report yet";
    private string _reportDetail = "A validated summary and JSON report will appear here.";
    private string _reportJson = "";
    private string _reportStatus = "Pending";
    private string _reportTracks = "—";
    private string _reportSections = "—";
    private string _reportDisposition = "—";
    private string _jobDirectory = "—";
    private string _threadId = "—";

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
    public Func<bool, bool>? ConfirmStart { get; set; }

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
    public bool DeleteOriginals { get => _deleteOriginals; set => Set(ref _deleteOriginals, value); }
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
            StatusTitle = _preflight.CanStart ? "Ready to process" : "Run blocked safely";
            StatusDetail = _preflight.CanStart ? "Every blocking preflight check passed. The source remains until final verification." : "Resolve the blocked checks below. No album files were changed.";
            Log(_preflight.CanStart ? "READY" : "BLOCKED", StatusDetail); await LoadReportAsync();
        }
        catch (Exception error) { _scan = null; _preflight = null; StatusTitle = "Could not inventory this folder"; StatusDetail = error.Message; Log("ERROR", error.Message); }
        finally { Busy = false; Raise(nameof(CanStart)); StartCommand.Refresh(); }
    }

    private async Task StartAsync()
    {
        if (!CanStart || _scan is null || _preflight is null || ConfirmStart is not null && !ConfirmStart(DeleteOriginals)) return;
        Busy = true; Progress = 1; foreach (var item in Timeline) item.State = "Pending"; _cancel = new();
        JobDirectory = PreflightService.CreateJobDirectory(_preflight.TempRoot);
        var options = new RunOptions(_preflight.Tools["codex"]!, _scan.AlbumRoot, JobDirectory, DeleteOriginals, SkillPath);
        StatusTitle = "Starting the transactional workflow…"; StatusDetail = DeleteOriginals ? "Deletion is authorized only after every final verification gate." : "Every original will be retained after verification."; Log("START", JobDirectory);
        using var monitorCancel = CancellationTokenSource.CreateLinkedTokenSource(_cancel.Token);
        var monitor = MonitorAsync(Path.Combine(JobDirectory, "ui-progress.json"), monitorCancel.Token);
        try
        {
            var result = await _runner.RunAsync(options, new Progress<RunEvent>(OnRunEvent), _cancel.Token); ThreadId = result.ThreadId ?? "—";
            monitorCancel.Cancel(); await IgnoreCancel(monitor); await LoadReportAsync();
            if (result.Canceled) Apply(new(JobPhase.Canceled, (int)Progress, "canceled", "Run canceled. Review staging and report before retrying.", DateTimeOffset.UtcNow));
            else if (result.ExitCode == 0) { Progress = 100; StatusTitle = ReportStatus.Equals("passed", StringComparison.OrdinalIgnoreCase) ? "Album completed and verified" : "Codex finished — review the report"; StatusDetail = await FinalMessage(result.FinalMessagePath) ?? StatusDetail; Log("DONE", $"Report status: {ReportStatus}"); }
            else Apply(new(JobPhase.Failed, (int)Progress, "failed", $"Codex exited with code {result.ExitCode}. Incomplete jobs retain originals.", DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) { monitorCancel.Cancel(); await IgnoreCancel(monitor); Apply(new(JobPhase.Canceled, (int)Progress, "canceled", "Run canceled. Inspect staging before resuming.", DateTimeOffset.UtcNow)); }
        catch (Exception error) { monitorCancel.Cancel(); await IgnoreCancel(monitor); Apply(new(JobPhase.Failed, (int)Progress, "failed", error.Message, DateTimeOffset.UtcNow)); Log("ERROR", error.Message); await LoadReportAsync(); }
        finally { _cancel.Dispose(); _cancel = null; Busy = false; }
    }

    private async Task MonitorAsync(string path, CancellationToken token)
    {
        var last = DateTime.MinValue;
        while (!token.IsCancellationRequested)
        {
            try { if (File.Exists(path) && File.GetLastWriteTimeUtc(path) > last) { var text = await File.ReadAllTextAsync(path, token); if (CodexRunner.TryProgress(text, out var progress)) { Apply(progress); last = File.GetLastWriteTimeUtc(path); await LoadReportAsync(); } } }
            catch (IOException error) { Log("WAIT", error.Message); }
            await Task.Delay(600, token);
        }
    }

    private async Task LoadReportAsync()
    {
        var path = Path.Combine(AlbumPath, "conversion-report.json"); if (!File.Exists(path)) return;
        try { var report = await ReportReader.LoadAsync(path); ReportHeadline = report.Headline; ReportDetail = report.Detail; ReportJson = report.Json; ReportStatus = report.Status; ReportTracks = report.Tracks.ToString(); ReportSections = report.Sections.ToString(); ReportDisposition = report.Deleted ? "Deleted after proof" : "Retained"; Replace(ReportErrors, report.Errors); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { ReportHeadline = "Report is not readable yet"; ReportDetail = error.Message; }
    }

    private void OnRunEvent(RunEvent item) { if (item.ThreadId is not null) ThreadId = item.ThreadId; if (item.Progress is not null) Apply(item.Progress); if (item.Message.Length > 0) Log(item.Kind.ToUpperInvariant(), item.Message); }
    private void Apply(ProgressSnapshot snapshot)
    {
        Progress = Math.Max(Progress, snapshot.Percent); StatusTitle = PhaseTitle(snapshot.Phase); StatusDetail = snapshot.Detail;
        var number = snapshot.Phase is >= JobPhase.Inventoried and <= JobPhase.CleanupCompleted ? (int)snapshot.Phase : Timeline.FirstOrDefault(x => x.State == "Active")?.Number ?? 0;
        foreach (var item in Timeline) item.State = snapshot.Phase == JobPhase.Failed && item.Number == number ? "Failed" : snapshot.Phase == JobPhase.Canceled && item.Number == number ? "Canceled" : item.Number < number ? "Complete" : item.Number == number ? snapshot.Phase == JobPhase.CleanupCompleted ? "Complete" : "Active" : "Pending";
    }

    private void Cancel() { if (!Busy) return; StatusTitle = "Stopping at the next safe boundary…"; StatusDetail = "An incomplete job cannot authorize source deletion."; Log("CANCEL", "Cancellation requested."); _cancel?.Cancel(); _runner.Cancel(); }
    private void OpenAlbum() { if (Directory.Exists(AlbumPath)) Process.Start(new ProcessStartInfo(AlbumPath) { UseShellExecute = true }); }
    private void CopyReport() { if (ReportJson.Length > 0) Clipboard.SetText(ReportJson); }
    private void Invalidate() { if (Busy) return; _scan = null; _preflight = null; Checks.Clear(); Media.Clear(); AlbumName = string.IsNullOrWhiteSpace(AlbumPath) ? "No album selected" : Path.GetFileName(AlbumPath.TrimEnd(Path.DirectorySeparatorChar)); Workflow = "Scan to classify this album"; Inventory = SourceSize = "—"; StatusTitle = "Ready to scan"; StatusDetail = "Inventory is read-only."; Raise(nameof(CanStart)); StartCommand.Refresh(); }
    private void Log(string kind, string message) { Activity.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), kind, message.ReplaceLineEndings(" "))); while (Activity.Count > 250) Activity.RemoveAt(Activity.Count - 1); }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items) { target.Clear(); foreach (var item in items) target.Add(item); }
    private static string S(int count) => count == 1 ? "" : "s";
    private static string PhaseTitle(JobPhase phase) => phase switch { JobPhase.Inventoried => "Inventory complete", JobPhase.CopyingIn => "Copying into local staging…", JobPhase.SourceCopyVerified => "Source copy verified", JobPhase.Processing => "Splitting or extracting…", JobPhase.Tagging => "Writing metadata and artwork…", JobPhase.LocalVerificationPassed => "Local verification passed", JobPhase.CopyingBack => "Copying verified output back…", JobPhase.NetworkHashesVerified => "Network-side hashes verified", JobPhase.FinalCommit => "Committing final files…", JobPhase.FinalVerificationPassed => "Final-path verification passed", JobPhase.SourceDisposition => "Recording source disposition…", JobPhase.CleanupCompleted => "Album completed", JobPhase.Failed => "Run stopped safely", JobPhase.Canceled => "Run canceled", _ => "Preparing the job…" };
    private static async Task<string?> FinalMessage(string path) { if (!File.Exists(path)) return null; var text = (await File.ReadAllTextAsync(path, Encoding.UTF8)).Trim(); return text.Length <= 800 ? text : text[..799] + "…"; }
    private static async Task IgnoreCancel(Task task) { try { await task; } catch (OperationCanceledException) { } }
    public void Dispose() { _cancel?.Cancel(); _cancel?.Dispose(); _runner.Dispose(); GC.SuppressFinalize(this); }
}
