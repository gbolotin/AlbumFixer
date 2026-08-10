using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using AlbumFixer.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AlbumFixer.App;

public sealed record ActivityRow(string Time, string Kind, string Message);

public sealed record MediaRow(string Path, string Kind, string Size, string Note);

public sealed record AlbumJobOutcome(
    string JobDirectory,
    string ReportPath,
    int Tracks,
    bool SourcesDeleted,
    bool Incomplete,
    string? ThreadId);

public sealed record JobUiUpdate(
    int Index,
    int AlbumIndex,
    string AlbumName,
    ProgressSnapshot? Progress = null,
    string? Kind = null,
    string? Message = null,
    string? ThreadId = null);

public sealed record BatchAlbumReportEntry(
    int Index,
    string Album,
    string AlbumRoot,
    string Status,
    string? JobDirectory,
    string? ReportPath,
    int Tracks,
    bool SourcesDeleted,
    string? ThreadId,
    string? Error);

public sealed class CheckRow : ObservableObject
{
    private string _state;
    private string _detail;
    private CheckState _rawState;

    public CheckRow(
        string name,
        string state,
        string detail,
        CheckState rawState,
        int? albumIndex = null,
        string? albumFolderPath = null)
    {
        Name = name;
        _state = state;
        _detail = detail;
        _rawState = rawState;
        AlbumIndex = albumIndex;
        AlbumFolderPath = albumFolderPath;
    }

    public string Name { get; }
    public int? AlbumIndex { get; }
    public string? AlbumFolderPath { get; }
    public bool HasAlbumFolder => !string.IsNullOrWhiteSpace(AlbumFolderPath);
    public Visibility AlbumNameTextVisibility => HasAlbumFolder ? Visibility.Collapsed : Visibility.Visible;
    public Visibility AlbumNameLinkVisibility => HasAlbumFolder ? Visibility.Visible : Visibility.Collapsed;
    public string State { get => _state; set => SetProperty(ref _state, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public CheckState RawState { get => _rawState; set => SetProperty(ref _rawState, value); }
}

public sealed class TimelineRow : ObservableObject
{
    private string _state = "Pending";

    public required int Number { get; init; }
    public required JobPhase Phase { get; init; }
    public required string Title { get; init; }
    public string NumberText => Number.ToString("00");
    public string State { get => _state; set => SetProperty(ref _state, value); }
}

public sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var replacement = items.ToArray();
        CheckReentrancy();
        Items.Clear();

        foreach (var item in replacement)
            Items.Add(item);

        OnPropertyChanged(new(nameof(Count)));
        OnPropertyChanged(new("Item[]"));
        OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
    }
}
