using System.Collections.ObjectModel;

namespace AlbumFixer.App;

/// <summary>The Activity tab: the latest 250 activity lines, newest first.</summary>
public sealed class ActivityViewModel
{
    private const int MaximumItems = 250;

    public ObservableCollection<ActivityRow> Items { get; } = [];

    public void Add(string kind, string message)
    {
        Items.Insert(0, new(DateTime.Now.ToString("HH:mm:ss"), kind, message.ReplaceLineEndings(" ")));
        while (Items.Count > MaximumItems)
        {
            Items.RemoveAt(Items.Count - 1);
        }
    }
}
