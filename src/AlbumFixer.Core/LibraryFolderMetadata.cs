namespace AlbumFixer.Core;

internal static class LibraryFolderMetadata
{
    private static readonly IReadOnlyDictionary<string, string> Genres =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alternative"] = "Alternative",
            ["blues"] = "Blues",
            ["classical"] = "Classical",
            ["country"] = "Country",
            ["electronic"] = "Electronic",
            ["folk"] = "Folk",
            ["funk"] = "Funk",
            ["jazz"] = "Jazz",
            ["metal"] = "Metal",
            ["opera"] = "Opera",
            ["pop"] = "Pop",
            ["reggae"] = "Reggae",
            ["rock"] = "Rock",
            ["soul"] = "Soul",
            ["soundtrack"] = "Soundtrack",
            ["spoken word"] = "Spoken Word"
        };

    internal static string? InferGenre(string albumRoot)
    {
        for (var directory = Directory.GetParent(albumRoot); directory is not null; directory = directory.Parent)
            if (Genres.TryGetValue(directory.Name.Trim(), out var genre)) return genre;
        return null;
    }
}
