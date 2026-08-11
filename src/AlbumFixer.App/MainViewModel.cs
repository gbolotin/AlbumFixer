using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using AlbumFixer.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlbumFixer.App;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AlbumScanner _scanner;
    private readonly PreflightService _preflightService;
    private readonly HostStagingService _staging;
    private readonly LocalFlacProcessor _localProcessor;
    private readonly LocalMetadataEnrichmentService _metadataEnrichment;
    private readonly LocalDsdProcessor _localDsdProcessor;
    private readonly HostCommitService _commit;
    private readonly StartupPrerequisiteService startupPrerequisites;
    private readonly IUserInteractionService _userInteraction;
    private readonly IUiTimer _progressTimer;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private CancellationTokenSource? _cancel;
    private ScanResult? _scan;
    private IReadOnlyList<ScanResult> _scans = [];
    private IReadOnlyList<AlbumPreflightResult> _albumPreflights = [];
    private PreflightResult? _preflight;
    private readonly Dictionary<int, ProgressSnapshot> _jobProgress = [];
    private readonly Dictionary<int, CheckRow> _albumCheckRows = [];
    private readonly Dictionary<int, long> _activeAlbumActivity = [];
    private string _albumName = "No source folders selected";
    private string _workflow = "Choose one or more source folders to begin";
    private string _inventory = "—";
    private string _sourceSize = "—";
    private string _statusTitle = "Ready for a safe run";
    private string _statusDetail = "Inventory first. Originals stay in place whenever a proof is missing.";
    private double _progress;
    private string _progressTime = "Elapsed —";
    private bool _busy;
    private bool _isRunActive;
    private bool _deleteOriginals = true;
    private string _reportHeadline = "No conversion report yet";
    private string _reportDetail = "A validated summary and JSON report will appear here.";
    private string _reportJson = "";
    private string _reportStatus = "Pending";
    private string _reportTracks = "—";
    private string _reportSections = "—";
    private string _reportDisposition = "—";
    private string _jobDirectory = "—";
    private JobPhase _lastPhase = JobPhase.Ready;
    private string _lastRunDetail = "No run has started.";
    private DateTimeOffset _runStartedAt;
    private int _previousOutputFileCount;
    private bool _addingSourceFolders;
    private long _albumActivitySequence;
    private bool startupChecked;

    public MainViewModel(
        AlbumScanner scanner,
        PreflightService preflightService,
        HostStagingService staging,
        LocalFlacProcessor localProcessor,
        LocalMetadataEnrichmentService metadataEnrichment,
        LocalDsdProcessor localDsdProcessor,
        HostCommitService commit,
        StartupPrerequisiteService startupPrerequisites,
        IUserInteractionService userInteraction,
        IUiTimer progressTimer)
    {
        _scanner = scanner;
        _preflightService = preflightService;
        _staging = staging;
        _localProcessor = localProcessor;
        _metadataEnrichment = metadataEnrichment;
        _localDsdProcessor = localDsdProcessor;
        _commit = commit;
        this.startupPrerequisites = startupPrerequisites;
        _userInteraction = userInteraction;
        _progressTimer = progressTimer;

        _progressTimer.Tick += ProgressTimer_Tick;

        BrowseCommand = new AsyncRelayCommand(BrowseAsync, () => CanBrowse);
        RemoveSourceFolderCommand = new AsyncRelayCommand<string>(RemoveSourceFolderAsync, CanRemoveSourceFolder);
        OpenAlbumFolderCommand = new AsyncRelayCommand<string>(OpenAlbumFolderAsync, CanOpenAlbumFolder);
        ScanCommand = new AsyncRelayCommand(ScanAsync, () => CanBrowse && SourceFolders.Count > 0);
        StartCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        CancelCommand = new RelayCommand(Cancel, () => IsRunActive);
        RefreshReportCommand = new AsyncRelayCommand(LoadReportAsync, () => !Busy && SourceFolders.Count > 0);
        ClearSourceFoldersCommand = new RelayCommand(ClearSourceFolders, () => CanBrowse && SourceFolders.Count > 0);
        CopyReportCommand = new RelayCommand(CopyReport, () => ReportJson.Length > 0);

        var titles = new[] { "Inventoried", "Preparing source", "Source size checked", "Split / extract", "Tagging", "Local verification", "Copying back", "Destination sizes", "Final commit", "Final verification", "Source disposition", "Cleanup" };
        for (var i = 0; i < titles.Length; i++) Timeline.Add(new() { Number = i + 1, Phase = (JobPhase)(i + 1), Title = titles[i] });
    }

    public ObservableCollection<string> SourceFolders { get; } = [];
    public RangeObservableCollection<CheckRow> PreflightChecks { get; } = [];
    public RangeObservableCollection<CheckRow> Albums { get; } = [];
    public RangeObservableCollection<MediaRow> Media { get; } = [];
    public ObservableCollection<TimelineRow> Timeline { get; } = [];
    public ObservableCollection<ActivityRow> Activity { get; } = [];
    public RangeObservableCollection<string> ReportErrors { get; } = [];
    public IAsyncRelayCommand BrowseCommand { get; }
    public IAsyncRelayCommand<string> RemoveSourceFolderCommand { get; }
    public IAsyncRelayCommand<string> OpenAlbumFolderCommand { get; }
    public IAsyncRelayCommand ScanCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IAsyncRelayCommand RefreshReportCommand { get; }
    public IRelayCommand ClearSourceFoldersCommand { get; }
    public IRelayCommand CopyReportCommand { get; }

    public int SourceFolderCount => SourceFolders.Count;
    public string? BrowseInitialDirectory => SourceFolders.LastOrDefault();
    public string AlbumName { get => _albumName; private set => SetProperty(ref _albumName, value); }
    public string Workflow { get => _workflow; private set => SetProperty(ref _workflow, value); }
    public string Inventory { get => _inventory; private set => SetProperty(ref _inventory, value); }
    public string SourceSize { get => _sourceSize; private set => SetProperty(ref _sourceSize, value); }
    public string StatusTitle { get => _statusTitle; private set => SetProperty(ref _statusTitle, value); }
    public string StatusDetail { get => _statusDetail; private set => SetProperty(ref _statusDetail, value); }
    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }
    public string ProgressTime { get => _progressTime; private set => SetProperty(ref _progressTime, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanBrowse));
            NotifyCommandStates();
        }
    }

    public bool IsRunActive
    {
        get => _isRunActive;
        private set
        {
            if (SetProperty(ref _isRunActive, value))
                CancelCommand.NotifyCanExecuteChanged();
        }
    }

    public bool DeleteOriginals
    {
        get => _deleteOriginals;
        set
        {
            if (!SetProperty(ref _deleteOriginals, value)) return;
            OnPropertyChanged(nameof(DeletesSourceAfterSuccess));
            OnPropertyChanged(nameof(DeletesAnySourceAfterSuccess));
        }
    }
    public bool CanBrowse => !Busy && !_addingSourceFolders;
    public bool CanStart => !Busy && _scans.Count > 0 && _preflight?.CanStart == true;
    public int AlbumCount => _scans.Count;
    public int RunnableAlbumCount => _albumPreflights.Count(album => album.CanStart);
    public BatchPipelineLimits BatchPipeline => PreflightService.PipelineLimits(_albumPreflights);
    public int BatchWorkerLimit => BatchPipeline.MaxInFlight;
    public string BatchPipelineDescription => BatchPipeline.Description;
    public int PreviousOutputFileCount => _previousOutputFileCount;
    public bool IsBatch => AlbumCount > 1;
    public bool IsSingleSacd => AlbumCount == 1 && _scans[0].Mode == WorkflowMode.DsdExtraction;
    public bool HasSacdWorkflows => _scans.Any(scan => scan.Mode == WorkflowMode.DsdExtraction);
    public bool DeletesSourceAfterSuccess => DeleteOriginals && RunnableAlbumCount > 0 && _albumPreflights.Where(album => album.CanStart).All(album => album.Scan is { ImageCount: 1, TrackCount: 0 });
    public bool DeletesAnySourceAfterSuccess => DeleteOriginals && _albumPreflights.Any(album => album.CanStart && album.Scan is { ImageCount: 1, TrackCount: 0 });
    private string SourceActionDetail => IsBatch
        ? $"Hardware-aware pipeline: {BatchPipelineDescription}. Every album remains isolated, blocked albums are skipped, and SACD areas stay sequential inside each album. " +
          (DeleteOriginals ? "Failed, canceled, or artwork-incomplete albums retain their originals." : "Delete originals is off; every original will be retained.")
        : !DeleteOriginals
            ? "Delete originals is off. Verified output will be committed and every original source will be retained."
        : IsSingleSacd
            ? "Every reported SACD area is extracted to DSF twice and compared by file size. Tags, artwork, DSD structure, and final network file sizes are checked before the exact ISO may be deleted."
        : DeletesSourceAfterSuccess
            ? "PCM/MD5 and cryptographic hash comparisons are skipped. Album Fixer deletes only the exact inventoried image, and only after tracks are committed and pass quick FLAC, tag, artwork, and file-size checks. If artwork cannot be completed, usable tracks are delivered as incomplete work and the image is retained."
            : "PCM/MD5 comparison is skipped. Multiple original images are retained after tracks are committed and pass quick checks.";
    public string ReportHeadline { get => _reportHeadline; private set => SetProperty(ref _reportHeadline, value); }
    public string ReportDetail { get => _reportDetail; private set => SetProperty(ref _reportDetail, value); }

    public string ReportJson
    {
        get => _reportJson;
        private set
        {
            if (SetProperty(ref _reportJson, value))
                CopyReportCommand.NotifyCanExecuteChanged();
        }
    }

    public string ReportStatus { get => _reportStatus; private set => SetProperty(ref _reportStatus, value); }
    public string ReportTracks { get => _reportTracks; private set => SetProperty(ref _reportTracks, value); }
    public string ReportSections { get => _reportSections; private set => SetProperty(ref _reportSections, value); }
    public string ReportDisposition { get => _reportDisposition; private set => SetProperty(ref _reportDisposition, value); }
    public string JobDirectory { get => _jobDirectory; private set => SetProperty(ref _jobDirectory, value); }

    public async Task InitializeAsync()
    {
        if (startupChecked) return;
        startupChecked = true;
        Busy = true;
        StatusTitle = "Checking required components…";
        StatusDetail = "Album Fixer will verify bundled components and install FFmpeg if it is missing.";

        try
        {
            var progress = new Progress<string>(detail => StatusDetail = detail);
            var result = await startupPrerequisites.EnsureInstalledAsync(progress, lifetimeCancellation.Token);
            if (result.Succeeded)
            {
                StatusTitle = "Required components are ready";
                StatusDetail = "ffmpeg, ffprobe, and sacd_extract are available.";
                Log("READY", StatusDetail);
                return;
            }

            StatusTitle = "Some components could not be installed";
            StatusDetail = "Affected workflows will remain blocked until the missing components are installed.";
            foreach (var failure in result.Failures) Log("COMPONENT", failure);
            _userInteraction.ShowError(
                "Component installation failed",
                "Album Fixer could not install these required components:\n\n" +
                string.Join("\n\n", result.Failures.Select(failure => "• " + failure)) +
                "\n\nYou can continue using the app, but workflows that need these components will be blocked. Album Fixer will try again the next time it starts.");
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            StatusTitle = "Component check failed";
            StatusDetail = error.Message;
            Log("COMPONENT", error.Message);
            _userInteraction.ShowError(
                "Component check failed",
                $"Album Fixer could not complete its startup component check.\n\n{error.Message}\n\nThe app will remain open, but affected workflows may be blocked.");
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task BrowseAsync()
    {
        var folder = _userInteraction.SelectSourceFolder(BrowseInitialDirectory);
        if (folder is not null)
            await AddSourceFoldersAsync([folder]);
    }

    private Task OpenAlbumFolderAsync(string? folder) =>
        string.IsNullOrWhiteSpace(folder)
            ? Task.CompletedTask
            : _userInteraction.OpenFolderAsync(folder);

    private bool CanRemoveSourceFolder(string? folder) =>
        CanBrowse && !string.IsNullOrWhiteSpace(folder);

    private static bool CanOpenAlbumFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder);

    public async Task AddSourceFoldersAsync(IEnumerable<string> folders)
    {
        if (!CanBrowse) return;
        var shouldScan = false;
        _addingSourceFolders = true;
        OnPropertyChanged(nameof(CanBrowse));
        NotifyCommandStates();
        var added = 0;
        var collapsed = 0;
        var ignored = 0;
        try
        {
            var candidates = folders.ToArray();
            var validation = await Task.Run(() =>
            {
                var available = new List<string>();
                var unavailable = 0;
                foreach (var candidate in candidates)
                {
                    var normalized = NormalizeSourceFolder(candidate);
                    if (normalized is null || !Directory.Exists(normalized)) unavailable++;
                    else available.Add(normalized);
                }
                return (Available: available, Unavailable: unavailable);
            });
            if (Busy) return;
            ignored = validation.Unavailable;
            foreach (var normalized in validation.Available)
            {
                if (SourceFolders.Any(existing => IsSameOrNestedFolder(normalized, existing)))
                {
                    ignored++;
                    continue;
                }

                var nestedSources = SourceFolders.Where(existing => IsSameOrNestedFolder(existing, normalized)).ToArray();
                foreach (var nestedSource in nestedSources) SourceFolders.Remove(nestedSource);
                collapsed += nestedSources.Length;
                SourceFolders.Add(normalized);
                added++;
            }

            if (added == 0)
            {
                if (ignored > 0)
                {
                    StatusTitle = "No source folders added";
                    StatusDetail = $"{ignored} duplicate or unavailable selection{S(ignored)} {(ignored == 1 ? "was" : "were")} ignored.";
                    Log("SOURCE", StatusDetail);
                }
                return;
            }
            Invalidate();
            StatusTitle = "Source folders ready to scan";
            StatusDetail = $"{SourceFolderCount} parent folder{S(SourceFolderCount)} will be scanned recursively.";
            if (collapsed > 0) StatusDetail += $" {collapsed} nested selection{S(collapsed)} already covered by a parent folder {(collapsed == 1 ? "was" : "were")} removed.";
            if (ignored > 0) StatusDetail += $" {ignored} duplicate or unavailable selection{S(ignored)} {(ignored == 1 ? "was" : "were")} ignored.";
            Log("SOURCE", StatusDetail);
            shouldScan = true;
        }
        finally
        {
            _addingSourceFolders = false;
            OnPropertyChanged(nameof(CanBrowse));
            NotifyCommandStates();
        }
        if (shouldScan && SourceFolders.Count > 0 && !Busy) await ScanAsync();
    }

    private async Task ScanAsync()
    {
        Busy = true; StatusTitle = "Inventorying albums…"; StatusDetail = "Reading media, CUE references, artwork, and provenance without changing files."; Log("SCAN", "Read-only inventory started.");
        try
        {
            var sourceScans = new List<(string Folder, ScanResult RootScan, IReadOnlyList<ScanResult> Albums)>();
            foreach (var sourceFolder in SourceFolders)
            {
                var rootScan = await _scanner.ScanAsync(sourceFolder);
                var albums = rootScan.Mode == WorkflowMode.MultipleAlbums
                    ? await _scanner.ScanAlbumsAsync(sourceFolder)
                    : [rootScan];
                sourceScans.Add((sourceFolder, rootScan, albums));
            }
            var discoveredScans = sourceScans
                .SelectMany(source => source.Albums)
                .GroupBy(scan => scan.AlbumRoot, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var completedScans = discoveredScans.Where(scan => !scan.RequiresProcessing).ToArray();
            _scans = discoveredScans.Where(scan => scan.RequiresProcessing).ToArray();
            var firstRootScan = sourceScans[0].RootScan;
            _scan = _scans.Count == 1 ? _scans[0] : completedScans.Length == 1 && _scans.Count == 0 ? completedScans[0] : firstRootScan;
            Replace(Media, sourceScans.SelectMany(source => source.RootScan.Media.Select(item => new MediaRow(
                SourceFolderCount == 1 ? item.RelativePath : Path.Combine(source.Folder, item.RelativePath),
                item.Kind, item.Size > 0 ? SizeText.Format(item.Size) : "—", item.Note))));
            if (_scans.Count == 0 && completedScans.Length > 0)
            {
                _albumPreflights = [];
                _preflight = null;
                _previousOutputFileCount = 0;
                _albumCheckRows.Clear();
                PreflightChecks.Clear();
                Albums.Clear();
                AlbumName = completedScans.Length == 1 ? completedScans[0].AlbumName : $"{completedScans.Length} completed albums";
                Workflow = completedScans.Length == 1 ? completedScans[0].WorkflowLabel : "Already completed — no pending work";
                Inventory = $"{completedScans.Length} completed album{S(completedScans.Length)}  •  0 pending";
                SourceSize = "—";
                StatusTitle = "No work needed";
                StatusDetail = $"{completedScans.Length} report-confirmed completed album{S(completedScans.Length)} skipped. Verified outputs and preserved provenance remain unchanged.";
                Log("COMPLETE", StatusDetail);
                NotifyAlbumStateChanged();
                await LoadReportAsync();
                return;
            }
            AlbumName = IsBatch
                ? SourceFolderCount == 1 ? $"{firstRootScan.AlbumName} ({AlbumCount} albums)" : $"{AlbumCount} albums from {SourceFolderCount} source folder{S(SourceFolderCount)}"
                : _scan.AlbumName;
            Workflow = IsBatch ? $"Parallel album batch ({AlbumCount} discovered across {SourceFolderCount} source folder{S(SourceFolderCount)})" : _scan.WorkflowLabel;
            var images = _scans.Sum(scan => scan.ImageCount);
            var tracks = _scans.Sum(scan => scan.TrackCount);
            var cues = _scans.Sum(scan => scan.CueCount);
            Inventory = $"{AlbumCount} album{S(AlbumCount)}  •  {images} image{S(images)}  •  {tracks} track{S(tracks)}  •  {cues} CUE" +
                (completedScans.Length > 0 ? $"  •  {completedScans.Length} completed skipped" : "");
            SourceSize = SizeText.Format(_scans.Sum(scan => scan.SourceBytes));
            StatusTitle = "Checking safe-run prerequisites…";
            var scans = _scans;
            var preflightLoad = await Task.Run(async () =>
            {
                var albums = await _preflightService.CheckAlbumsAsync(scans).ConfigureAwait(false);
                var previousOutputFileCount = albums.Sum(album =>
                    PreviousOutputCleanupService.Discover(album.Scan.AlbumRoot)?.Files.Count ?? 0);
                return (Albums: albums, PreviousOutputFileCount: previousOutputFileCount);
            });
            _albumPreflights = preflightLoad.Albums;
            _previousOutputFileCount = preflightLoad.PreviousOutputFileCount;
            _preflight = PreflightService.CombineBatch(_albumPreflights);
            _albumCheckRows.Clear();
            _activeAlbumActivity.Clear();
            _albumActivitySequence = 0;
            var preflightRows = new List<CheckRow>();
            var albumRows = new List<CheckRow>();
            foreach (var item in _preflight.Checks)
            {
                var album = _albumPreflights.FirstOrDefault(candidate =>
                    item.Name.Equals($"Album {candidate.Index + 1}: {candidate.Scan.AlbumName}", StringComparison.Ordinal));
                var row = new CheckRow(item.Name,
                    album is not null ? album.CanStart ? "Ready" : "Blocked" : item.State switch { CheckState.Passed => "Ready", CheckState.Warning => "Review", _ => "Blocked" },
                    item.Detail, item.State, album?.Index, album?.Scan.AlbumRoot);
                if (album is null)
                {
                    preflightRows.Add(row);
                }
                else
                {
                    albumRows.Add(row);
                    _albumCheckRows[album.Index] = row;
                }
            }
            Replace(PreflightChecks, preflightRows);
            Replace(Albums, albumRows);
            NotifyAlbumStateChanged();
            var blocked = AlbumCount - RunnableAlbumCount;
            StatusTitle = _preflight.CanStart ? (IsBatch ? "Batch ready to process" : "Ready to process") : "Run blocked safely";
            StatusDetail = _preflight.CanStart
                ? IsBatch
                    ? $"{RunnableAlbumCount} album{S(RunnableAlbumCount)} ready; {blocked} blocked album{S(blocked)} will be skipped."
                    : "Every blocking preflight check passed. The source remains until final verification."
                : "Resolve the blocked checks below. No album files were changed.";
            if (completedScans.Length > 0)
                StatusDetail += $" {completedScans.Length} already completed album{S(completedScans.Length)} skipped.";
            Log(_preflight.CanStart ? "READY" : "BLOCKED", StatusDetail); await LoadReportAsync();
        }
        catch (Exception error) { _scan = null; _scans = []; _albumPreflights = []; _preflight = null; _previousOutputFileCount = 0; _albumCheckRows.Clear(); PreflightChecks.Clear(); Albums.Clear(); StatusTitle = "Could not inventory this folder"; StatusDetail = error.Message; Log("ERROR", error.Message); }
        finally
        {
            Busy = false;
            OnPropertyChanged(nameof(CanStart));
            StartCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task StartAsync()
    {
        var runnable = _albumPreflights.Where(album => album.CanStart).ToArray();
        if (!CanStart || runnable.Length == 0 || _preflight is null) return;

        var confirmation = new StartConfirmation(
            DeleteOriginals,
            IsBatch,
            IsSingleSacd,
            DeletesSourceAfterSuccess,
            PreviousOutputFileCount);
        if (!_userInteraction.ConfirmStart(confirmation)) return;
        var deleteOriginals = DeleteOriginals;
        IsRunActive = true; Busy = true; Progress = 1; foreach (var item in Timeline) item.State = "Pending"; _cancel = new();
        _jobProgress.Clear();
        _activeAlbumActivity.Clear();
        _albumActivitySequence = 0;
        for (var index = 0; index < runnable.Length; index++)
            _jobProgress[index] = new(JobPhase.Ready, 0, "pending", "Waiting for a batch worker.", DateTimeOffset.UtcNow);
        foreach (var album in runnable)
            if (_albumCheckRows.TryGetValue(album.Index, out var row))
            {
                row.State = "Queued · 0%";
                row.Detail = "Waiting for an available album worker.";
                row.RawState = CheckState.Warning;
            }
        _lastPhase = JobPhase.Ready;
        _lastRunDetail = "Starting the safe run.";
        _runStartedAt = DateTimeOffset.UtcNow;
        RefreshProgressTime();
        _progressTimer.Start();
        ReportStatus = "Pending"; ReportTracks = ReportSections = ReportDisposition = "—"; ReportJson = "";
        ReportHeadline = "Run in progress"; ReportDetail = "A terminal report will be preserved even if the run stops.";
        JobDirectory = PreflightService.CreateJobDirectory(_preflight.TempRoot);
        var pipelineLimits = PreflightService.PipelineLimits(_albumPreflights);
        var workerLimit = pipelineLimits.MaxInFlight;
        using var pipeline = new BatchPipelineScheduler(pipelineLimits);
        StatusTitle = IsBatch ? "Starting parallel album workers…" : IsSingleSacd ? "Starting verified SACD extraction…" : "Starting the fast workflow…";
        StatusDetail = IsBatch
            ? $"{runnable.Length} admitted album{S(runnable.Length)}; {pipelineLimits.Description}. {AlbumCount - runnable.Length} blocked album{S(AlbumCount - runnable.Length)} will be skipped."
            : SourceActionDetail;
        Log("START", JobDirectory);
        try
        {
            IProgress<JobUiUpdate> uiUpdates = new Progress<JobUiUpdate>(ApplyJobUpdate);
            var runToken = _cancel.Token;
            var results = await Task.Run(() => BoundedBatchProcessor.RunAsync<AlbumPreflightResult, AlbumJobOutcome>(
                runnable,
                (album, index, token) => ProcessAlbumAsync(album.Scan, index, album.Index, pipeline, uiUpdates, deleteOriginals, token),
                workerLimit,
                runToken));

            if (IsBatch)
                await Task.Run(() => WriteBatchReportAsync(results, runToken.IsCancellationRequested, pipelineLimits, pipeline.Telemetry, deleteOriginals));
            await LoadReportAsync();
            var succeeded = results.Count(result => result.Succeeded);
            var failed = results.Count(result => !result.Succeeded && !result.Canceled);
            var canceled = results.Count(result => result.Canceled);
            var tracks = results.Where(result => result.Succeeded).Sum(result => result.Value!.Tracks);
            var deleted = results.Count(result => result.Succeeded && result.Value!.SourcesDeleted);
            var incomplete = results.Count(result => result.Succeeded && result.Value!.Incomplete);
            var blocked = AlbumCount - runnable.Length;

            if (runToken.IsCancellationRequested || canceled > 0)
            {
                var detail = IsBatch
                    ? $"Batch canceled after {succeeded} of {runnable.Length} admitted album{S(runnable.Length)} completed. Unfinished and blocked albums retained their originals."
                    : "Run canceled. The unfinished album transaction retained its original source.";
                Apply(new(JobPhase.Canceled, (int)Progress, "canceled", detail, DateTimeOffset.UtcNow));
                Log("CANCELED", detail);
            }
            else if (failed > 0)
            {
                var detail = IsBatch
                    ? $"{succeeded} of {runnable.Length} admitted album{S(runnable.Length)} completed; {failed} failed and {blocked} preflight-blocked. Other albums were not interrupted."
                    : results[0].Error?.Message ?? "The album transaction failed and retained its original source.";
                Apply(new(JobPhase.Failed, (int)Progress, "failed", detail, DateTimeOffset.UtcNow));
                Log("ERROR", detail);
            }
            else
            {
                var completionStatus = incomplete > 0 ? "incomplete" : blocked > 0 ? "partial_success" : "passed";
                Apply(new(JobPhase.CleanupCompleted, 100, completionStatus, $"{succeeded} album{S(succeeded)} delivered; {incomplete} incomplete album{S(incomplete)} retained source artwork work; {blocked} blocked album{S(blocked)} skipped.", DateTimeOffset.UtcNow));
                StatusTitle = incomplete > 0
                    ? IsBatch ? "Batch completed with incomplete artwork" : "Tracks delivered · artwork incomplete"
                    : IsBatch ? "Batch completed with quick checks" : "Album completed with quick checks";
                StatusDetail = $"{tracks} track{S(tracks)} passed quick checks across {succeeded} album{S(succeeded)}; {incomplete} incomplete; {blocked} blocked; {deleted} source image{S(deleted)} deleted.";
                Log(incomplete > 0 ? "INCOMPLETE" : "DONE", $"{succeeded} albums delivered, {incomplete} incomplete, {blocked} skipped, {tracks} tracks passed.");
            }
        }
        catch (Exception error)
        {
            Apply(new(JobPhase.Failed, (int)Progress, "failed", error.Message, DateTimeOffset.UtcNow)); Log("ERROR", error.Message);
            if (_scans.Count == 1) await EnsureTerminalReportAsync(false);
            await LoadReportAsync();
        }
        finally
        {
            if (_preflight is not null && !string.IsNullOrWhiteSpace(JobDirectory))
            {
                try
                {
                    var cleaned = await WorkflowCleanupService.CleanupLocalJobAsync(JobDirectory, _preflight.TempRoot);
                    Log("CLEANUP", cleaned ? "Removed the Album Fixer Temp job." : $"Could not remove the Album Fixer Temp job: {JobDirectory}");
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    Log("CLEANUP", $"Could not remove the Album Fixer Temp job: {error.Message}");
                }
            }
            _progressTimer.Stop(); RefreshProgressTime(); _cancel.Dispose(); _cancel = null; IsRunActive = false; Busy = false;
        }
    }

    private async Task<AlbumJobOutcome> ProcessAlbumAsync(
        ScanResult scan,
        int index,
        int albumIndex,
        BatchPipelineScheduler pipeline,
        IProgress<JobUiUpdate> uiUpdates,
        bool deleteOriginals,
        CancellationToken token)
    {
        if (_preflight is null) throw new InvalidOperationException("Batch preflight is unavailable.");
        var jobDirectory = IsBatch
            ? Path.Combine(JobDirectory, $"album-{albumIndex + 1:000}")
            : JobDirectory;
        Directory.CreateDirectory(jobDirectory);
        var last = new ProgressSnapshot(JobPhase.Ready, 0, "pending", "Waiting for a batch worker.", DateTimeOffset.UtcNow);
        void Report(ProgressSnapshot snapshot)
        {
            last = snapshot;
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Progress: snapshot));
        }
        var progress = new CallbackProgress<ProgressSnapshot>(Report);
        var sourceCacheRequired = HostStagingService.RequiresSourceCache(scan.AlbumRoot);

        try
        {
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "START", Message: $"Isolated transaction: {jobDirectory}"));
            Report(new(JobPhase.Ready, 0, "queued", sourceCacheRequired
                ? "Waiting for an available NAS source-cache lane."
                : "Waiting for an available local source-verification lane; no Temp source copy is required.", DateTimeOffset.UtcNow));
            var staged = await pipeline.RunCopyInAsync(
                ct => _staging.StageAsync(scan, _preflight, jobDirectory, progress, ct), token);
            staged = staged with { PipelineLimits = pipeline.Limits };
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "SPLIT", Message: scan.Mode == WorkflowMode.DsdExtraction
                ? "Starting sequential SACD extraction and independent DSD verification."
                : "Starting the deterministic local CUE/FFmpeg splitter."));
            Report(new(JobPhase.SourceCopyVerified, Math.Max(last.Percent, 17), "queued", staged.SourceCacheUsed
                ? "Temp source cache verified; waiting for a local processing lane."
                : "Fixed-disk source size recorded; waiting for a local processing lane.", DateTimeOffset.UtcNow));
            var localResult = await pipeline.RunProcessingAsync(scan.Mode == WorkflowMode.DsdExtraction, ct =>
                scan.Mode == WorkflowMode.DsdExtraction
                    ? _localDsdProcessor.ProcessAsync(scan, staged, progress, ct)
                    : _localProcessor.ProcessAsync(scan, staged, progress, ct), token);
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "SPLIT", Message: $"Local processing completed: {localResult.Tracks} tracks."));

            var gaps = localResult.Metadata;
            if (gaps.RequiresResearch)
            {
                var fields = string.Join(", ", gaps.MissingFields);
                if (scan.Mode == WorkflowMode.FlacCueSplit)
                {
                    uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "METADATA", Message: $"Running deterministic local metadata enrichment for: {fields}."));
                    gaps = await _metadataEnrichment.EnrichAsync(scan, staged, localResult, progress, token);
                    var unresolvedRequired = gaps.MissingFields
                        .Where(field => !field.Equals("COVER", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    if (unresolvedRequired.Length > 0)
                        throw new InvalidOperationException($"Required FLAC metadata remains unresolved after local lookup ({string.Join(", ", unresolvedRequired)}). Local results are preserved at {staged.AlbumRoot}.");
                    if (gaps.RequiresResearch)
                        uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "METADATA", Message: "Front artwork remains unavailable. Verified tracks will be delivered as incomplete work and the source image will be retained."));
                    else
                        uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "METADATA", Message: "Missing metadata and artwork were completed by deterministic local code."));
                }
                else
                {
                    uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "METADATA",
                        Message: $"Catalog lookup left these SACD fields unresolved: {fields}. Verified tracks will be delivered as incomplete work and the source ISO will be retained."));
                }
            }
            else
            {
                uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "METADATA", Message: "All required metadata and artwork were found locally; no fallback process was needed."));
            }

            Report(new(JobPhase.LocalVerificationPassed, Math.Max(last.Percent, 50), "queued", "Local processing is ready for host verification and an available NAS write-back lane.", DateTimeOffset.UtcNow));
            var committed = await pipeline.RunCopyBackAsync(ct =>
            {
                var commitStaged = staged with { PipelineTelemetry = pipeline.Telemetry };
                return _commit.CommitAsync(scan, commitStaged, progress, deleteOriginals, ct);
            }, token);
            return new(jobDirectory, committed.ReportPath, committed.Tracks, committed.SourcesDeleted, committed.Incomplete);
        }
        catch (OperationCanceledException error)
        {
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Progress: new(JobPhase.Canceled, last.Percent, "canceled", error.Message, DateTimeOffset.UtcNow)));
            await EnsureAlbumTerminalReportAsync(scan, jobDirectory, last with { Detail = error.Message }, true, uiUpdates, index, albumIndex);
            throw;
        }
        catch (Exception error)
        {
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Progress: new(JobPhase.Failed, last.Percent, "failed", error.Message, DateTimeOffset.UtcNow)));
            await EnsureAlbumTerminalReportAsync(scan, jobDirectory, last with { Detail = error.Message }, false, uiUpdates, index, albumIndex);
            throw;
        }
        finally
        {
            try
            {
                var cleaned = await WorkflowCleanupService.CleanupLocalJobAsync(jobDirectory, _preflight.TempRoot);
                uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "CLEANUP", Message: cleaned
                    ? "Removed all transient files for this album transaction."
                    : $"Could not remove the transient album job: {jobDirectory}"));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "CLEANUP", Message: $"Could not remove the transient album job: {error.Message}"));
            }
        }
    }

    private void ApplyJobUpdate(JobUiUpdate update)
    {
        if (update.Kind is not null && update.Message is not null)
        {
            Log(update.Kind, $"[{update.AlbumName}] {update.Message}");
        }
        if (update.Progress is null) return;
        _jobProgress[update.Index] = update.Progress;
        if (_albumCheckRows.TryGetValue(update.AlbumIndex, out var albumRow))
        {
            albumRow.State = AlbumProgressLabel(update.Progress);
            albumRow.Detail = update.Progress.Detail;
            albumRow.RawState = update.Progress.Phase switch
            {
                JobPhase.Failed => CheckState.Failed,
                JobPhase.Canceled => CheckState.Warning,
                JobPhase.CleanupCompleted when update.Progress.Status.Equals("incomplete", StringComparison.OrdinalIgnoreCase) => CheckState.Warning,
                JobPhase.CleanupCompleted => CheckState.Passed,
                _ => CheckState.Warning
            };
        }
        UpdateAlbumActivityOrder(update.AlbumIndex, update.Progress);
        if (!IsBatch)
        {
            Apply(update.Progress);
            return;
        }

        var snapshots = Enumerable.Range(0, RunnableAlbumCount).Select(index => _jobProgress[index]).ToArray();
        Progress = snapshots.Average(snapshot => snapshot.Percent);
        var completed = snapshots.Count(snapshot => snapshot.Phase == JobPhase.CleanupCompleted);
        var active = snapshots.Count(snapshot => snapshot.Phase is >= JobPhase.Inventoried and < JobPhase.CleanupCompleted);
        var pipelinePhases = snapshots
            .Where(snapshot => snapshot.Phase is >= JobPhase.Inventoried and <= JobPhase.CleanupCompleted)
            .Select(snapshot => snapshot.Phase)
            .ToArray();
        var minimumPhase = pipelinePhases.Length > 0 ? pipelinePhases.Min() : JobPhase.Ready;
        _lastPhase = minimumPhase;
        _lastRunDetail = $"{update.AlbumName}: {update.Progress.Detail}";
        StatusTitle = $"{completed}/{RunnableAlbumCount} admitted albums complete · {active} running · {AlbumCount - RunnableAlbumCount} blocked";
        StatusDetail = _lastRunDetail;
        var number = (int)minimumPhase;
        foreach (var item in Timeline)
        {
            item.State = item.Number < number
                ? "Complete"
                : item.Number == number && number > 0 ? "Active" : "Pending";
        }
    }

    private async Task EnsureAlbumTerminalReportAsync(
        ScanResult scan,
        string jobDirectory,
        ProgressSnapshot last,
        bool canceled,
        IProgress<JobUiUpdate> uiUpdates,
        int index,
        int albumIndex)
    {
        if (_preflight is null) return;
        try
        {
            await HostReportWriter.EnsureTerminalReportAsync(scan, _preflight, jobDirectory,
                canceled ? "canceled" : "failed", last.Phase, last.Percent, last.Detail);
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "REPORT", Message: "A terminal report was preserved; this album's originals remain in place."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            uiUpdates.Report(new(index, albumIndex, scan.AlbumName, Kind: "REPORT", Message: $"Could not preserve the fallback report: {error.Message}"));
        }
    }

    private static string AlbumProgressLabel(ProgressSnapshot snapshot)
    {
        if (snapshot.Status.Equals("incomplete", StringComparison.OrdinalIgnoreCase)) return $"Incomplete · {snapshot.Percent}%";
        return snapshot.Phase switch
        {
            JobPhase.Ready => $"Queued · {snapshot.Percent}%",
            JobPhase.Inventoried => $"Inventoried · {snapshot.Percent}%",
            JobPhase.CopyingIn => $"Preparing source · {snapshot.Percent}%",
            JobPhase.SourceCopyVerified => $"Source verified · {snapshot.Percent}%",
            JobPhase.Processing => $"Splitting · {snapshot.Percent}%",
            JobPhase.Tagging => $"Tagging · {snapshot.Percent}%",
            JobPhase.LocalVerificationPassed => $"Locally verified · {snapshot.Percent}%",
            JobPhase.CopyingBack => $"Copying back · {snapshot.Percent}%",
            JobPhase.DestinationSizesVerified => $"Sizes verified · {snapshot.Percent}%",
            JobPhase.FinalCommit => $"Committing · {snapshot.Percent}%",
            JobPhase.FinalVerificationPassed => $"Final verified · {snapshot.Percent}%",
            JobPhase.SourceDisposition => $"Source action · {snapshot.Percent}%",
            JobPhase.CleanupCompleted => "Complete · 100%",
            JobPhase.Failed => "Failed",
            JobPhase.Canceled => "Canceled",
            _ => $"Working · {snapshot.Percent}%"
        };
    }

    private async Task WriteBatchReportAsync(
        IReadOnlyList<BatchItemResult<AlbumPreflightResult, AlbumJobOutcome>> results,
        bool cancellationRequested,
        BatchPipelineLimits pipelineLimits,
        BatchPipelineTelemetry pipelineTelemetry,
        bool deleteOriginals)
    {
        var succeeded = results.Count(result => result.Succeeded);
        var failed = results.Count(result => !result.Succeeded && !result.Canceled);
        var canceled = results.Count(result => result.Canceled);
        var blocked = _albumPreflights.Count(album => !album.CanStart);
        var incomplete = results.Count(result => result.Succeeded && result.Value!.Incomplete);
        var status = cancellationRequested || canceled > 0
            ? "canceled"
            : failed > 0 ? succeeded == 0 ? "failed" : "partial_failure"
            : incomplete > 0 ? "incomplete"
            : blocked > 0 ? "partial_success" : "passed";
        var deleted = results.Count(result => result.Succeeded && result.Value!.SourcesDeleted);
        var sourceImages = _scans.Sum(scan => scan.ImageCount);
        var albumEntries = _albumPreflights.Select(album =>
        {
            var result = results.FirstOrDefault(candidate => candidate.Item.Index == album.Index);
            if (!album.CanStart)
                return new BatchAlbumReportEntry(album.Index + 1, album.Scan.AlbumName, album.Scan.AlbumRoot, "blocked",
                    null, null, 0, false, album.Detail);
            if (result is null)
                return new BatchAlbumReportEntry(album.Index + 1, album.Scan.AlbumName, album.Scan.AlbumRoot, "canceled",
                    Path.Combine(JobDirectory, $"album-{album.Index + 1:000}"), null, 0, false, "The admitted job did not start.");
            return new BatchAlbumReportEntry(album.Index + 1, album.Scan.AlbumName, album.Scan.AlbumRoot,
                result.Succeeded ? result.Value!.Incomplete ? "incomplete" : "passed" : result.Canceled ? "canceled" : "failed",
                result.Value?.JobDirectory ?? Path.Combine(JobDirectory, $"album-{album.Index + 1:000}"),
                result.Value?.ReportPath, result.Value?.Tracks ?? 0, result.Value?.SourcesDeleted ?? false,
                result.Error?.Message);
        }).OrderBy(album => album.Index).ToArray();
        var report = new
        {
            schema_version = "1.0",
            workflow_mode = "parallel_album_batch",
            source_folders = SourceFolders.ToArray(),
            created_at_utc = DateTimeOffset.UtcNow,
            status,
            delete_originals_requested = deleteOriginals,
            worker_limit = pipelineLimits.MaxInFlight,
            pipeline_limits = new
            {
                maximum_in_flight = pipelineLimits.MaxInFlight,
                copy_in_workers = pipelineLimits.CopyInWorkers,
                processing_workers = pipelineLimits.ProcessingWorkers,
                sacd_processing_workers = pipelineLimits.DsdProcessingWorkers,
                copy_back_workers = pipelineLimits.CopyBackWorkers
            },
            pipeline_observed = new
            {
                copy_in_workers = pipelineTelemetry.MaximumCopyIn,
                processing_workers = pipelineTelemetry.MaximumProcessing,
                sacd_processing_workers = pipelineTelemetry.MaximumDsdProcessing,
                copy_back_workers = pipelineTelemetry.MaximumCopyBack
            },
            albums_total = AlbumCount,
            albums_admitted = results.Count,
            albums_blocked = blocked,
            albums_succeeded = succeeded,
            albums_incomplete = incomplete,
            albums_failed = failed,
            albums_canceled = canceled,
            tracks = results.Where(result => result.Succeeded).Sum(result => result.Value!.Tracks),
            source_images_total = sourceImages,
            source_images_deleted = deleted,
            albums = albumEntries
        };
        var path = Path.Combine(JobDirectory, "batch-report.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        }), new UTF8Encoding(false));
    }

    private async Task LoadReportAsync()
    {
        var batchReport = Directory.Exists(JobDirectory) ? Path.Combine(JobDirectory, "batch-report.json") : "";
        if (File.Exists(batchReport))
        {
            try
            {
                var batch = await Task.Run(async () =>
                {
                    var json = await File.ReadAllTextAsync(batchReport, Encoding.UTF8).ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    var root = document.RootElement;
                    var status = root.GetProperty("status").GetString() ?? "pending";
                    var total = root.GetProperty("albums_total").GetInt32();
                    var succeeded = root.GetProperty("albums_succeeded").GetInt32();
                    var failed = root.GetProperty("albums_failed").GetInt32();
                    var canceled = root.GetProperty("albums_canceled").GetInt32();
                    var incomplete = root.TryGetProperty("albums_incomplete", out var incompleteValue) ? incompleteValue.GetInt32() : 0;
                    var blocked = root.TryGetProperty("albums_blocked", out var blockedValue) ? blockedValue.GetInt32() : 0;
                    var tracks = root.GetProperty("tracks").GetInt32();
                    var sourceImages = root.GetProperty("source_images_total").GetInt32();
                    var deleted = root.GetProperty("source_images_deleted").GetInt32();
                    var workerLimit = root.GetProperty("worker_limit").GetInt32();
                    var prettyJson = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
                    var errors = root.GetProperty("albums").EnumerateArray()
                        .Where(album => album.GetProperty("error").ValueKind == JsonValueKind.String)
                        .Select(album => $"{album.GetProperty("album").GetString()}: {album.GetProperty("error").GetString()}")
                        .ToArray();
                    return (status, total, succeeded, failed, canceled, incomplete, blocked, tracks, sourceImages, deleted, workerLimit, prettyJson, errors);
                });
                ReportStatus = batch.status;
                ReportHeadline = $"Batch {batch.status.Replace('_', ' ')} · {batch.succeeded}/{batch.total} albums completed";
                ReportDetail = $"{batch.tracks} tracks · {batch.incomplete} incomplete · {batch.failed} failed · {batch.blocked} blocked · {batch.canceled} canceled · worker limit {batch.workerLimit}";
                ReportJson = batch.prettyJson;
                ReportTracks = batch.tracks.ToString();
                ReportSections = batch.total.ToString();
                ReportDisposition = $"{batch.deleted} deleted · {batch.sourceImages - batch.deleted} retained";
                Replace(ReportErrors, batch.errors);
                return;
            }
            catch (Exception error) when (error is IOException or JsonException)
            {
                ReportHeadline = "Batch report is not readable yet";
                ReportDetail = error.Message;
                return;
            }
        }

        var sourceFolders = SourceFolders.ToArray();
        var jobDirectory = JobDirectory;
        var candidates = await Task.Run(() => sourceFolders.Select(folder => Path.Combine(folder, "conversion-report.json"))
            .Append(Directory.Exists(jobDirectory) ? Path.Combine(jobDirectory, "album", "conversion-report.json") : "")
            .Append(Directory.Exists(jobDirectory) ? Path.Combine(jobDirectory, "conversion-report.json") : "")
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ToArray());
        if (candidates.Length == 0) return;
        try { var report = await Task.Run(() => ReportReader.LoadAsync(candidates[0])); ReportHeadline = report.Headline; ReportDetail = report.Detail; ReportJson = report.Json; ReportStatus = report.Status; ReportTracks = report.Tracks.ToString(); ReportSections = report.Sections.ToString(); ReportDisposition = report.Deleted ? "Deleted after quick checks" : "Retained"; Replace(ReportErrors, report.Errors); }
        catch (Exception error) when (error is IOException or System.Text.Json.JsonException) { ReportHeadline = "Report is not readable yet"; ReportDetail = error.Message; }
    }

    private async Task EnsureTerminalReportAsync(bool canceled)
    {
        if (_scan is null || _preflight is null) return;
        try
        {
            await HostReportWriter.EnsureTerminalReportAsync(_scan, _preflight, JobDirectory,
                canceled ? "canceled" : "failed", _lastPhase, (int)Progress, _lastRunDetail);
            Log("REPORT", "A terminal report was preserved; every original remains in place.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log("REPORT", $"Could not preserve the fallback report: {error.Message}");
        }
    }

    private void Apply(ProgressSnapshot snapshot)
    {
        _lastPhase = snapshot.Phase;
        _lastRunDetail = snapshot.Detail;
        Progress = Math.Max(Progress, snapshot.Percent); StatusTitle = PhaseTitle(snapshot.Phase); StatusDetail = snapshot.Detail;
        var number = snapshot.Phase is >= JobPhase.Inventoried and <= JobPhase.CleanupCompleted ? (int)snapshot.Phase : Timeline.FirstOrDefault(x => x.State == "Active")?.Number ?? 1;
        foreach (var item in Timeline) item.State = snapshot.Phase == JobPhase.Failed && item.Number == number ? "Failed" : snapshot.Phase == JobPhase.Canceled && item.Number == number ? "Canceled" : item.Number < number ? "Complete" : item.Number == number ? snapshot.Phase == JobPhase.CleanupCompleted ? "Complete" : "Active" : "Pending";
    }

    private void UpdateAlbumActivityOrder(int albumIndex, ProgressSnapshot snapshot)
    {
        var active = snapshot.Phase is >= JobPhase.Inventoried and < JobPhase.CleanupCompleted;
        if (active) _activeAlbumActivity[albumIndex] = ++_albumActivitySequence;
        else _activeAlbumActivity.Remove(albumIndex);

        var ordered = Albums
            .OrderBy(row => row.AlbumIndex is int index && _activeAlbumActivity.ContainsKey(index) ? 0 : 1)
            .ThenByDescending(row => row.AlbumIndex is int index && _activeAlbumActivity.TryGetValue(index, out var activity) ? activity : long.MinValue)
            .ThenBy(row => row.AlbumIndex ?? int.MaxValue)
            .ToArray();
        for (var targetIndex = 0; targetIndex < ordered.Length; targetIndex++)
        {
            var currentIndex = Albums.IndexOf(ordered[targetIndex]);
            if (currentIndex != targetIndex) Albums.Move(currentIndex, targetIndex);
        }
    }

    private void ProgressTimer_Tick(object? sender, EventArgs e) => RefreshProgressTime();
    private void RefreshProgressTime()
    {
        if (_runStartedAt == default) return;
        ProgressTime = $"Elapsed {Clock(DateTimeOffset.UtcNow - _runStartedAt)}";
    }

    private static string Clock(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        return value.TotalHours >= 1 ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}" : $"{value.Minutes}:{value.Seconds:00}";
    }
    private void Cancel()
    {
        if (!IsRunActive) return;

        StatusTitle = "Stopping album workers at safe boundaries…";
        StatusDetail = "Incomplete album jobs cannot authorize source deletion.";
        Log("CANCEL", "Cancellation requested for every active album worker.");
        _cancel?.Cancel();
    }
    private void ClearSourceFolders()
    {
        if (Busy || SourceFolders.Count == 0) return;
        SourceFolders.Clear();
        Invalidate();
        StatusTitle = "Source folders cleared";
        StatusDetail = "Add one or more album or parent folders, then scan recursively.";
        Log("SOURCE", "Source folder list cleared.");
    }

    public async Task RemoveSourceFolderAsync(string? folder)
    {
        if (!CanBrowse || string.IsNullOrWhiteSpace(folder)) return;
        var existing = SourceFolders.FirstOrDefault(item => item.Equals(folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null) return;
        SourceFolders.Remove(existing);
        Invalidate();
        StatusTitle = "Source folder removed";
        StatusDetail = SourceFolders.Count == 0
            ? "Add one or more album or parent folders, then scan recursively."
            : $"{SourceFolderCount} parent folder{S(SourceFolderCount)} remain and will be rescanned.";
        Log("SOURCE", $"Removed source folder: {existing}");
        if (SourceFolders.Count > 0) await ScanAsync();
    }

    public bool TryClose()
    {
        if (Busy && !_userInteraction.ConfirmCloseWhileRunning())
            return false;

        if (Busy)
            Cancel();

        return true;
    }

    private void CopyReport()
    {
        if (ReportJson.Length > 0)
            _userInteraction.CopyToClipboard(ReportJson);
    }

    private void Invalidate()
    {
        if (Busy) return;

        _scan = null;
        _scans = [];
        _albumPreflights = [];
        _preflight = null;
        _previousOutputFileCount = 0;
        _albumCheckRows.Clear();
        _activeAlbumActivity.Clear();
        _albumActivitySequence = 0;

        PreflightChecks.Clear();
        Albums.Clear();
        Media.Clear();

        AlbumName = SourceFolderCount switch
        {
            0 => "No source folders selected",
            1 => Path.GetFileName(SourceFolders[0].TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)),
            _ => $"{SourceFolderCount} source folders selected"
        };
        Workflow = "Scan to classify albums recursively";
        Inventory = SourceSize = "—";
        StatusTitle = "Ready to scan";
        StatusDetail = "Inventory is read-only.";

        OnPropertyChanged(nameof(SourceFolderCount));
        OnPropertyChanged(nameof(BrowseInitialDirectory));
        NotifyAlbumStateChanged();
        NotifyCommandStates();
    }

    private void NotifyAlbumStateChanged()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(AlbumCount));
        OnPropertyChanged(nameof(RunnableAlbumCount));
        OnPropertyChanged(nameof(BatchWorkerLimit));
        OnPropertyChanged(nameof(BatchPipelineDescription));
        OnPropertyChanged(nameof(PreviousOutputFileCount));
        OnPropertyChanged(nameof(IsBatch));
        OnPropertyChanged(nameof(IsSingleSacd));
        OnPropertyChanged(nameof(HasSacdWorkflows));
        OnPropertyChanged(nameof(DeletesSourceAfterSuccess));
        OnPropertyChanged(nameof(DeletesAnySourceAfterSuccess));
    }

    private void NotifyCommandStates()
    {
        BrowseCommand.NotifyCanExecuteChanged();
        RemoveSourceFolderCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RefreshReportCommand.NotifyCanExecuteChanged();
        ClearSourceFoldersCommand.NotifyCanExecuteChanged();
    }
    private static string? NormalizeSourceFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return normalized;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }
    private static bool IsSameOrNestedFolder(string path, string parent)
    {
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        var parentWithSeparator = parent.EndsWith(Path.DirectorySeparatorChar) || parent.EndsWith(Path.AltDirectorySeparatorChar)
            ? parent
            : parent + Path.DirectorySeparatorChar;
        return path.StartsWith(parentWithSeparator, StringComparison.OrdinalIgnoreCase);
    }
    private void Log(string kind, string message)
    {
        Activity.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), kind, message.ReplaceLineEndings(" ")));
        while (Activity.Count > 250)
            Activity.RemoveAt(Activity.Count - 1);
    }
    private static void Replace<T>(RangeObservableCollection<T> target, IEnumerable<T> items) => target.ReplaceAll(items);
    private static string S(int count) => count == 1 ? "" : "s";
    private static string PhaseTitle(JobPhase phase) => phase switch { JobPhase.Inventoried => "Inventory complete", JobPhase.CopyingIn => "Preparing the source…", JobPhase.SourceCopyVerified => "Source verified", JobPhase.Processing => "Splitting or extracting…", JobPhase.Tagging => "Writing metadata and artwork…", JobPhase.LocalVerificationPassed => "Local verification passed", JobPhase.CopyingBack => "Copying verified output back…", JobPhase.DestinationSizesVerified => "Destination-side sizes verified", JobPhase.FinalCommit => "Committing final files…", JobPhase.FinalVerificationPassed => "Final-path verification passed", JobPhase.SourceDisposition => "Recording source disposition…", JobPhase.CleanupCompleted => "Album completed", JobPhase.Failed => "Run stopped safely", JobPhase.Canceled => "Run canceled", _ => "Preparing the job…" };
    public void Dispose()
    {
        _progressTimer.Stop();
        _progressTimer.Tick -= ProgressTimer_Tick;
        _cancel?.Cancel();
        _cancel?.Dispose();
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
        GC.SuppressFinalize(this);
    }
}
