using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AlbumFixer.App;

/// <summary>
/// The Report tab: the summary and JSON of the latest conversion report. The run coordinator decides which report
/// to show and when; this ViewModel only presents it and copies it.
/// </summary>
public sealed partial class ReportViewModel : ObservableObject
{
    private readonly IUserInteractionService _userInteraction;

    /// <param name="refresh">Reloads the latest report; the coordinator owns where reports are found.</param>
    /// <param name="canRefresh">Whether a reload is possible now.</param>
    public ReportViewModel(IUserInteractionService userInteraction, Func<Task> refresh, Func<bool> canRefresh)
    {
        _userInteraction = userInteraction;
        RefreshCommand = new AsyncRelayCommand(refresh, canRefresh);
    }

    [ObservableProperty]
    public partial string Headline { get; private set; } = "No conversion report yet";

    [ObservableProperty]
    public partial string Detail { get; private set; } = "A validated summary and JSON report will appear here.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    public partial string Json { get; private set; } = "";

    [ObservableProperty]
    public partial string Status { get; private set; } = "Pending";

    [ObservableProperty]
    public partial string Tracks { get; private set; } = "—";

    [ObservableProperty]
    public partial string Sections { get; private set; } = "—";

    [ObservableProperty]
    public partial string Disposition { get; private set; } = "—";

    public RangeObservableCollection<string> Errors { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>Where Album Fixer writes its application log.</summary>
    public string LogsFolder => AppLogging.LogsFolder;

    /// <summary>Clears the previous report while a run is in progress.</summary>
    public void ShowRunInProgress()
    {
        Status = "Pending";
        Tracks = Sections = Disposition = "—";
        Json = "";
        Headline = "Run in progress";
        Detail = "Terminal status will be recorded before transient staging is cleaned.";
    }

    public void Show(string status, string headline, string detail, string json, string tracks, string sections, string disposition, IEnumerable<string> errors)
    {
        Status = status;
        Headline = headline;
        Detail = detail;
        Json = json;
        Tracks = tracks;
        Sections = sections;
        Disposition = disposition;
        Errors.ReplaceAll(errors);
    }

    /// <summary>Shows why a report could not be read, keeping the rest of the previous report.</summary>
    public void ShowUnreadable(string headline, string detail)
    {
        Headline = headline;
        Detail = detail;
    }

    private bool CanCopy() => Json.Length > 0;

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private void Copy() => _userInteraction.CopyToClipboard(Json);

    [RelayCommand]
    private static void OpenLogsFolder() => AppLogging.OpenLogsFolder();
}
