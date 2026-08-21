using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

public sealed record AlbumMetadataQuery(
    string Album,
    string Artist,
    int TrackCount,
    int? OriginalYear = null,
    int? EditionYear = null,
    string? CatalogNumber = null,
    bool RequireSacd = true,
    IReadOnlyList<string>? TrackTitleHints = null,
    IReadOnlyList<string>? AlbumTitleHints = null);

public sealed record ExternalAlbumMetadata(
    string? Genre,
    string? ReleaseDate,
    string? OriginalDate,
    string? Label,
    string? CatalogNumber,
    string? Barcode,
    string? ReleaseCountry,
    string? GenreSourceType,
    string? GenreConfidence,
    string? GenreRationale,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Warnings,
    string? MusicBrainzReleaseId,
    string? MusicBrainzReleaseGroupId,
    IReadOnlyList<string> TrackTitles)
{
    public bool HasMatch => Sources.Count > 0;
    public IReadOnlyList<string> TrackComposers { get; init; } = [];
    public string? TrackComposerSourceType { get; init; }
    public IReadOnlyList<string> TrackArtists { get; init; } = [];
    public string? TrackArtistSourceType { get; init; }
    public string? ArtworkUrl { get; init; }
    public string? ArtworkSourceType { get; init; }
    public bool IsMixedGenreCompilation { get; init; }
}

public sealed record ExternalAlbumIdentity(
    string Album,
    string Artist,
    string CatalogNumber,
    string? ReleaseDate,
    string Source,
    IReadOnlyList<string> TrackTitles,
    string? MusicBrainzReleaseId = null);

public sealed partial class ExternalMetadataService
{
    private const string UserAgent = "AlbumFixer/1.0 (https://github.com/gbolotin/AlbumFixer)";
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static readonly SemaphoreSlim DiscogsGate = new(1, 1);
    private static DateTimeOffset _lastMusicBrainzRequest = DateTimeOffset.MinValue;
    private static DateTimeOffset _lastDiscogsRequest = DateTimeOffset.MinValue;

    private readonly HttpClient _client;
    private readonly string? _discogsToken;
    private readonly TimeSpan _musicBrainzMinimumInterval;
    private readonly TimeSpan _discogsMinimumInterval;
    private readonly TimeSpan _requestTimeout;

    public ExternalMetadataService(
        HttpClient? client = null,
        string? discogsToken = null,
        TimeSpan? musicBrainzMinimumInterval = null,
        TimeSpan? discogsMinimumInterval = null,
        TimeSpan? requestTimeout = null)
    {
        _client = client ?? SharedClient;
        _discogsToken = Nonempty(discogsToken) ?? Nonempty(Environment.GetEnvironmentVariable("DISCOGS_TOKEN"));
        _musicBrainzMinimumInterval = musicBrainzMinimumInterval ?? TimeSpan.FromMilliseconds(1100);
        _discogsMinimumInterval = discogsMinimumInterval ?? (client is not null
            ? TimeSpan.Zero
            : _discogsToken is null ? TimeSpan.FromMilliseconds(2500) : TimeSpan.FromMilliseconds(1100));
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<ExternalAlbumMetadata> ResolveAsync(
        AlbumMetadataQuery query,
        bool includeTrackTitles = false,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Album);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Artist);

        var matches = new List<ProviderMetadata>();
        var warnings = new List<string>();
        var canVerifyPublicDiscogsSearch = query.TrackCount > 0 && query.TrackTitleHints?.Count == query.TrackCount;
        if (_discogsToken is null && !canVerifyPublicDiscogsSearch)
        {
            warnings.Add("Direct Discogs database search was skipped because DISCOGS_TOKEN is not configured and no complete ordered local track-title list was available for public-result verification; public Discogs records linked by MusicBrainz are still checked.");
        }
        else
        {
            await TryProviderAsync("Discogs", () => ResolveDiscogsAsync(query, token), matches, warnings, token);
        }

        await TryProviderAsync("MusicBrainz", () => ResolveMusicBrainzAsync(query, includeTrackTitles, token), matches, warnings, token);
        await TryProviderAsync("Apple Music", () => ResolveAppleAsync(query, token), matches, warnings, token);

        var genreMatch = matches.FirstOrDefault(match => match.Genre is not null);
        var artworkMatch = matches.FirstOrDefault(match => Nonempty(match.ArtworkUrl) is not null);
        var (trackComposers, composerSourceType) = MergeAlignedValues(
            matches, query.TrackCount, match => match.TrackComposers, match => match.TrackComposerSourceType);
        var (trackArtists, artistSourceType) = MergeAlignedValues(
            matches, query.TrackCount, match => match.TrackArtists, match => match.TrackArtistSourceType);
        return new(
            genreMatch?.Genre,
            First(matches, match => match.ReleaseDate),
            First(matches, match => match.OriginalDate),
            First(matches, match => match.Label),
            First(matches, match => match.CatalogNumber),
            First(matches, match => match.Barcode),
            First(matches, match => match.ReleaseCountry),
            genreMatch?.GenreSourceType,
            genreMatch?.GenreConfidence,
            genreMatch?.GenreRationale,
            matches.SelectMany(match => match.Sources).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            First(matches, match => match.MusicBrainzReleaseId),
            First(matches, match => match.MusicBrainzReleaseGroupId),
            matches.Select(match => match.TrackTitles).FirstOrDefault(titles => titles.Count == query.TrackCount) ?? [])
        {
            TrackComposers = trackComposers,
            TrackComposerSourceType = composerSourceType,
            TrackArtists = trackArtists,
            TrackArtistSourceType = artistSourceType,
            ArtworkUrl = artworkMatch?.ArtworkUrl,
            ArtworkSourceType = artworkMatch?.ArtworkSourceType,
            IsMixedGenreCompilation = matches.Any(match => match.IsMixedGenreCompilation)
        };
    }

    private static (IReadOnlyList<string> Values, string? SourceType) MergeAlignedValues(
        IReadOnlyList<ProviderMetadata> matches,
        int trackCount,
        Func<ProviderMetadata, IReadOnlyList<string>> values,
        Func<ProviderMetadata, string?> sourceType)
    {
        var aligned = matches.Where(match => values(match).Count == trackCount).ToArray();
        if (aligned.Length == 0) return ([], null);
        var merged = new string[trackCount];
        var contributors = new List<string>();
        foreach (var match in aligned)
        {
            var contributed = false;
            var candidate = values(match);
            for (var index = 0; index < trackCount; index++)
            {
                if (Nonempty(merged[index]) is not null || Nonempty(candidate[index]) is not { } value) continue;
                merged[index] = value;
                contributed = true;
            }
            if (contributed && Nonempty(sourceType(match)) is { } source)
                contributors.Add(source);
        }
        if (!merged.Any(value => Nonempty(value) is not null)) return ([], null);
        var distinctSources = contributors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return (merged, distinctSources.Length switch
        {
            0 => null,
            1 => distinctSources[0],
            _ => "merged_aligned_external_track_credits"
        });
    }

    public Task<ExternalAlbumMetadata> ResolveAsync(
        AlbumMetadataQuery query,
        CancellationToken token) => ResolveAsync(query, includeTrackTitles: false, token);

    public async Task<ExternalAlbumIdentity?> ResolveIdentityByCatalogAsync(
        string catalogNumber,
        int trackCount,
        int? editionYear = null,
        bool requireSacd = true,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogNumber);
        if (trackCount <= 0) throw new ArgumentOutOfRangeException(nameof(trackCount));

        try
        {
            var lucene = $"catno:\"{EscapeLucene(catalogNumber)}\"";
            var uri = new Uri($"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(lucene)}&fmt=json&limit=100");
            using var search = await GetMusicBrainzJsonAsync(uri, token);
            if (!search.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = releases.EnumerateArray()
                .Select(release =>
                {
                    var label = LabelInfo(release);
                    var formats = MediaFormats(release);
                    var releaseYear = Year(Text(release, "date"));
                    var score = (Integer(release, "score") ?? 0) +
                                (releaseYear == editionYear ? 40 : 0) +
                                (HasSacdMedium(release) ? 30 : 0);
                    return new
                    {
                        Release = release.Clone(),
                        Album = Text(release, "title"),
                        Artist = ArtistCredit(release),
                        Catalog = label.CatalogNumber,
                        Formats = formats,
                        HasMatchingMedium = HasMatchingMedium(release, trackCount, requireSacd),
                        Score = score
                    };
                })
                .Where(candidate =>
                    !string.IsNullOrWhiteSpace(candidate.Album) &&
                    !string.IsNullOrWhiteSpace(candidate.Artist) &&
                    CatalogsEquivalent(candidate.Catalog, catalogNumber) &&
                    candidate.HasMatchingMedium &&
                    (!requireSacd || candidate.Formats.Any(IsSacdMediumFormat)))
                .ToArray();
            if (candidates.Length == 0) return null;

            var identities = candidates
                .GroupBy(candidate => $"{IdentityKey(candidate.Artist!)}\0{IdentityKey(candidate.Album!)}", StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (identities.Length != 1) return null;

            var selected = identities[0].OrderByDescending(candidate => candidate.Score).First();
            var releaseId = Text(selected.Release, "id");
            if (releaseId is null) return null;
            var trackTitles = await ResolveMusicBrainzTrackTitlesAsync(releaseId, trackCount, requireSacd, token);
            return new(
                selected.Album!,
                selected.Artist!,
                selected.Catalog!,
                Text(selected.Release, "date"),
                $"https://musicbrainz.org/release/{releaseId}",
                trackTitles,
                releaseId);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidDataException)
        {
            return null;
        }
    }

    public async Task<DownloadedArtwork> DownloadFrontCoverAsync(
        string releaseId,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        var source = new Uri($"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseId)}/front-500");
        return await DownloadImageAsync(source, "Cover Art Archive", token);
    }

    public async Task<DownloadedArtwork> DownloadArtworkAsync(
        string artworkUrl,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artworkUrl);
        if (!Uri.TryCreate(artworkUrl, UriKind.Absolute, out var source) ||
            !source.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !source.Host.Equals("i.discogs.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The external artwork URL is not a trusted Discogs image URL.");
        return await DownloadImageAsync(source, "Discogs", token);
    }

    private async Task<DownloadedArtwork> DownloadImageAsync(
        Uri source,
        string provider,
        CancellationToken token)
    {
        const long maximumDownloadBytes = 15L * 1024 * 1024;
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("image/jpeg, image/png;q=0.9, image/*;q=0.8");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_requestTimeout);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{provider} returned a non-image response.");
        if (response.Content.Headers.ContentLength is > maximumDownloadBytes)
            throw new InvalidDataException($"{provider} returned an unexpectedly large image.");

        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, timeout.Token);
            if (read == 0) break;
            total += read;
            if (total > maximumDownloadBytes)
                throw new InvalidDataException($"{provider} image exceeded the download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        if (total == 0) throw new InvalidDataException($"{provider} returned an empty image.");
        return new(
            output.ToArray(),
            contentType,
            response.RequestMessage?.RequestUri?.AbsoluteUri ?? source.AbsoluteUri);
    }

    internal static string? ChooseBroadGenre(IEnumerable<(string Name, int Weight)> values)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, weight) in values)
        {
            var broad = BroadGenre(name);
            if (broad is null) continue;
            scores[broad] = scores.GetValueOrDefault(broad) + Math.Max(1, weight);
        }
        return scores.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Key).FirstOrDefault();
    }

    internal static bool CatalogsEquivalent(string? left, string? right)
    {
        if (Nonempty(left) is null || Nonempty(right) is null) return false;
        var normalizedLeft = Normalize(left!);
        var normalizedRight = Normalize(right!);
        if (normalizedLeft.Equals(normalizedRight, StringComparison.Ordinal)) return true;
        return Math.Min(normalizedLeft.Length, normalizedRight.Length) >= 6 &&
               (normalizedLeft.Contains(normalizedRight, StringComparison.Ordinal) ||
                normalizedRight.Contains(normalizedLeft, StringComparison.Ordinal));
    }

    private async Task<ProviderMetadata?> ResolveMusicBrainzAsync(
        AlbumMetadataQuery query,
        bool includeTrackTitles,
        CancellationToken token)
    {
        var titleCandidates = AlbumTitleCandidates(query);
        var lookupAlbum = titleCandidates[0];
        var selected = await SearchMusicBrainzReleaseAsync(
            lookupAlbum, query, requireArtistMatch: true, requireTrackMatch: false, token);
        var titleFallbackUsed = false;
        if (selected is null && includeTrackTitles && query.TrackTitleHints?.Count == query.TrackCount)
        {
            foreach (var titleCandidate in titleCandidates)
            {
                selected = await SearchMusicBrainzReleaseAsync(
                    titleCandidate, query, requireArtistMatch: false, requireTrackMatch: true, token);
                if (selected is null) continue;
                lookupAlbum = titleCandidate;
                titleFallbackUsed = true;
                break;
            }
        }
        if (selected is null) return null;

        var candidate = selected.Value;
        var candidateYear = Year(Text(candidate, "date"));
        var candidateLabel = LabelInfo(candidate);
        var exactEdition = HasMatchingMedium(candidate, query.TrackCount, query.RequireSacd) &&
                           (query.EditionYear is null || candidateYear == query.EditionYear || EquivalentCatalog(candidateLabel.CatalogNumber, query.CatalogNumber));

        var releaseId = Text(candidate, "id");
        var groupId = candidate.TryGetProperty("release-group", out var group) ? Text(group, "id") : null;
        string? genre = null;
        var genreSourceType = "musicbrainz_release_group";
        var genreRationale = "Broad genre selected from MusicBrainz release-group genres and weighted tags.";
        var sources = new List<string>();
        string? originalDate = null;
        var mixedGenreCompilation = false;
        var linkedDiscogs = new LinkedDiscogsMetadata(null, null, [], []);
        if (groupId is not null)
        {
            var groupUri = new Uri($"https://musicbrainz.org/ws/2/release-group/{Uri.EscapeDataString(groupId)}?inc=genres%2Btags%2Burl-rels&fmt=json");
            using var details = await GetMusicBrainzJsonAsync(groupUri, token);
            originalDate = Text(details.RootElement, "first-release-date");
            var genres = WeightedNames(details.RootElement, "genres", defaultWeight: 10)
                .Concat(WeightedNames(details.RootElement, "tags", defaultWeight: 1)).ToArray();
            genre = ChooseBroadGenre(genres);
            mixedGenreCompilation = ClassicalMetadataPolicy.IsCompilationArtist(query.Artist) &&
                                    HasClassicalAndNonClassicalGenres(genres.Select(value => value.Name));
            try
            {
                var discogs = await ResolveLinkedDiscogsMetadataAsync(
                    details.RootElement, query, lookupAlbum, allowArtistMismatch: titleFallbackUsed, token);
                linkedDiscogs = discogs;
                mixedGenreCompilation |= discogs.IsMixedGenreCompilation;
                if (discogs.Genre is not null)
                {
                    genre = discogs.Genre;
                    genreSourceType = "discogs_linked_from_musicbrainz";
                    genreRationale = "Broad genre selected from the Discogs master or release linked by the matched MusicBrainz release group.";
                }
                if (discogs.Source is not null) sources.Add(discogs.Source);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (Exception) when (!token.IsCancellationRequested) { }
        }

        if (genre is null && exactEdition && releaseId is not null && CuratedReleaseGenres.TryGetValue(releaseId, out var curatedGenre))
        {
            genre = curatedGenre.Genre;
            genreSourceType = "curated_exact_release";
            genreRationale = "Broad genre selected from a reviewed public-catalog record tied to this exact MusicBrainz release.";
            sources.Add(curatedGenre.Source);
        }

        if (exactEdition && releaseId is not null) sources.Add($"https://musicbrainz.org/release/{releaseId}");
        if (groupId is not null) sources.Add($"https://musicbrainz.org/release-group/{groupId}");
        var trackMetadata = includeTrackTitles && releaseId is not null
            ? await ResolveMusicBrainzTrackMetadataAsync(releaseId, query.TrackCount, query.RequireSacd, includeRelationships: true, token)
            : new MusicBrainzTrackMetadata([], []);
        if (!TrackMetadataMatchesHints(trackMetadata, query.TrackTitleHints, requireComplete: titleFallbackUsed))
            trackMetadata = new([], []);
        var linkedTrackMetadata = new MusicBrainzTrackMetadata(linkedDiscogs.TrackTitles, linkedDiscogs.TrackComposers)
        {
            Artists = linkedDiscogs.TrackArtists
        };
        var musicBrainzComposerCount = trackMetadata.Composers.Count(value => Nonempty(value) is not null);
        if (TrackMetadataMatchesHints(linkedTrackMetadata, query.TrackTitleHints, requireComplete: titleFallbackUsed))
            trackMetadata = MergeTrackMetadata(trackMetadata, linkedTrackMetadata, query.TrackCount);
        if (titleFallbackUsed && !TrackMetadataMatchesHints(trackMetadata, query.TrackTitleHints, requireComplete: true))
            return null;
        return new(
            genre,
            exactEdition ? Text(candidate, "date") : null,
            originalDate,
            exactEdition ? candidateLabel.Label : null,
            exactEdition ? candidateLabel.CatalogNumber : null,
            exactEdition ? Text(candidate, "barcode") : null,
            exactEdition ? Text(candidate, "country") : null,
            genre is null ? null : genreSourceType,
            genre is null ? null : exactEdition ? "high" : "medium",
            genre is null ? null : genreRationale,
            sources,
            exactEdition ? releaseId : null,
            groupId,
            trackMetadata.Titles)
        {
            TrackComposers = trackMetadata.Composers,
            TrackComposerSourceType = trackMetadata.Composers.Any(value => Nonempty(value) is not null)
                ? trackMetadata.Composers.Count(value => Nonempty(value) is not null) > musicBrainzComposerCount
                    ? "discogs_linked_tracklist_credit"
                    : "musicbrainz_work_relationship"
                : null,
            TrackArtists = trackMetadata.Artists,
            TrackArtistSourceType = trackMetadata.Artists.Any(value => Nonempty(value) is not null)
                ? "discogs_linked_track_artist_credit"
                : null,
            ArtworkUrl = linkedDiscogs.ArtworkUrl,
            ArtworkSourceType = linkedDiscogs.ArtworkUrl is null ? null : "discogs_linked_primary_image",
            IsMixedGenreCompilation = mixedGenreCompilation
        };
    }

    private async Task<JsonElement?> SearchMusicBrainzReleaseAsync(
        string album,
        AlbumMetadataQuery query,
        bool requireArtistMatch,
        bool requireTrackMatch,
        CancellationToken token)
    {
        var lucene = requireArtistMatch
            ? $"release:\"{EscapeLucene(album)}\" AND artist:\"{EscapeLucene(query.Artist)}\""
            : $"release:\"{EscapeLucene(album)}\"";
        var searchUri = new Uri($"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(lucene)}&fmt=json&limit=100");
        using var search = await GetMusicBrainzJsonAsync(searchUri, token);
        if (!search.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return null;

        JsonElement? selected = null;
        var selectedScore = int.MinValue;
        foreach (var release in releases.EnumerateArray())
        {
            var title = Text(release, "title");
            var artist = ArtistCredit(release);
            if (!AlbumTitlesEquivalent(title, album) || requireArtistMatch &&
                !ArtistCreditsEquivalent(artist, query.Artist) && !FeaturedCreditContainsArtist(release, query.Artist))
                continue;
            var formatMatch = !query.RequireSacd || HasSacdMedium(release);
            var trackMatch = HasMatchingMedium(release, query.TrackCount, query.RequireSacd);
            if (requireTrackMatch && !trackMatch) continue;
            var releaseYear = Year(Text(release, "date"));
            var catalog = LabelInfo(release).CatalogNumber;
            var score = (Integer(release, "score") ?? 0) + (formatMatch ? 40 : 0) + (trackMatch ? 25 : 0);
            if (query.EditionYear is not null && releaseYear == query.EditionYear) score += 35;
            if (query.OriginalYear is not null && releaseYear == query.OriginalYear) score += 5;
            if (EquivalentCatalog(catalog, query.CatalogNumber)) score += 60;
            if (score <= selectedScore) continue;
            selected = release.Clone();
            selectedScore = score;
        }
        return selected;
    }

    private static IReadOnlyList<string> AlbumTitleCandidates(AlbumMetadataQuery query)
    {
        var values = new[] { query.Album }
            .Concat(query.AlbumTitleHints ?? [])
            .Select(value => AlbumLookupTitle(value, query.Artist))
            .SelectMany(value => new[]
            {
                value,
                Regex.Match(value, @"^.{2,80}\s+[–—-]\s+(?<title>.+)$") is { Success: true } match
                    ? match.Groups["title"].Value.Trim()
                    : value
            })
            .SelectMany(AlbumSearchVariants)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 ? [query.Album] : values;
    }

    private static IEnumerable<string> AlbumSearchVariants(string value)
    {
        yield return value;
        var core = Regex.Replace(value,
            @"\s*[\(\[]\s*(?:(?:19|20)\d{2}|sacd(?:-r)?|\d{2,3}\s*[-/]\s*\d{2,3})\s*[\)\]]",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        core = Regex.Replace(core,
            @"\s+\d{2,3}\s*[-/]\s*\d{2,3}(?:\s+studio\s+master)?\s*$",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        core = Regex.Replace(core, @"\s+sampler\s*$", string.Empty, RegexOptions.IgnoreCase).Trim();
        foreach (var candidate in new[] { core, WithoutLeadingThe(core) }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var collection = Regex.Replace(candidate,
                @"\bsuper\s+audio\s+(?:(?:sound|surround)\s+)?collection\b",
                "Super Audio Collection", RegexOptions.IgnoreCase);
            var surroundCollection = Regex.Replace(collection,
                @"\bsuper\s+audio\s+collection\b", "Super Audio Surround Collection",
                RegexOptions.IgnoreCase);
            foreach (var title in new[] { candidate, collection, surroundCollection }
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                yield return title;
                var abbreviated = Regex.Replace(title, @"\bvolume\s+(?=\d)", "Vol. ", RegexOptions.IgnoreCase);
                if (!abbreviated.Equals(title, StringComparison.OrdinalIgnoreCase)) yield return abbreviated;
            }
        }
    }

    private static bool TrackMetadataMatchesHints(
        MusicBrainzTrackMetadata metadata,
        IReadOnlyList<string>? hints,
        bool requireComplete = false,
        int? maximumMismatches = null)
    {
        if (metadata.Titles.Count == 0) return false;
        if (hints is not { Count: > 0 }) return true;
        if (hints.Count != metadata.Titles.Count) return false;
        var matches = hints.Zip(metadata.Titles).Count(pair => TrackTitlesEquivalent(pair.First, pair.Second));
        var required = maximumMismatches is { } allowed
            ? Math.Max(1, metadata.Titles.Count - Math.Max(0, allowed))
            : requireComplete ? metadata.Titles.Count : Math.Max(1, metadata.Titles.Count * 3 / 4);
        return matches >= required;
    }

    private static MusicBrainzTrackMetadata MergeTrackMetadata(
        MusicBrainzTrackMetadata primary,
        MusicBrainzTrackMetadata fallback,
        int trackCount)
    {
        if (fallback.Titles.Count != trackCount) return primary;
        if (primary.Titles.Count != trackCount) return fallback;
        var composers = Enumerable.Range(0, trackCount)
            .Select(index => Nonempty(primary.Composers.ElementAtOrDefault(index)) ??
                             Nonempty(fallback.Composers.ElementAtOrDefault(index)) ?? string.Empty)
            .ToArray();
        var artists = Enumerable.Range(0, trackCount)
            .Select(index => Nonempty(primary.Artists.ElementAtOrDefault(index)) ??
                             Nonempty(fallback.Artists.ElementAtOrDefault(index)) ?? string.Empty)
            .ToArray();
        return new(primary.Titles, composers) { Artists = artists };
    }

    private async Task<IReadOnlyList<string>> ResolveMusicBrainzTrackTitlesAsync(
        string releaseId,
        int expectedTrackCount,
        bool requireSacd,
        CancellationToken token) =>
        (await ResolveMusicBrainzTrackMetadataAsync(releaseId, expectedTrackCount, requireSacd, includeRelationships: false, token)).Titles;

    private async Task<MusicBrainzTrackMetadata> ResolveMusicBrainzTrackMetadataAsync(
        string releaseId,
        int expectedTrackCount,
        bool requireSacd,
        bool includeRelationships,
        CancellationToken token)
    {
        var includes = includeRelationships
            ? "recordings%2Brecording-level-rels%2Bwork-rels%2Bwork-level-rels%2Bartist-rels"
            : "recordings";
        var uri = new Uri($"https://musicbrainz.org/ws/2/release/{Uri.EscapeDataString(releaseId)}?inc={includes}&fmt=json");
        using var details = await GetMusicBrainzJsonAsync(uri, token);
        if (!details.RootElement.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
            return new([], []);
        var matchingMedia = OrderedByPosition(media)
            .Where(value => MediumTrackCount(value) == expectedTrackCount)
            .Where(value => !requireSacd || IsSacdMediumFormat(Text(value, "format")))
            .ToArray();
        if (matchingMedia.Length == 0) return new([], []);
        var metadataSets = matchingMedia
            .Where(value => value.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            .Select(value => OrderedByPosition(value.GetProperty("tracks")).Select(track => new
            {
                Title = Text(track, "title") ?? (track.TryGetProperty("recording", out var recording) ? Text(recording, "title") : null),
                Composer = includeRelationships ? MusicBrainzComposer(track) : null
            }).ToArray())
            .Where(values => values.Length == expectedTrackCount && values.All(value => value.Title is not null))
            .Select(values => new MusicBrainzTrackMetadata(
                values.Select(value => value.Title!).ToArray(),
                values.Select(value => value.Composer ?? string.Empty).ToArray()))
            .GroupBy(value => string.Join('\0', value.Titles.Select(IdentityKey)) + "\u0001" +
                              string.Join('\0', value.Composers.Select(IdentityKey)), StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        return metadataSets.Length == 1 ? metadataSets[0] : new([], []);
    }

    private static string? MusicBrainzComposer(JsonElement track)
    {
        if (!track.TryGetProperty("recording", out var recording) ||
            !recording.TryGetProperty("relations", out var recordingRelations) ||
            recordingRelations.ValueKind != JsonValueKind.Array)
            return null;

        var composers = new List<string>();
        foreach (var performance in recordingRelations.EnumerateArray())
        {
            if (Text(performance, "type")?.Equals("performance", StringComparison.OrdinalIgnoreCase) != true ||
                !performance.TryGetProperty("work", out var work) ||
                !work.TryGetProperty("relations", out var workRelations) || workRelations.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var relation in workRelations.EnumerateArray())
            {
                if (Text(relation, "type")?.Equals("composer", StringComparison.OrdinalIgnoreCase) != true ||
                    !relation.TryGetProperty("artist", out var artist) || Nonempty(Text(artist, "name")) is not { } composer)
                    continue;
                composers.Add(composer);
            }
        }
        var distinct = composers.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private async Task<ProviderMetadata?> ResolveDiscogsAsync(AlbumMetadataQuery query, CancellationToken token)
    {
        var searches = new List<(string Album, bool RequireArtist)> { (AlbumLookupTitle(query.Album, query.Artist), true) };
        if (query.TrackTitleHints?.Count == query.TrackCount)
            searches.AddRange(AlbumTitleCandidates(query).Select(title => (title, false)));

        foreach (var (lookupAlbum, requireArtist) in searches.Distinct())
        {
            var parameters = new List<KeyValuePair<string, string>>
            {
                new("type", "release"), new("release_title", lookupAlbum), new("per_page", "50")
            };
            if (requireArtist)
                parameters.Add(new("artist", ArtistCreditsEquivalent(query.Artist, "Various Artists") ? "Various" : query.Artist));
            if (query.RequireSacd) parameters.Add(new("format", "SACD"));
            if (query.EditionYear is not null) parameters.Add(new("year", query.EditionYear.Value.ToString(CultureInfo.InvariantCulture)));
            using var search = await GetDiscogsJsonAsync(new Uri("https://api.discogs.com/database/search?" + Query(parameters)), token);
            if (!search.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;

            foreach (var result in results.EnumerateArray())
            {
                var combined = Text(result, "title") ?? string.Empty;
                var separator = combined.IndexOf(" - ", StringComparison.Ordinal);
                var artist = separator >= 0 ? combined[..separator] : string.Empty;
                var album = separator >= 0 ? combined[(separator + 3)..] : combined;
                artist = Regex.Replace(artist, "\\s*\\(\\d+\\)$", string.Empty);
                if (!AlbumTitlesEquivalent(album, lookupAlbum) || requireArtist && !ArtistCreditsEquivalent(artist, query.Artist)) continue;
                var formats = StringArray(result, "format");
                if (query.RequireSacd && !formats.Any(value => value.Contains("sacd", StringComparison.OrdinalIgnoreCase))) continue;
                var year = Integer(result, "year");
                if (query.EditionYear is not null && year != query.EditionYear) continue;
                var id = Integer(result, "id");
                if (id is null) continue;

                using var details = await GetDiscogsJsonAsync(new Uri($"https://api.discogs.com/releases/{id.Value}"), token);
                var root = details.RootElement;
                if (!AlbumTitlesEquivalent(Text(root, "title"), lookupAlbum)) continue;
                var hasCompleteHints = query.TrackTitleHints?.Count == query.TrackCount;
                var corroboratedYear = query.EditionYear ?? query.OriginalYear;
                var maximumTrackMismatches = requireArtist && hasCompleteHints && query.TrackCount >= 10 &&
                                             corroboratedYear is not null && year == corroboratedYear
                    ? 1
                    : 0;
                var trackMetadata = DiscogsTrackMetadata(root, query.TrackCount, query.TrackTitleHints);
                if (!TrackMetadataMatchesHints(trackMetadata, query.TrackTitleHints,
                        requireComplete: hasCompleteHints || !requireArtist,
                        maximumMismatches: maximumTrackMismatches))
                    trackMetadata = new([], []);
                if ((!requireArtist || hasCompleteHints) && trackMetadata.Titles.Count != query.TrackCount) continue;

                var discogsGenres = StringArray(root, "genres");
                var genre = ChooseBroadGenre(discogsGenres.Select(value => (value, 20))
                    .Concat(StringArray(root, "styles").Select(value => (value, 5))));
                var label = root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
                    ? labels.EnumerateArray().FirstOrDefault() : default;
                var barcode = root.TryGetProperty("identifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array
                    ? identifiers.EnumerateArray().FirstOrDefault(value => Text(value, "type")?.Contains("barcode", StringComparison.OrdinalIgnoreCase) == true)
                    : default;
                return new(
                    genre, Text(root, "released"), null,
                    label.ValueKind == JsonValueKind.Object ? Text(label, "name") : null,
                    label.ValueKind == JsonValueKind.Object ? Text(label, "catno") : null,
                    barcode.ValueKind == JsonValueKind.Object ? Text(barcode, "value") : null,
                    Text(root, "country"), genre is null ? null : "discogs_exact_release",
                    genre is null ? null : "high",
                    genre is null ? null : "Broad genre selected from the exact Discogs release genres and styles.",
                    [$"https://www.discogs.com/release/{id.Value}"], null, null, trackMetadata.Titles)
                {
                    TrackComposers = trackMetadata.Composers,
                    TrackComposerSourceType = trackMetadata.Composers.Any(value => Nonempty(value) is not null)
                        ? "discogs_exact_release_tracklist_credit"
                        : null,
                    TrackArtists = trackMetadata.Artists,
                    TrackArtistSourceType = trackMetadata.Artists.Any(value => Nonempty(value) is not null)
                        ? "discogs_exact_release_track_artist_credit"
                        : null,
                    ArtworkUrl = DiscogsPrimaryArtwork(root),
                    ArtworkSourceType = DiscogsPrimaryArtwork(root) is null
                        ? null
                        : "discogs_exact_release_primary_image",
                    IsMixedGenreCompilation = ClassicalMetadataPolicy.IsCompilationArtist(query.Artist) &&
                                              HasClassicalAndNonClassicalGenres(discogsGenres)
                };
            }
        }
        return null;
    }

    private async Task<LinkedDiscogsMetadata> ResolveLinkedDiscogsMetadataAsync(
        JsonElement musicBrainzGroup,
        AlbumMetadataQuery query,
        string matchedAlbumTitle,
        bool allowArtistMismatch,
        CancellationToken token)
    {
        if (!musicBrainzGroup.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array)
            return new(null, null, [], []);
        foreach (var relation in relations.EnumerateArray())
        {
            if (Text(relation, "type")?.Equals("discogs", StringComparison.OrdinalIgnoreCase) != true ||
                !relation.TryGetProperty("url", out var url)) continue;
            var resource = Text(url, "resource");
            if (resource is null) continue;
            var match = Regex.Match(resource, "discogs\\.com/(?<type>master|release)/(?<id>\\d+)", RegexOptions.IgnoreCase);
            if (!match.Success) continue;
            var type = match.Groups["type"].Value.ToLowerInvariant();
            var id = match.Groups["id"].Value;
            using var details = await GetDiscogsJsonAsync(new Uri($"https://api.discogs.com/{type}s/{id}"), token);
            var root = details.RootElement;
            if (!AlbumTitlesEquivalent(Text(root, "title"), matchedAlbumTitle)) continue;
            var artists = root.TryGetProperty("artists", out var artistNodes) && artistNodes.ValueKind == JsonValueKind.Array
                ? string.Join(" & ", artistNodes.EnumerateArray().Select(value => Regex.Replace(Text(value, "name") ?? string.Empty, "\\s*\\(\\d+\\)$", string.Empty)))
                : null;
            var artistMatches = ArtistCreditsEquivalent(artists, query.Artist);
            var discogsGenres = StringArray(root, "genres");
            var genre = ChooseBroadGenre(discogsGenres.Select(value => (value, 20))
                .Concat(StringArray(root, "styles").Select(value => (value, 5))));
            var trackMetadata = DiscogsTrackMetadata(root, query.TrackCount, query.TrackTitleHints);
            if (!TrackMetadataMatchesHints(trackMetadata, query.TrackTitleHints, requireComplete: allowArtistMismatch))
                trackMetadata = new([], []);
            if (!artistMatches && (!allowArtistMismatch || trackMetadata.Titles.Count != query.TrackCount)) continue;
            return new(genre, resource, trackMetadata.Titles, trackMetadata.Composers)
            {
                TrackArtists = trackMetadata.Artists,
                ArtworkUrl = DiscogsPrimaryArtwork(root),
                IsMixedGenreCompilation = ClassicalMetadataPolicy.IsCompilationArtist(query.Artist) &&
                                          HasClassicalAndNonClassicalGenres(discogsGenres)
            };
        }
        return new(null, null, [], []);
    }

    private static MusicBrainzTrackMetadata DiscogsTrackMetadata(
        JsonElement root,
        int expectedTrackCount,
        IReadOnlyList<string>? titleHints = null)
    {
        if (!root.TryGetProperty("tracklist", out var tracklist) || tracklist.ValueKind != JsonValueKind.Array)
            return new([], []);
        var tracks = tracklist.EnumerateArray()
            .Where(track => Text(track, "type_") is not { } type || type.Equals("track", StringComparison.OrdinalIgnoreCase))
            .Select(track => new
            {
                Title = Text(track, "title"),
                Composer = DiscogsTrackComposer(track),
                Artist = DiscogsTrackArtist(track)
            })
            .Where(track => track.Title is not null)
            .ToArray();
        var metadata = new MusicBrainzTrackMetadata(
            tracks.Select(track => track.Title!).ToArray(),
            tracks.Select(track => track.Composer ?? string.Empty).ToArray())
        {
            Artists = tracks.Select(track => track.Artist ?? string.Empty).ToArray()
        };
        if (tracks.Length == expectedTrackCount) return metadata;
        if (titleHints?.Count != expectedTrackCount || tracks.Length < expectedTrackCount) return new([], []);

        var aligned = Enumerable.Range(0, tracks.Length - expectedTrackCount + 1)
            .Where(start => titleHints.Zip(metadata.Titles.Skip(start).Take(expectedTrackCount))
                .All(pair => TrackTitlesEquivalent(pair.First, pair.Second)))
            .ToArray();
        if (aligned.Length != 1) return new([], []);
        var offset = aligned[0];
        return new MusicBrainzTrackMetadata(
            metadata.Titles.Skip(offset).Take(expectedTrackCount).ToArray(),
            metadata.Composers.Skip(offset).Take(expectedTrackCount).ToArray())
        {
            Artists = metadata.Artists.Skip(offset).Take(expectedTrackCount).ToArray()
        };
    }

    private static string? DiscogsTrackArtist(JsonElement track)
    {
        if (!track.TryGetProperty("artists", out var credits) || credits.ValueKind != JsonValueKind.Array)
            return null;
        var artists = credits.EnumerateArray()
            .Select(credit => Text(credit, "name"))
            .Where(value => value is not null)
            .Select(value => Regex.Replace(value!, @"\s*\(\d+\)$", string.Empty).Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return artists.Length == 0 ? null : string.Join(" & ", artists);
    }

    private static string? DiscogsPrimaryArtwork(JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return null;
        var candidates = images.EnumerateArray()
            .OrderByDescending(image => Text(image, "type")?.Equals("primary", StringComparison.OrdinalIgnoreCase) == true)
            .ToArray();
        foreach (var image in candidates)
        {
            var value = Text(image, "uri") ?? Text(image, "resource_url");
            if (value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Equals("i.discogs.com", StringComparison.OrdinalIgnoreCase))
                return uri.AbsoluteUri;
        }
        return null;
    }

    private static string? DiscogsTrackComposer(JsonElement track)
    {
        if (track.TryGetProperty("extraartists", out var credits) && credits.ValueKind == JsonValueKind.Array)
        {
            var composers = credits.EnumerateArray()
                .Where(credit => Regex.IsMatch(Text(credit, "role") ?? string.Empty,
                    @"\b(?:composed\s+by|composer|music\s+by|written-by)\b", RegexOptions.IgnoreCase))
                .Select(credit => Text(credit, "name"))
                .Where(value => value is not null)
                .Select(value => Regex.Replace(value!, @"\s*\(\d+\)$", string.Empty).Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (composers.Length > 0) return string.Join("; ", composers);
        }

        var title = Text(track, "title");
        if (title is null) return null;
        var embedded = EmbeddedComposerCredit(title);
        if (embedded is not null) return embedded;
        var prefix = Regex.Match(title,
            @"^\s*(?<composer>[\p{L}\p{M}][\p{L}\p{M}.''’\-]*(?:\s+[\p{L}\p{M}][\p{L}\p{M}.''’\-]*){0,4}(?:\s*(?:,|&|;)\s*[\p{L}\p{M}][\p{L}\p{M}.''’\-]*(?:\s+[\p{L}\p{M}][\p{L}\p{M}.''’\-]*){0,4})*)\s*:\s+",
            RegexOptions.CultureInvariant);
        if (!prefix.Success) return null;
        var value = prefix.Groups["composer"].Value.Trim();
        return value.Equals("Anon", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("Anonymous", StringComparison.OrdinalIgnoreCase)
            ? "Anonymous"
            : value;
    }

    private async Task<ProviderMetadata?> ResolveAppleAsync(AlbumMetadataQuery query, CancellationToken token)
    {
        var lookupAlbum = AlbumLookupTitle(query.Album, query.Artist);
        var term = Uri.EscapeDataString($"{query.Artist} {lookupAlbum}");
        using var document = await GetJsonAsync(new Uri($"https://itunes.apple.com/search?term={term}&entity=album&limit=25"), token);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var result in results.EnumerateArray())
        {
            if (!ArtistCreditsEquivalent(Text(result, "artistName"), query.Artist) || !AlbumTitlesEquivalent(Text(result, "collectionName"), lookupAlbum)) continue;
            var trackCount = Integer(result, "trackCount");
            if (query.TrackCount > 0 && trackCount != query.TrackCount) continue;
            var releaseDate = Text(result, "releaseDate");
            if (query.OriginalYear is not null && Year(releaseDate) != query.OriginalYear) continue;
            var genre = ChooseBroadGenre([(Text(result, "primaryGenreName") ?? string.Empty, 20)]);
            var source = Text(result, "collectionViewUrl") ?? "https://music.apple.com/";
            return new(
                genre,
                null,
                releaseDate,
                null,
                null,
                null,
                null,
                genre is null ? null : "apple_music_catalog",
                genre is null ? null : "medium",
                genre is null ? null : "Broad genre selected from an exact Apple Music artist, album, year, and track-count match.",
                [source],
                null,
                null,
                []);
        }
        return null;
    }

    private async Task<JsonDocument> GetMusicBrainzJsonAsync(Uri uri, CancellationToken token)
    {
        await MusicBrainzGate.WaitAsync(token);
        try
        {
            var delay = _musicBrainzMinimumInterval - (DateTimeOffset.UtcNow - _lastMusicBrainzRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            return await GetJsonAsync(uri, token);
        }
        finally
        {
            _lastMusicBrainzRequest = DateTimeOffset.UtcNow;
            MusicBrainzGate.Release();
        }
    }

    private async Task<JsonDocument> GetDiscogsJsonAsync(Uri uri, CancellationToken token)
    {
        await DiscogsGate.WaitAsync(token);
        try
        {
            var delay = _discogsMinimumInterval - (DateTimeOffset.UtcNow - _lastDiscogsRequest);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            return await GetJsonAsync(uri, token, discogs: true);
        }
        finally
        {
            _lastDiscogsRequest = DateTimeOffset.UtcNow;
            DiscogsGate.Release();
        }
    }

    private async Task<JsonDocument> GetJsonAsync(Uri uri, CancellationToken token, bool discogs = false)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (discogs && _discogsToken is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Discogs", $"token={_discogsToken}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(_requestTimeout);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var transient = response.StatusCode is System.Net.HttpStatusCode.TooManyRequests or
                System.Net.HttpStatusCode.BadGateway or System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.GatewayTimeout;
            if (attempt == 0 && transient)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(1100);
                if (delay < TimeSpan.FromMilliseconds(1100)) delay = TimeSpan.FromMilliseconds(1100);
                await Task.Delay(delay > TimeSpan.FromSeconds(2) ? TimeSpan.FromSeconds(2) : delay, token);
                continue;
            }
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        throw new HttpRequestException("External metadata lookup exhausted its retry policy.");
    }

    private static async Task TryProviderAsync(
        string provider,
        Func<Task<ProviderMetadata?>> lookup,
        ICollection<ProviderMetadata> matches,
        ICollection<string> warnings,
        CancellationToken token)
    {
        try
        {
            var match = await lookup();
            if (match is null) warnings.Add($"{provider} returned no sufficiently exact album match.");
            else matches.Add(match);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            warnings.Add($"{provider} metadata lookup timed out; extraction will continue.");
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        {
            warnings.Add($"{provider} metadata lookup failed ({error.GetType().Name}: {error.Message}); extraction will continue.");
        }
    }

    private static string? First(IEnumerable<ProviderMetadata> values, Func<ProviderMetadata, string?> selector) =>
        values.Select(selector).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static (string? Label, string? CatalogNumber) LabelInfo(JsonElement release)
    {
        if (!release.TryGetProperty("label-info", out var values) || values.ValueKind != JsonValueKind.Array) return (null, null);
        var first = values.EnumerateArray().FirstOrDefault();
        var label = first.ValueKind == JsonValueKind.Object && first.TryGetProperty("label", out var labelNode) ? Text(labelNode, "name") : null;
        return (label, first.ValueKind == JsonValueKind.Object ? Text(first, "catalog-number") : null);
    }

    private static string? ArtistCredit(JsonElement release)
    {
        if (!release.TryGetProperty("artist-credit", out var values) || values.ValueKind != JsonValueKind.Array) return null;
        return string.Concat(values.EnumerateArray().Select(value => (Text(value, "name") ?? string.Empty) + (Text(value, "joinphrase") ?? string.Empty))).Trim();
    }

    private static IReadOnlyList<string> MediaFormats(JsonElement release)
    {
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return [];
        return media.EnumerateArray().Select(value => Text(value, "format")).Where(value => value is not null).Select(value => value!).ToArray();
    }

    private static bool HasSacdMedium(JsonElement release) =>
        release.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array &&
        media.EnumerateArray().Any(value => IsSacdMediumFormat(Text(value, "format")));

    private static bool HasMatchingMedium(JsonElement release, int expectedTrackCount, bool requireSacd)
    {
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return false;
        return media.EnumerateArray().Any(value =>
            (!requireSacd || IsSacdMediumFormat(Text(value, "format"))) &&
            (expectedTrackCount <= 0 || MediumTrackCount(value) == expectedTrackCount));
    }

    private static int? MediumTrackCount(JsonElement medium) =>
        Integer(medium, "track-count") ??
        (medium.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array
            ? tracks.GetArrayLength()
            : null);

    private static bool IsSacdMediumFormat(string? format) =>
        format?.Contains("sacd", StringComparison.OrdinalIgnoreCase) == true &&
        !Regex.IsMatch(format, "(?<![A-Za-z])CD\\s+layer", RegexOptions.IgnoreCase);

    private static IEnumerable<JsonElement> OrderedByPosition(JsonElement values) => values.EnumerateArray()
        .Select((value, index) => new { Value = value, Index = index, Position = Integer(value, "position") ?? index + 1 })
        .OrderBy(value => value.Position)
        .ThenBy(value => value.Index)
        .Select(value => value.Value);

    private static IEnumerable<(string Name, int Weight)> WeightedNames(JsonElement root, string property, int defaultWeight)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) yield break;
        foreach (var value in values.EnumerateArray())
        {
            var name = Text(value, "name");
            if (name is not null) yield return (name, Integer(value, "count") ?? defaultWeight);
        }
    }

    private static IReadOnlyList<string> StringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray();
    }

    private static string? BroadGenre(string value)
    {
        var text = value.Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        var mappings = new (string Needle, string Genre)[]
        {
            ("opera", "Opera"), ("classical", "Classical"), ("choral", "Choral"), ("metal", "Metal"),
            ("jazz", "Jazz"), ("rock", "Rock"), ("pop", "Pop"), ("folk", "Folk"),
            ("electronic", "Electronic"), ("electronica", "Electronic"), ("soundtrack", "Soundtrack"),
            ("spoken", "Spoken Word"), ("blues", "Blues"), ("country", "Country"),
            ("reggae", "Reggae"), ("soul", "Soul"), ("funk", "Funk"), ("hip hop", "Hip-Hop"),
            ("rap", "Hip-Hop"), ("latin", "Latin"), ("world", "World")
        };
        return mappings.FirstOrDefault(mapping => text.Contains(mapping.Needle, StringComparison.OrdinalIgnoreCase)).Genre;
    }

    private static bool HasClassicalAndNonClassicalGenres(IEnumerable<string> values)
    {
        var genres = values.Select(BroadGenre).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var hasClassical = genres.Any(value => value is "Classical" or "Opera" or "Choral");
        return hasClassical && genres.Any(value => value is not ("Classical" or "Opera" or "Choral"));
    }

    private static bool Equivalent(string? left, string? right) => left is not null && right is not null && Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    internal static bool TrackTitlesEquivalent(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftIdentity = Normalize(left);
        var rightIdentity = Normalize(right);
        if (leftIdentity.Equals(rightIdentity, StringComparison.Ordinal)) return true;
        if (Math.Min(leftIdentity.Length, rightIdentity.Length) >= 4 &&
            (leftIdentity.EndsWith(rightIdentity, StringComparison.Ordinal) ||
             rightIdentity.EndsWith(leftIdentity, StringComparison.Ordinal))) return true;

        var embeddedCreditLeft = WithoutEmbeddedComposerCredit(left);
        var embeddedCreditRight = WithoutEmbeddedComposerCredit(right);
        if (TrackTitleVariantsEquivalent(embeddedCreditLeft, embeddedCreditRight)) return true;

        var uncreditedLeft = Normalize(WithoutLeadingComposerCredit(embeddedCreditLeft));
        var uncreditedRight = Normalize(WithoutLeadingComposerCredit(embeddedCreditRight));
        if (uncreditedLeft.Equals(uncreditedRight, StringComparison.Ordinal) ||
            Math.Min(uncreditedLeft.Length, uncreditedRight.Length) >= 8 &&
            (uncreditedLeft.StartsWith(uncreditedRight, StringComparison.Ordinal) ||
             uncreditedRight.StartsWith(uncreditedLeft, StringComparison.Ordinal)))
            return true;

        var leftTokens = TitleTokens(WithoutLeadingComposerCredit(embeddedCreditLeft));
        var rightTokens = TitleTokens(WithoutLeadingComposerCredit(embeddedCreditRight));
        if (Math.Min(leftTokens.Count, rightTokens.Count) < 4) return false;
        var common = LongestCommonSubsequenceLength(leftTokens, rightTokens);
        return common * 5 >= Math.Max(leftTokens.Count, rightTokens.Count) * 4;
    }

    internal static bool AlbumTitlesEquivalent(string? left, string? right)
    {
        if (left is null || right is null) return false;
        if (Equivalent(left, right)) return true;
        var leftIdentity = AlbumTitleIdentity(left);
        var rightIdentity = AlbumTitleIdentity(right);
        return leftIdentity.Length >= 8 && leftIdentity.Equals(rightIdentity, StringComparison.Ordinal);
    }

    internal static string AlbumLookupTitle(string value, string? artist = null)
    {
        var result = Regex.Replace(value,
            "\\s*[\\(\\[]\\s*(?:society\\s+of\\s+sound|womad\\s+\\d{4}|(?:deluxe|limited|special|expanded|remaster(?:ed)?)\\s+edition)[^\\)\\]]*[\\)\\]]\\s*$",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        result = Regex.Replace(result,
            "\\s+[–—-]\\s+(?:a\\s+selection|society\\s+of\\s+sound|(?:deluxe|limited|special|expanded|remaster(?:ed)?)\\s+edition)\\s*$",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        if (!string.IsNullOrWhiteSpace(artist))
            result = Regex.Replace(result,
                $@"^\s*{Regex.Escape(artist.Trim())}\s*[–—:-]\s*", string.Empty,
                RegexOptions.IgnoreCase).Trim();
        return result;
    }
    internal static bool ArtistCreditsEquivalent(string? left, string? right)
    {
        if (left is null || right is null) return false;
        var leftIdentity = Normalize(WithoutLeadingThe(left));
        var rightIdentity = Normalize(WithoutLeadingThe(right));
        if (leftIdentity.Equals(rightIdentity, StringComparison.Ordinal)) return true;
        if (leftIdentity is "various" or "variousartists" && rightIdentity is "various" or "variousartists")
            return true;
        return FeaturedCreditBase(left).Equals(rightIdentity, StringComparison.Ordinal) ||
               FeaturedCreditBase(right).Equals(leftIdentity, StringComparison.Ordinal);
    }

    private static string WithoutLeadingComposerCredit(string value)
    {
        var match = Regex.Match(value,
            @"^\s*[\p{L}\p{M}][\p{L}\p{M}.''’\-]*(?:\s+[\p{L}\p{M}][\p{L}\p{M}.''’\-]*){0,4}(?:\s*(?:,|&|;)\s*[\p{L}\p{M}][\p{L}\p{M}.''’\-]*(?:\s+[\p{L}\p{M}][\p{L}\p{M}.''’\-]*){0,4})*\s*:\s+(?<title>.+)$",
            RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["title"].Value : value;
    }

    private static string WithoutEmbeddedComposerCredit(string value) =>
        EmbeddedComposerPattern().Replace(value, match =>
            LocalTrackRepairProcessor.IsKnownClassicalComposerCredit(match.Groups["composer"].Value)
                ? " "
                : match.Value).Trim();

    private static string? EmbeddedComposerCredit(string value)
    {
        foreach (Match match in EmbeddedComposerPattern().Matches(value))
        {
            var candidate = match.Groups["composer"].Value.Trim();
            if (LocalTrackRepairProcessor.IsKnownClassicalComposerCredit(candidate)) return candidate;
        }
        return null;
    }

    private static bool TrackTitleVariantsEquivalent(string left, string right)
    {
        var leftIdentity = Normalize(left);
        var rightIdentity = Normalize(right);
        if (leftIdentity.Equals(rightIdentity, StringComparison.Ordinal)) return true;
        if (Math.Min(leftIdentity.Length, rightIdentity.Length) >= 8 &&
            (leftIdentity.StartsWith(rightIdentity, StringComparison.Ordinal) ||
             rightIdentity.StartsWith(leftIdentity, StringComparison.Ordinal))) return true;
        var leftTokens = TitleTokens(left);
        var rightTokens = TitleTokens(right);
        if (TrackTitleTokensContained(leftTokens, rightTokens)) return true;
        if (Math.Min(leftTokens.Count, rightTokens.Count) < 4) return false;
        var common = LongestCommonSubsequenceLength(leftTokens, rightTokens);
        return common * 5 >= Math.Max(leftTokens.Count, rightTokens.Count) * 4;
    }

    private static bool TrackTitleTokensContained(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var subset = left.Count <= right.Count ? left : right;
        var superset = left.Count <= right.Count ? right : left;
        if (subset.Count < 2 || subset.Count(token => token.Length >= 3) < 2) return false;
        var used = new bool[superset.Count];
        var fuzzyMatches = 0;
        foreach (var token in subset)
        {
            var exact = Enumerable.Range(0, superset.Count)
                .FirstOrDefault(index => !used[index] && token.Equals(superset[index], StringComparison.Ordinal), -1);
            if (exact >= 0)
            {
                used[exact] = true;
                continue;
            }
            if (subset.Count < 4 || fuzzyMatches > 0) return false;
            var fuzzy = Enumerable.Range(0, superset.Count)
                .FirstOrDefault(index => !used[index] && NearTitleToken(token, superset[index]), -1);
            if (fuzzy < 0) return false;
            used[fuzzy] = true;
            fuzzyMatches++;
        }
        return true;
    }

    private static bool NearTitleToken(string left, string right)
    {
        if (left.Length < 4 || right.Length < 4 || left[0] != right[0]) return false;
        var maximum = Math.Min(left.Length, right.Length) >= 5 ? 2 : 1;
        if (Math.Abs(left.Length - right.Length) > maximum) return false;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var leftIndex = 0; leftIndex < left.Length; leftIndex++)
        {
            current[0] = leftIndex + 1;
            for (var rightIndex = 0; rightIndex < right.Length; rightIndex++)
                current[rightIndex + 1] = Math.Min(
                    Math.Min(current[rightIndex] + 1, previous[rightIndex + 1] + 1),
                    previous[rightIndex] + (left[leftIndex] == right[rightIndex] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[right.Length] <= maximum;
    }

    [GeneratedRegex(@"\(\s*(?<composer>[\p{L}\p{M}][\p{L}\p{M}.''’\-]*(?:\s+[\p{L}\p{M}][\p{L}\p{M}.''’\-]*){1,5})\s*\)",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedComposerPattern();

    private static IReadOnlyList<string> TitleTokens(string value) => Regex.Matches(value, @"[\p{L}\p{M}\p{Nd}]+")
        .Select(match => Normalize(match.Value))
        .Where(token => token.Length > 0)
        .ToArray();

    private static int LongestCommonSubsequenceLength(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        var previous = new int[right.Count + 1];
        var current = new int[right.Count + 1];
        for (var leftIndex = 0; leftIndex < left.Count; leftIndex++)
        {
            Array.Clear(current);
            for (var rightIndex = 0; rightIndex < right.Count; rightIndex++)
                current[rightIndex + 1] = left[leftIndex].Equals(right[rightIndex], StringComparison.Ordinal)
                    ? previous[rightIndex] + 1
                    : Math.Max(previous[rightIndex + 1], current[rightIndex]);
            (previous, current) = (current, previous);
        }
        return previous[right.Count];
    }

    private static string AlbumTitleIdentity(string value) => Normalize(WithoutLeadingThe(Regex.Replace(
        AlbumLookupTitle(value), @"\bvolume\b", "vol", RegexOptions.IgnoreCase)));

    private static string FeaturedCreditBase(string value) => Normalize(WithoutLeadingThe(
        Regex.Replace(value, @"\s+(?:feat(?:uring)?\.?|ft\.?)\s+.+$", string.Empty,
            RegexOptions.IgnoreCase).Trim()));

    internal static bool FeaturedCreditContainsArtist(JsonElement release, string artist)
    {
        if (!release.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array ||
            !credits.EnumerateArray().Any(value => Regex.IsMatch(Text(value, "joinphrase") ?? string.Empty,
                @"\b(?:feat(?:uring)?\.?|ft\.?)\b", RegexOptions.IgnoreCase)))
            return false;
        return credits.EnumerateArray().Any(value => ArtistCreditsEquivalent(Text(value, "name"), artist));
    }
    private static bool EquivalentCatalog(string? left, string? right) => Nonempty(left) is not null && Nonempty(right) is not null && Normalize(left!).Equals(Normalize(right!), StringComparison.Ordinal);
    private static string IdentityKey(string value) => Normalize(WithoutLeadingThe(value));
    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }
    private static string WithoutLeadingThe(string value) => Regex.Replace(value, "^\\s*the\\s+", string.Empty, RegexOptions.IgnoreCase);

    private static int? Year(string? value) => value is not null && Regex.Match(value, "(?:19|20)\\d{2}") is { Success: true } match && int.TryParse(match.Value, CultureInfo.InvariantCulture, out var year) ? year : null;
    private static string EscapeLucene(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? Nonempty(value.GetString()) : null;
    private static int? Integer(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String &&
               int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HttpClient CreateSharedClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    private static readonly IReadOnlyDictionary<string, (string Genre, string Source)> CuratedReleaseGenres =
        new Dictionary<string, (string Genre, string Source)>(StringComparer.OrdinalIgnoreCase)
        {
            ["6996d8df-7ab3-4b33-b3c2-67af7d45955f"] =
                ("Blues", "https://musify.club/release/bee-mc-cash-is-king-2016-793083"),
            ["d1e3684c-549d-441d-a188-ec4e06ba12f0"] =
                ("Pop", "https://www.muziekweb.nl/en/Link/JK207876/Hollie-Stephenson-bonus-track")
        };

    private sealed record ProviderMetadata(
        string? Genre,
        string? ReleaseDate,
        string? OriginalDate,
        string? Label,
        string? CatalogNumber,
        string? Barcode,
        string? ReleaseCountry,
        string? GenreSourceType,
        string? GenreConfidence,
        string? GenreRationale,
        IReadOnlyList<string> Sources,
        string? MusicBrainzReleaseId,
        string? MusicBrainzReleaseGroupId,
        IReadOnlyList<string> TrackTitles)
    {
        public IReadOnlyList<string> TrackComposers { get; init; } = [];
        public string? TrackComposerSourceType { get; init; }
        public IReadOnlyList<string> TrackArtists { get; init; } = [];
        public string? TrackArtistSourceType { get; init; }
        public string? ArtworkUrl { get; init; }
        public string? ArtworkSourceType { get; init; }
        public bool IsMixedGenreCompilation { get; init; }
    }

    private sealed record MusicBrainzTrackMetadata(
        IReadOnlyList<string> Titles,
        IReadOnlyList<string> Composers)
    {
        public IReadOnlyList<string> Artists { get; init; } = [];
    }

    private sealed record LinkedDiscogsMetadata(
        string? Genre,
        string? Source,
        IReadOnlyList<string> TrackTitles,
        IReadOnlyList<string> TrackComposers)
    {
        public IReadOnlyList<string> TrackArtists { get; init; } = [];
        public string? ArtworkUrl { get; init; }
        public bool IsMixedGenreCompilation { get; init; }
    }
}
