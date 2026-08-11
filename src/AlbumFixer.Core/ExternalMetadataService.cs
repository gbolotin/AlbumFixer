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
    string? CatalogNumber = null);

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
}

public sealed class ExternalMetadataService
{
    private const string UserAgent = "AlbumFixer/1.0 (https://github.com/gbolotin/AlbumFixer)";
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private static readonly SemaphoreSlim MusicBrainzGate = new(1, 1);
    private static DateTimeOffset _lastMusicBrainzRequest = DateTimeOffset.MinValue;

    private readonly HttpClient _client;
    private readonly string? _discogsToken;
    private readonly TimeSpan _musicBrainzMinimumInterval;
    private readonly TimeSpan _requestTimeout;

    public ExternalMetadataService(
        HttpClient? client = null,
        string? discogsToken = null,
        TimeSpan? musicBrainzMinimumInterval = null,
        TimeSpan? requestTimeout = null)
    {
        _client = client ?? SharedClient;
        _discogsToken = Nonempty(discogsToken) ?? Nonempty(Environment.GetEnvironmentVariable("DISCOGS_TOKEN"));
        _musicBrainzMinimumInterval = musicBrainzMinimumInterval ?? TimeSpan.FromMilliseconds(1100);
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
        if (_discogsToken is null)
        {
            warnings.Add("Authenticated Discogs database search was skipped because DISCOGS_TOKEN is not configured; public Discogs records linked by MusicBrainz are still checked.");
        }
        else
        {
            await TryProviderAsync("Discogs", () => ResolveDiscogsAsync(query, token), matches, warnings, token);
        }

        await TryProviderAsync("MusicBrainz", () => ResolveMusicBrainzAsync(query, includeTrackTitles, token), matches, warnings, token);
        await TryProviderAsync("Apple Music", () => ResolveAppleAsync(query, token), matches, warnings, token);

        var genreMatch = matches.FirstOrDefault(match => match.Genre is not null);
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
            matches.Select(match => match.TrackTitles).FirstOrDefault(titles => titles.Count == query.TrackCount) ?? []);
    }

    public Task<ExternalAlbumMetadata> ResolveAsync(
        AlbumMetadataQuery query,
        CancellationToken token) => ResolveAsync(query, includeTrackTitles: false, token);

    public async Task<DownloadedArtwork> DownloadFrontCoverAsync(
        string releaseId,
        CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);
        const long maximumDownloadBytes = 15L * 1024 * 1024;
        var source = new Uri($"https://coverartarchive.org/release/{Uri.EscapeDataString(releaseId)}/front-500");
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("image/jpeg, image/png;q=0.9, image/*;q=0.8");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_requestTimeout);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is null || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Cover Art Archive returned a non-image response.");
        if (response.Content.Headers.ContentLength is > maximumDownloadBytes)
            throw new InvalidDataException("Cover Art Archive returned an unexpectedly large image.");

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
                throw new InvalidDataException("Cover Art Archive image exceeded the download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
        }
        if (total == 0) throw new InvalidDataException("Cover Art Archive returned an empty image.");
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

    private async Task<ProviderMetadata?> ResolveMusicBrainzAsync(
        AlbumMetadataQuery query,
        bool includeTrackTitles,
        CancellationToken token)
    {
        var lucene = $"release:\"{EscapeLucene(query.Album)}\" AND artist:\"{EscapeLucene(query.Artist)}\"";
        var searchUri = new Uri($"https://musicbrainz.org/ws/2/release/?query={Uri.EscapeDataString(lucene)}&fmt=json&limit=100");
        using var search = await GetMusicBrainzJsonAsync(searchUri, token);
        if (!search.RootElement.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array) return null;

        JsonElement? selected = null;
        var selectedScore = int.MinValue;
        foreach (var release in releases.EnumerateArray())
        {
            var title = Text(release, "title");
            var artist = ArtistCredit(release);
            if (!Equivalent(title, query.Album) || !Equivalent(artist, query.Artist)) continue;
            var formats = MediaFormats(release);
            var formatMatch = formats.Any(value => value.Contains("sacd", StringComparison.OrdinalIgnoreCase));
            var trackCount = Integer(release, "track-count") ?? MediaTrackCount(release);
            var trackMatch = query.TrackCount <= 0 || trackCount == query.TrackCount;
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
        if (selected is null) return null;

        var candidate = selected.Value;
        var candidateFormats = MediaFormats(candidate);
        var candidateTrackCount = Integer(candidate, "track-count") ?? MediaTrackCount(candidate);
        var candidateYear = Year(Text(candidate, "date"));
        var candidateLabel = LabelInfo(candidate);
        var exactEdition = candidateFormats.Any(value => value.Contains("sacd", StringComparison.OrdinalIgnoreCase)) &&
                           (query.TrackCount <= 0 || candidateTrackCount == query.TrackCount) &&
                           (query.EditionYear is null || candidateYear == query.EditionYear || EquivalentCatalog(candidateLabel.CatalogNumber, query.CatalogNumber));

        var releaseId = Text(candidate, "id");
        var groupId = candidate.TryGetProperty("release-group", out var group) ? Text(group, "id") : null;
        string? genre = null;
        var genreSourceType = "musicbrainz_release_group";
        var genreRationale = "Broad genre selected from MusicBrainz release-group genres and weighted tags.";
        var sources = new List<string>();
        string? originalDate = null;
        if (groupId is not null)
        {
            var groupUri = new Uri($"https://musicbrainz.org/ws/2/release-group/{Uri.EscapeDataString(groupId)}?inc=genres%2Btags%2Burl-rels&fmt=json");
            using var details = await GetMusicBrainzJsonAsync(groupUri, token);
            originalDate = Text(details.RootElement, "first-release-date");
            var genres = WeightedNames(details.RootElement, "genres", defaultWeight: 10)
                .Concat(WeightedNames(details.RootElement, "tags", defaultWeight: 1));
            genre = ChooseBroadGenre(genres);
            try
            {
                var discogs = await ResolveLinkedDiscogsGenreAsync(details.RootElement, query, token);
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

        if (exactEdition && releaseId is not null) sources.Add($"https://musicbrainz.org/release/{releaseId}");
        if (groupId is not null) sources.Add($"https://musicbrainz.org/release-group/{groupId}");
        var trackTitles = includeTrackTitles && releaseId is not null
            ? await ResolveMusicBrainzTrackTitlesAsync(releaseId, query.TrackCount, token)
            : [];
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
            releaseId,
            groupId,
            trackTitles);
    }

    private async Task<IReadOnlyList<string>> ResolveMusicBrainzTrackTitlesAsync(
        string releaseId,
        int expectedTrackCount,
        CancellationToken token)
    {
        var uri = new Uri($"https://musicbrainz.org/ws/2/release/{Uri.EscapeDataString(releaseId)}?inc=recordings&fmt=json");
        using var details = await GetMusicBrainzJsonAsync(uri, token);
        if (!details.RootElement.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return [];
        var titles = media.EnumerateArray()
            .Where(value => value.TryGetProperty("tracks", out var tracks) && tracks.ValueKind == JsonValueKind.Array)
            .SelectMany(value => value.GetProperty("tracks").EnumerateArray())
            .Select(track => Text(track, "title") ?? (track.TryGetProperty("recording", out var recording) ? Text(recording, "title") : null))
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToArray();
        return titles.Length == expectedTrackCount ? titles : [];
    }

    private async Task<ProviderMetadata?> ResolveDiscogsAsync(AlbumMetadataQuery query, CancellationToken token)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("type", "release"), new("artist", query.Artist), new("release_title", query.Album),
            new("format", "SACD"), new("per_page", "50")
        };
        if (query.EditionYear is not null) parameters.Add(new("year", query.EditionYear.Value.ToString(CultureInfo.InvariantCulture)));
        var searchUri = new Uri("https://api.discogs.com/database/search?" + Query(parameters));
        using var search = await GetJsonAsync(searchUri, token, discogs: true);
        if (!search.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;

        JsonElement? selected = null;
        foreach (var result in results.EnumerateArray())
        {
            var combined = Text(result, "title") ?? string.Empty;
            var separator = combined.IndexOf(" - ", StringComparison.Ordinal);
            var artist = separator >= 0 ? combined[..separator] : string.Empty;
            var album = separator >= 0 ? combined[(separator + 3)..] : combined;
            artist = Regex.Replace(artist, "\\s*\\(\\d+\\)$", string.Empty);
            if (!Equivalent(artist, query.Artist) || !Equivalent(album, query.Album)) continue;
            var formats = StringArray(result, "format");
            if (!formats.Any(value => value.Contains("sacd", StringComparison.OrdinalIgnoreCase))) continue;
            var year = Integer(result, "year");
            if (query.EditionYear is not null && year != query.EditionYear) continue;
            selected = result.Clone();
            break;
        }
        if (selected is null) return null;

        var id = Integer(selected.Value, "id");
        if (id is null) return null;
        using var details = await GetJsonAsync(new Uri($"https://api.discogs.com/releases/{id.Value}"), token, discogs: true);
        var root = details.RootElement;
        var genre = ChooseBroadGenre(StringArray(root, "genres").Select(value => (value, 20))
            .Concat(StringArray(root, "styles").Select(value => (value, 5))));
        var label = root.TryGetProperty("labels", out var labels) && labels.ValueKind == JsonValueKind.Array
            ? labels.EnumerateArray().FirstOrDefault() : default;
        var barcode = root.TryGetProperty("identifiers", out var identifiers) && identifiers.ValueKind == JsonValueKind.Array
            ? identifiers.EnumerateArray().FirstOrDefault(value => Text(value, "type")?.Contains("barcode", StringComparison.OrdinalIgnoreCase) == true)
            : default;
        return new(
            genre,
            Text(root, "released"),
            null,
            label.ValueKind == JsonValueKind.Object ? Text(label, "name") : null,
            label.ValueKind == JsonValueKind.Object ? Text(label, "catno") : null,
            barcode.ValueKind == JsonValueKind.Object ? Text(barcode, "value") : null,
            Text(root, "country"),
            genre is null ? null : "discogs_exact_release",
            genre is null ? null : "high",
            genre is null ? null : "Broad genre selected from the exact Discogs SACD release genres and styles.",
            [$"https://www.discogs.com/release/{id.Value}"],
            null,
            null,
            []);
    }

    private async Task<(string? Genre, string? Source)> ResolveLinkedDiscogsGenreAsync(JsonElement musicBrainzGroup, AlbumMetadataQuery query, CancellationToken token)
    {
        if (!musicBrainzGroup.TryGetProperty("relations", out var relations) || relations.ValueKind != JsonValueKind.Array) return (null, null);
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
            using var details = await GetJsonAsync(new Uri($"https://api.discogs.com/{type}s/{id}"), token);
            var root = details.RootElement;
            if (!Equivalent(Text(root, "title"), query.Album)) continue;
            var artists = root.TryGetProperty("artists", out var artistNodes) && artistNodes.ValueKind == JsonValueKind.Array
                ? string.Join(" & ", artistNodes.EnumerateArray().Select(value => Regex.Replace(Text(value, "name") ?? string.Empty, "\\s*\\(\\d+\\)$", string.Empty)))
                : null;
            if (!Equivalent(artists, query.Artist)) continue;
            var genre = ChooseBroadGenre(StringArray(root, "genres").Select(value => (value, 20))
                .Concat(StringArray(root, "styles").Select(value => (value, 5))));
            return (genre, resource);
        }
        return (null, null);
    }

    private async Task<ProviderMetadata?> ResolveAppleAsync(AlbumMetadataQuery query, CancellationToken token)
    {
        var term = Uri.EscapeDataString($"{query.Artist} {query.Album}");
        using var document = await GetJsonAsync(new Uri($"https://itunes.apple.com/search?term={term}&entity=album&limit=25"), token);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var result in results.EnumerateArray())
        {
            if (!Equivalent(Text(result, "artistName"), query.Artist) || !Equivalent(Text(result, "collectionName"), query.Album)) continue;
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
            warnings.Add($"{provider} metadata lookup failed ({error.GetType().Name}); extraction will continue.");
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

    private static int? MediaTrackCount(JsonElement release)
    {
        if (!release.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array) return null;
        var counts = media.EnumerateArray().Select(value => Integer(value, "track-count")).Where(value => value is not null).Select(value => value!.Value).ToArray();
        return counts.Length == 0 ? null : counts.Sum();
    }

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

    private static bool Equivalent(string? left, string? right) => left is not null && right is not null && Normalize(left).Equals(Normalize(right), StringComparison.Ordinal);
    private static bool EquivalentCatalog(string? left, string? right) => Nonempty(left) is not null && Nonempty(right) is not null && Normalize(left!).Equals(Normalize(right!), StringComparison.Ordinal);
    private static string Normalize(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static int? Year(string? value) => value is not null && Regex.Match(value, "(?:19|20)\\d{2}") is { Success: true } match && int.TryParse(match.Value, CultureInfo.InvariantCulture, out var year) ? year : null;
    private static string EscapeLucene(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
    private static string Query(IEnumerable<KeyValuePair<string, string>> values) => string.Join("&", values.Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
    private static string? Text(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? Nonempty(value.GetString()) : null;
    private static int? Integer(JsonElement element, string property) => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) ? number : null;
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HttpClient CreateSharedClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

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
        IReadOnlyList<string> TrackTitles);
}
