using AlbumFixer.Core;

var roots = args.Length == 1
    ? new[] { Path.GetFullPath(args[0]) }
    : Directory.EnumerateDirectories(@"C:\Music\LINN")
        .Where(path => !Path.GetFileName(path).Contains("Volume 7", StringComparison.OrdinalIgnoreCase))
        .Where(path => !Path.GetFileName(path).Contains("Volume 8", StringComparison.OrdinalIgnoreCase))
        .Where(path => !Path.GetFileName(path).Contains("40th Anniversary", StringComparison.OrdinalIgnoreCase))
        .Where(path => Directory.EnumerateFiles(path).Any(file => Path.GetExtension(file) is ".flac" or ".dsf"))
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToArray();

foreach (var root in roots)
{
    var paths = Directory.EnumerateFiles(root)
        .Where(file => Path.GetExtension(file) is ".flac" or ".dsf")
        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    var tagged = paths.Select(path =>
    {
        using var file = TagLib.File.Create(path);
        return new
        {
            Title = file.Tag.Title ?? Path.GetFileNameWithoutExtension(path),
            Album = file.Tag.Album,
            Artist = file.Tag.FirstPerformer,
            AlbumArtist = file.Tag.FirstAlbumArtist,
            Year = file.Tag.Year
        };
    }).ToArray();
    var album = tagged.Select(value => value.Album).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? Path.GetFileName(root);
    var albumArtist = tagged.Select(value => value.AlbumArtist).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    var distinctArtists = tagged.Select(value => value.Artist).Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    albumArtist ??= distinctArtists.Length >= 3 || distinctArtists.Any(value => value!.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
        ? "Various Artists"
        : distinctArtists.FirstOrDefault() ?? "Various Artists";
    var titles = tagged.Select(value => value.Title).ToArray();
    var year = tagged.Select(value => value.Year).FirstOrDefault(value => value > 0);
    var metadata = await new ExternalMetadataService(requestTimeout: TimeSpan.FromSeconds(20)).ResolveAsync(
        new(album, albumArtist, titles.Length, OriginalYear: year == 0 ? null : (int)year,
            RequireSacd: false, TrackTitleHints: titles, AlbumTitleHints: [Path.GetFileName(root)]),
        includeTrackTitles: true);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        Folder = Path.GetFileName(root),
        Query = new { album, albumArtist, TrackCount = titles.Length, year },
        metadata.MusicBrainzReleaseId,
        metadata.CatalogNumber,
        metadata.Sources,
        metadata.Warnings,
        ExternalTrackCount = metadata.TrackTitles.Count,
        ComposerCount = metadata.TrackComposers.Count(value => !string.IsNullOrWhiteSpace(value)),
        ArtistCount = metadata.TrackArtists.Count(value => !string.IsNullOrWhiteSpace(value)),
        metadata.TrackComposerSourceType,
        Tracks = titles.Select((title, index) => new
        {
            Number = index + 1,
            Local = title,
            External = metadata.TrackTitles.ElementAtOrDefault(index),
            Composer = metadata.TrackComposers.ElementAtOrDefault(index),
            Artist = metadata.TrackArtists.ElementAtOrDefault(index)
        })
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
}
