namespace AlbumFixer.App;

/// <summary>The Inventory tab: the media and preserved sidecars the last scan detected.</summary>
public sealed class InventoryViewModel
{
    public RangeObservableCollection<MediaRow> Media { get; } = [];
}
