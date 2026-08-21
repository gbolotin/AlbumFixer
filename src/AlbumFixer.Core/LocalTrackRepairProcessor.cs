using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TagFile = TagLib.File;

namespace AlbumFixer.Core;

public sealed partial class LocalTrackRepairProcessor
{
    private readonly ExternalMetadataService _externalMetadata;
    private readonly InMemoryArtworkService _artwork = new();

    public LocalTrackRepairProcessor(ExternalMetadataService externalMetadata)
    {
        _externalMetadata = externalMetadata ?? throw new ArgumentNullException(nameof(externalMetadata));
    }

    public async Task<LocalSplitResult> ProcessAsync(
        ScanResult scan,
        StagedJob staged,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token = default)
    {
        var retainedDsdIso = scan.ImageCount == 1 &&
                             scan.Media.Count(item => item.Kind == "SACD / DSD image") == 1 &&
                             scan.Media.Any(item => item.Kind is "Existing DSF" or "Existing DFF");
        if (scan.Mode != WorkflowMode.ExistingTrackRepair || scan.ImageCount != 0 && !retainedDsdIso)
            throw new NotSupportedException("Verified existing-track repair requires same-format standalone FLAC, DSF, or DFF tracks. DSF or DFF repair may coexist with one retained SACD ISO.");

        var inventory = scan.Media
            .Where(item => item.Kind is "Existing FLAC" or "Existing DSF" or "Existing DFF")
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var formats = inventory.Select(item => Path.GetExtension(item.Path).ToLowerInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (inventory.Length < 2 || formats.Length != 1 || formats[0] is not (".flac" or ".dsf" or ".dff"))
            throw new InvalidOperationException("Existing-track repair requires at least two inventoried, same-format FLAC, DSF, or DFF tracks.");
        var isDsf = formats[0].Equals(".dsf", StringComparison.OrdinalIgnoreCase);
        var isDff = formats[0].Equals(".dff", StringComparison.OrdinalIgnoreCase);
        var formatLabel = isDsf ? "DSF" : isDff ? "DFF" : "FLAC";

        progress.Report(Snapshot(JobPhase.Processing, 19,
            $"Reading existing {formatLabel} tags, embedded artwork, and filename evidence from the verified local source."));
        var tracks = new List<RepairTrack>(inventory.Length);
        foreach (var item in inventory)
        {
            token.ThrowIfCancellationRequested();
            var input = HostStagingService.SafeCombine(staged.InputAlbumRoot, item.RelativePath);
            var output = HostStagingService.SafeCombine(staged.AlbumRoot, item.RelativePath);
            if (!File.Exists(input)) throw new FileNotFoundException($"An inventoried {formatLabel} track is missing from the verified source.", input);
            if (!staged.SourceCacheUsed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await HostStagingService.CopyFileAsync(input, output, null, token);
            }
            else if (!Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The network source cache does not map to the repair output tree.");
            }

            TrackEvidence evidence;
            string payload;
            string fullFile;
            try
            {
                evidence = ReadEvidence(output, item.RelativePath);
                payload = await TrackAudioPayload.Sha256Async(output, token);
                fullFile = await FullFileSha256Async(output, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          TagLib.CorruptFileException or InvalidDataException or InvalidOperationException)
            {
                throw new InvalidDataException(
                    $"Existing {formatLabel} track '{JsonPath(item.RelativePath)}' is corrupt or not structurally repairable: {error.Message}", error);
            }
            tracks.Add(new(item.RelativePath, output, evidence, payload, fullFile, item.Size));
        }
        var deduplication = CollapseExactDuplicates(tracks);
        tracks = deduplication.Tracks.ToList();
        var positionedTracks = PositionTracks(tracks);

        var folderIdentity = ParseFolderIdentity(scan.AlbumName);
        var album = Consensus(tracks.Select(track => track.Evidence.Album)) ?? folderIdentity.Album;
        var existingAlbumArtist = Consensus(tracks.Select(track => track.Evidence.AlbumArtist));
        var trackDerivedAlbumArtist = existingAlbumArtist is null
            ? ResolveTrackAlbumArtist(tracks.Select(track => track.Evidence.Artist))
            : null;
        var albumArtist = existingAlbumArtist
            ?? trackDerivedAlbumArtist
            ?? folderIdentity.Artist;
        var existingYear = ConsensusNumber(tracks.Select(track => track.Evidence.Year));
        var titleHints = positionedTracks
            .Select(position => Nonempty(position.Track.Evidence.Title) ?? position.Track.Evidence.FileTitle)
            .ToArray();
        var warnings = new List<string>();
        ExternalAlbumMetadata? external = null;
        if (album is not null && albumArtist is not null)
        {
            progress.Report(Snapshot(JobPhase.Tagging, 28,
                "Checking external catalogs against the album identity, track count, and existing-tag/filename title evidence."));
            external = await _externalMetadata.ResolveAsync(
                new(album, albumArtist, tracks.Count, OriginalYear: existingYear, RequireSacd: false,
                    TrackTitleHints: titleHints,
                    AlbumTitleHints: new[] { folderIdentity.Album, scan.AlbumName }
                        .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!).ToArray()),
                includeTrackTitles: true,
                token: token);
            warnings.AddRange(external.Warnings);
        }
        else
        {
            warnings.Add("External lookup requires a reliable album and artist from existing tags or the album-folder name.");
        }

        var preparedCover = await PrepareCoverAsync(tracks, scan, staged, external, warnings, token);
        var consensusGenre = Consensus(tracks.Select(track => track.Evidence.Genre));
        var folderGenre = LibraryFolderMetadata.InferGenre(scan.AlbumRoot);
        var contentGenre = InferGenreFromTrackEvidence(tracks
            .Select(track => (track.Evidence.Title, track.Evidence.FileTitle)));
        var isMultiArtistCompilation = albumArtist?.Equals("Various Artists", StringComparison.OrdinalIgnoreCase) == true;
        var genreResolution = ResolveGenre(consensusGenre, external, folderGenre, contentGenre, isMultiArtistCompilation);
        var genre = genreResolution.Value;
        var reviewedComposers = InferReviewedTrackComposers(album, scan.AlbumName,
            positionedTracks.Select(track => (track.Track.Evidence.Title, track.Track.Evidence.FileTitle)).ToArray());
        var inferredComposers = InferClassicalTrackComposers(album, scan.AlbumName, genre,
            positionedTracks.Select(track => (track.Track.Evidence.Title, track.Track.Evidence.FileTitle)).ToArray(),
            isMultiArtistCompilation);
        var externalYear = FindYear(Nonempty(external?.OriginalDate) ?? Nonempty(external?.ReleaseDate));
        var reviewedYear = InferReviewedReleaseYear(album, scan.AlbumName);
        var resolvedYear = existingYear ?? externalYear ?? FindYear(scan.AlbumName) ?? reviewedYear;
        var remoteTitles = external?.TrackTitles.Count == tracks.Count ? external.TrackTitles : [];
        var remoteComposers = external?.TrackComposers.Count == tracks.Count ? external.TrackComposers : [];
        var remoteArtists = external?.TrackArtists.Count == tracks.Count ? external.TrackArtists : [];
        var plans = new List<RepairPlan>(positionedTracks.Count);
        for (var index = 0; index < positionedTracks.Count; index++)
        {
            var positioned = positionedTracks[index];
            var evidence = positioned.Track.Evidence;
            var title = Nonempty(evidence.Title)
                ?? (remoteTitles.Count == tracks.Count ? Nonempty(remoteTitles[index]) : null)
                ?? evidence.FileTitle;
            var existingComposer = Nonempty(evidence.Composer);
            var externalComposer = existingComposer is null && remoteComposers.Count == tracks.Count
                ? Nonempty(remoteComposers[index])
                : null;
            var reviewedComposer = reviewedComposers.Count == positionedTracks.Count
                ? Nonempty(reviewedComposers[index])
                : null;
            var inferredComposer = inferredComposers.Count == positionedTracks.Count
                ? Nonempty(inferredComposers[index])
                : null;
            var filenameComposer = existingComposer is null && externalComposer is null && reviewedComposer is null && inferredComposer is null
                ? InferKnownComposerFromFileTitle(evidence.FileTitle)
                : null;
            var existingArtist = Nonempty(evidence.Artist);
            var externalArtist = remoteArtists.Count == tracks.Count ? Nonempty(remoteArtists[index]) : null;
            var replaceArtistPlaceholder = existingArtist is null || IsVariousArtists(existingArtist);
            var trackArtist = replaceArtistPlaceholder
                ? externalArtist ?? existingArtist ?? albumArtist
                : existingArtist;
            plans.Add(new(
                positioned.Track,
                title,
                Nonempty(evidence.Album) ?? album,
                trackArtist,
                Nonempty(evidence.AlbumArtist) ?? albumArtist,
                positioned.TrackNumber,
                positioned.DiscNumber,
                evidence.Year > 0 ? evidence.Year : resolvedYear is null ? 0u : (uint)resolvedYear.Value,
                Nonempty(evidence.Genre) ?? genre,
                existingComposer ?? externalComposer ?? reviewedComposer ?? inferredComposer ?? filenameComposer,
                existingComposer is not null ? "existing_track_tag"
                    : externalComposer is not null ? external?.TrackComposerSourceType ?? "external_track_credit"
                    : reviewedComposer is not null ? "reviewed_exact_release_track_credit"
                    : inferredComposer is not null ? "corroborated_album_identity"
                    : filenameComposer is not null ? "recognized_composer_in_track_filename"
                    : null,
                Nonempty(evidence.Title) is not null ? "existing_tag" : remoteTitles.Count == tracks.Count ? "external_tracklist" : "filename",
                externalArtist is not null && replaceArtistPlaceholder
                    ? external?.TrackArtistSourceType ?? "external_track_artist_credit"
                    : existingArtist is not null ? "existing_track_tag" : "album_artist_fallback"));
        }

        var duplicateCoordinates = plans
            .GroupBy(plan => (plan.DiscNumber, plan.TrackNumber))
            .Where(group => group.Count() > 1)
            .Select(group => $"disc {group.Key.DiscNumber}, track {group.Key.TrackNumber}")
            .ToArray();
        if (duplicateCoordinates.Length > 0)
            throw new InvalidDataException($"Existing and filename disc/track-number evidence is ambiguous: {string.Join("; ", duplicateCoordinates)}.");

        progress.Report(Snapshot(JobPhase.Tagging, 36,
            preparedCover is null
                ? "Writing repaired tags locally; no usable embedded, local, or external front cover was found."
                : "Writing repaired tags and the highest-priority available front cover locally."));
        var perDiscTrackTotals = plans.GroupBy(plan => plan.DiscNumber)
            .ToDictionary(group => group.Key, group => (uint)group.Count());
        var discTotal = Math.Max(
            plans.Max(plan => plan.DiscNumber),
            plans.Max(plan => plan.Track.Evidence.DiscCount));
        foreach (var plan in plans)
        {
            token.ThrowIfCancellationRequested();
            if (Path.GetExtension(plan.Track.Path).Equals(".dff", StringComparison.OrdinalIgnoreCase))
            {
                await DffMetadata.SaveAsync(plan.Track.Path, new(
                    plan.Title, plan.Album, plan.Artist, plan.AlbumArtist,
                    plan.TrackNumber, perDiscTrackTotals[plan.DiscNumber],
                    plan.DiscNumber, Math.Max(1u, discTotal), plan.Year,
                    plan.Genre, plan.Composer, preparedCover?.JpegBytes), token);
            }
            else
            {
                using var file = TagFile.Create(plan.Track.Path);
                file.Tag.Title = plan.Title;
                file.Tag.Album = plan.Album;
                file.Tag.Performers = Values(plan.Artist);
                file.Tag.AlbumArtists = Values(plan.AlbumArtist);
                file.Tag.Track = plan.TrackNumber;
                file.Tag.TrackCount = perDiscTrackTotals[plan.DiscNumber];
                file.Tag.Disc = plan.DiscNumber;
                file.Tag.DiscCount = Math.Max(1u, discTotal);
                file.Tag.Year = plan.Year;
                file.Tag.Genres = Values(plan.Genre);
                if (plan.Composer is not null) file.Tag.Composers = [plan.Composer];
                if (preparedCover is not null)
                    file.Tag.Pictures = [InMemoryArtworkService.CreatePicture(preparedCover)];
                file.Save();
            }
            var after = await TrackAudioPayload.Sha256Async(plan.Track.Path, token);
            if (!after.Equals(plan.Track.AudioPayloadBefore, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The native {formatLabel} audio payload changed while repairing '{plan.Track.RelativePath}'. The original remains untouched.");
            plan.AudioPayloadAfter = after;
        }

        var missing = MissingFields(plans, preparedCover is not null);
        var evidenceList = new List<string>
        {
            $"existing embedded {formatLabel} tags (highest metadata priority)",
            "track filenames (fallback track/title evidence)",
            $"album folder: {scan.AlbumName}"
        };
        if (trackDerivedAlbumArtist is not null)
            evidenceList.Add(trackDerivedAlbumArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)
                ? "distinct per-track artists established a Various Artists compilation identity"
                : $"common leading performer across every track established album artist: {trackDerivedAlbumArtist}");
        if (tracks.Any(track => track.Evidence.EmbeddedArtwork is not null))
            evidenceList.Add("existing embedded track artwork (highest artwork priority)");
        evidenceList.AddRange(external?.Sources ?? []);
        if (plans.Any(plan => plan.ComposerSource == "musicbrainz_work_relationship"))
            evidenceList.Add("exact MusicBrainz recording-to-work composer relationships");
        if (plans.Any(plan => plan.ComposerSource is "discogs_linked_tracklist_credit" or "discogs_exact_release_tracklist_credit"))
            evidenceList.Add("verified Discogs release tracklist composer credits aligned to the complete ordered local track list");
        if (plans.Any(plan => plan.ComposerSource == "merged_aligned_external_track_credits"))
            evidenceList.Add("aligned Discogs and MusicBrainz track credits merged by track position, with only missing values filled from the corroborating catalog");
        if (plans.Any(plan => plan.ArtistSource is "discogs_linked_track_artist_credit" or "discogs_exact_release_track_artist_credit"))
            evidenceList.Add("verified Discogs per-track artist credits aligned to the complete ordered local track list; ALBUMARTIST remains Various Artists for the compilation");
        if (plans.Any(plan => plan.ComposerSource == "reviewed_exact_release_track_credit"))
        {
            evidenceList.AddRange(ReviewedComposerSources(album, scan.AlbumName));
        }
        if (plans.Any(plan => plan.ComposerSource == "corroborated_album_identity"))
            evidenceList.Add("reviewed classical composer names corroborated by album, filename, and work-title evidence");
        if (plans.Any(plan => plan.ComposerSource == "recognized_composer_in_track_filename"))
            evidenceList.Add("recognized classical composer surnames in individual track filenames");
        if (deduplication.Duplicates.Count > 0)
            evidenceList.Add($"{deduplication.Duplicates.Count} byte-identical duplicate {formatLabel} filename entr{(deduplication.Duplicates.Count == 1 ? "y" : "ies")} collapsed by full-file SHA-256 and matching disc/track evidence");
        if (reviewedYear is not null && existingYear is null && externalYear is null && FindYear(scan.AlbumName) is null)
            evidenceList.Add("reviewed exact release: Mariinsky MAR0509 (February 2012), https://www.mariinsky.ru/en/news1/2012/01/16_229january/");
        if (preparedCover is not null) evidenceList.Add(preparedCover.Source);
        evidenceList = evidenceList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        await WriteReportAsync(scan, staged, plans, preparedCover, genreResolution,
            external, deduplication.Duplicates, missing, evidenceList, warnings, reportPath, token);
        await WriteGapManifestAsync(staged.JobDirectory, missing, evidenceList, token);
        progress.Report(Snapshot(JobPhase.Tagging, missing.Count == 0 ? 46 : 44,
            missing.Count == 0
                ? $"Track repair completed locally; exact native-{formatLabel} audio payload hashes are unchanged."
                : $"Track repair evidence remains incomplete: {string.Join(", ", missing)}."));
        return new(plans.Count, reportPath, new(true, missing.Count > 0, missing, evidenceList)
        {
            LookupWarnings = warnings
        });
    }

    private async Task<PreparedArtwork?> PrepareCoverAsync(
        IReadOnlyList<RepairTrack> tracks,
        ScanResult scan,
        StagedJob staged,
        ExternalAlbumMetadata? external,
        ICollection<string> warnings,
        CancellationToken token)
    {
        foreach (var track in tracks)
        {
            if (track.Evidence.EmbeddedArtwork is not { } embedded) continue;
            try
            {
                return await _artwork.PrepareDownloadedAsync(embedded, staged.FfmpegPath, staged.FfprobePath, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or JsonException)
            {
                warnings.Add($"Embedded artwork in '{track.RelativePath}' was unusable ({error.GetType().Name}); lower-priority artwork was considered.");
            }
        }

        var artworkMode = tracks.Any(track =>
                Path.GetExtension(track.Path).Equals(".dsf", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(track.Path).Equals(".dff", StringComparison.OrdinalIgnoreCase))
            ? ArtworkSelectionMode.Dsd
            : ArtworkSelectionMode.Flac;
        var local = await _artwork.PrepareLocalAsync(staged.InputAlbumRoot, staged.FfmpegPath, staged.FfprobePath,
            artworkMode, token);
        if (local.Artwork is not null) return local.Artwork;
        if (local.Issue is not null) warnings.Add(local.Issue);

        if (SiblingSacdAreaRoot(scan.AlbumRoot) is { } siblingAreaRoot)
        {
            foreach (var siblingTrack in Directory.EnumerateFiles(siblingAreaRoot, "*.dsf", SearchOption.TopDirectoryOnly)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    using var file = TagFile.Create(siblingTrack);
                    var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
                                  ?? file.Tag.Pictures.FirstOrDefault();
                    if (picture is null || picture.Data.Count == 0) continue;
                    return await _artwork.PrepareDownloadedAsync(
                        new DownloadedArtwork(picture.Data.Data.ToArray(), Nonempty(picture.MimeType) ?? "image/jpeg",
                            $"embedded artwork from sibling SACD area: {JsonPath(Path.Combine(Path.GetFileName(siblingAreaRoot), Path.GetFileName(siblingTrack)))}"),
                        staged.FfmpegPath, staged.FfprobePath, token);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                              TagLib.CorruptFileException or InvalidDataException or InvalidOperationException)
                {
                    warnings.Add($"Sibling SACD artwork in '{Path.GetFileName(siblingTrack)}' was unusable ({error.GetType().Name}); lower-priority artwork was considered.");
                }
            }
        }

        if (ParentVolumeArtworkRoot(scan.AlbumRoot) is { } parentArtworkRoot)
        {
            var parent = await _artwork.PrepareLocalAsync(parentArtworkRoot, staged.FfmpegPath, staged.FfprobePath,
                artworkMode, token);
            if (parent.Artwork is not null) return parent.Artwork;
            if (parent.Issue is not null) warnings.Add($"Parent-volume artwork lookup: {parent.Issue}");
        }

        var discogsCover = await _artwork.PrepareExternalUrlAsync(
            _externalMetadata, external?.ArtworkUrl, staged.FfmpegPath, staged.FfprobePath, token);
        if (discogsCover.Artwork is not null) return discogsCover.Artwork;
        if (discogsCover.Issue is not null && external?.ArtworkUrl is not null) warnings.Add(discogsCover.Issue);

        var externalCover = await _artwork.PrepareExternalAsync(
            _externalMetadata, external?.MusicBrainzReleaseId, staged.FfmpegPath, staged.FfprobePath, token);
        if (externalCover.Artwork is not null) return externalCover.Artwork;
        if (externalCover.Issue is not null) warnings.Add(externalCover.Issue);
        return null;
    }

    internal static string? ParentVolumeArtworkRoot(string albumRoot)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(albumRoot));
        var numberedVolume = Regex.IsMatch(name, @"^(?:volume|vol\.?|disc|disk|cd)\s*0*\d+\b", RegexOptions.IgnoreCase);
        var sacdArea = Regex.IsMatch(name, @"(?:^|[\s_-])(?:stereo|multi(?:channel|[\s_-]?ch))$", RegexOptions.IgnoreCase);
        if (!numberedVolume && !sacdArea)
            return null;
        var parent = Directory.GetParent(Path.GetFullPath(albumRoot))?.FullName;
        if (parent is null || !Directory.Exists(parent)) return null;
        if (!sacdArea) return parent;

        var artworkRoot = Path.Combine(parent, "Artwork");
        return Directory.Exists(artworkRoot) && Directory.EnumerateFiles(artworkRoot, "*", SearchOption.AllDirectories)
            .Any(path => new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            ? parent
            : null;
    }

    internal static string? SiblingSacdAreaRoot(string albumRoot)
    {
        var fullRoot = Path.GetFullPath(albumRoot);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullRoot));
        var area = Regex.Match(name, @"(?:^|[\s_-])(?:stereo|multi(?:channel|[\s_-]?ch))$", RegexOptions.IgnoreCase);
        if (!area.Success) return null;
        var baseName = name[..area.Index].TrimEnd(' ', '_', '-');
        if (baseName.Length == 0) return null;
        var parent = Directory.GetParent(fullRoot)?.FullName;
        if (parent is null) return null;
        var sibling = Path.Combine(parent, baseName);
        return Directory.Exists(sibling) && !sibling.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
            ? sibling
            : null;
    }

    private static TrackEvidence ReadEvidence(string path, string relativePath)
    {
        if (Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase))
        {
            var dff = DffMetadata.Read(path);
            if (dff.SampleRate <= 0 || dff.Channels <= 0)
                throw new InvalidDataException($"The existing DFF track has no readable DSD audio properties: {relativePath}");
            var parsedDff = ParseFileName(Path.GetFileNameWithoutExtension(relativePath));
            var embeddedDff = dff.Picture is not { Length: > 0 }
                ? null
                : new DownloadedArtwork(dff.Picture, Nonempty(dff.PictureMimeType) ?? "image/jpeg",
                    $"existing embedded artwork: {relativePath}");
            return new(
                Nonempty(dff.Title), Nonempty(dff.Album), Nonempty(dff.Artist), Nonempty(dff.AlbumArtist),
                dff.Track, dff.TrackCount, dff.Disc, dff.DiscCount, dff.Year,
                Nonempty(dff.Genre), Nonempty(dff.Composer), embeddedDff, parsedDff.Number, parsedDff.Title);
        }
        using var file = TagFile.Create(path);
        if (!file.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio) || file.Properties.AudioSampleRate <= 0)
            throw new InvalidDataException($"The existing track is not readable audio: {relativePath}");
        var parsed = ParseFileName(Path.GetFileNameWithoutExtension(relativePath));
        var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
            ?? file.Tag.Pictures.FirstOrDefault();
        var embedded = picture is null || picture.Data.Count == 0
            ? null
            : new DownloadedArtwork(picture.Data.Data.ToArray(), Nonempty(picture.MimeType) ?? "image/jpeg",
                $"existing embedded artwork: {relativePath}");
        return new(
            Nonempty(file.Tag.Title), Nonempty(file.Tag.Album), First(file.Tag.Performers), First(file.Tag.AlbumArtists),
            file.Tag.Track, file.Tag.TrackCount, file.Tag.Disc, file.Tag.DiscCount, file.Tag.Year,
            First(file.Tag.Genres), First(file.Tag.Composers), embedded, parsed.Number, parsed.Title);
    }

    private static async Task<string> FullFileSha256Async(string path, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static DeduplicationResult CollapseExactDuplicates(IReadOnlyList<RepairTrack> tracks)
    {
        var retained = new List<RepairTrack>(tracks.Count);
        var duplicates = new List<DeduplicatedTrack>();
        foreach (var hashGroup in tracks.GroupBy(track => track.FullFileSha256, StringComparer.OrdinalIgnoreCase))
        {
            var members = hashGroup.OrderBy(track => track.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            if (members.Length == 1)
            {
                retained.Add(members[0]);
                continue;
            }

            var coordinates = members.Select(track => (
                    DiscNumber: track.Evidence.DiscNumber > 0
                        ? track.Evidence.DiscNumber
                        : InferDiscNumber(track.RelativePath) ?? 1u,
                    TaggedTrackNumber: track.Evidence.TrackNumber > 0 ? (uint?)track.Evidence.TrackNumber : null,
                    track.Evidence.FileTrackNumber))
                .Distinct()
                .ToArray();
            var coordinate = coordinates.Length == 1 ? coordinates[0] : default;
            if (coordinates.Length != 1 || coordinate.TaggedTrackNumber is null && coordinate.FileTrackNumber is null)
            {
                retained.AddRange(members);
                continue;
            }

            var keeper = members[0];
            retained.Add(keeper);
            duplicates.AddRange(members.Skip(1).Select(duplicate => new DeduplicatedTrack(
                duplicate.RelativePath, keeper.RelativePath, hashGroup.Key)));
        }
        return new(retained, duplicates);
    }

    private static IReadOnlyList<string> MissingFields(IReadOnlyList<RepairPlan> plans, bool hasCover)
    {
        var missing = new List<string>();
        if (plans.Any(plan => plan.Title is null)) missing.Add("TITLE");
        if (plans.Any(plan => plan.Album is null)) missing.Add("ALBUM");
        if (plans.Any(plan => plan.Artist is null)) missing.Add("ARTIST");
        if (plans.Any(plan => plan.AlbumArtist is null)) missing.Add("ALBUMARTIST");
        if (plans.Any(plan => plan.TrackNumber == 0)) missing.Add("TRACKNUMBER");
        if (plans.Any(plan => plan.DiscNumber == 0)) missing.Add("DISCNUMBER");
        if (plans.Any(plan => plan.Year == 0)) missing.Add("DATE");
        if (plans.Any(plan => plan.Genre is null)) missing.Add("GENRE");
        if (plans.Any(plan => ClassicalMetadataPolicy.RequiresComposer(
                plan.Genre, plan.Title, ClassicalMetadataPolicy.IsCompilationArtist(plan.AlbumArtist)) &&
            plan.Composer is null))
            missing.Add("COMPOSER");
        if (!hasCover) missing.Add("COVER");
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task WriteReportAsync(
        ScanResult scan,
        StagedJob staged,
        IReadOnlyList<RepairPlan> plans,
        PreparedArtwork? cover,
        ResolvedValue genre,
        ExternalAlbumMetadata? external,
        IReadOnlyList<DeduplicatedTrack> duplicates,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> evidence,
        IReadOnlyList<string> warnings,
        string reportPath,
        CancellationToken token)
    {
        var extension = Path.GetExtension(plans[0].Track.Path).ToLowerInvariant();
        var format = extension.TrimStart('.');
        var formatLabel = format.ToUpperInvariant();
        var sourceKind = $"Existing {formatLabel}";
        var discs = new JsonArray();
        foreach (var discGroup in plans.GroupBy(plan => plan.DiscNumber).OrderBy(group => group.Key))
        {
            var ordered = discGroup.OrderBy(plan => plan.TrackNumber).ThenBy(plan => plan.Track.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray();
            var trackNodes = ordered.Select(plan => (JsonNode)new JsonObject
            {
                ["disc"] = plan.DiscNumber,
                ["track"] = plan.TrackNumber,
                ["title"] = plan.Title,
                ["title_source"] = plan.TitleSource,
                ["artist"] = plan.Artist,
                ["artist_source"] = plan.ArtistSource,
                ["album_artist"] = plan.AlbumArtist,
                ["composer"] = plan.Composer,
                ["composer_source"] = plan.ComposerSource,
                ["file"] = JsonPath(plan.Track.RelativePath),
                ["audio_payload_sha256_before"] = plan.Track.AudioPayloadBefore,
                ["audio_payload_sha256_after"] = plan.AudioPayloadAfter
            }).ToArray();
            discs.Add(new JsonObject
            {
                ["disc"] = discGroup.Key,
                ["source"] = JsonPath(ordered[0].Track.RelativePath),
                ["tracks"] = new JsonArray(trackNodes)
            });
        }

        var album = Consensus(plans.Select(plan => plan.Album));
        var artist = Consensus(plans.Select(plan => plan.AlbumArtist));
        var report = new JsonObject
        {
            ["schema_version"] = "2.0",
            ["album"] = album ?? string.Empty,
            ["artist"] = artist ?? string.Empty,
            ["edition"] = scan.AlbumName,
            ["format"] = format,
            ["source_type"] = $"existing_{format}_tracks",
            ["workflow_mode"] = "existing_track_repair",
            ["generated_by"] = "Album Fixer deterministic existing-track repair processor",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["metadata_sources"] = new JsonArray(evidence.Select(value => JsonValue.Create(value)).ToArray()),
            ["sources"] = new JsonArray(scan.Media
                .Where(item => item.Kind.Equals(sourceKind, StringComparison.OrdinalIgnoreCase) ||
                               format is "dsf" or "dff" && item.Kind == "SACD / DSD image")
                .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                .Select(item => (JsonNode)new JsonObject
            {
                ["path"] = JsonPath(item.RelativePath),
                ["type"] = item.Kind == "SACD / DSD image" ? "Retained SACD / DSD image" : $"Existing {formatLabel} track",
                ["size"] = item.Size
            }).ToArray()),
            ["deduplicated_tracks"] = new JsonArray(duplicates.Select(duplicate => (JsonNode)new JsonObject
            {
                ["removed_file"] = JsonPath(duplicate.RemovedRelativePath),
                ["retained_file"] = JsonPath(duplicate.RetainedRelativePath),
                ["source_file_sha256"] = duplicate.FullFileSha256,
                ["rationale"] = "Files were byte-identical and carried the same disc/track coordinate; one filename entry is sufficient."
            }).ToArray()),
            ["discs"] = discs,
            ["verification"] = new JsonObject
            {
                ["status"] = missing.Count == 0 ? "pending" : "awaiting_metadata",
                ["method"] = format == "flac"
                    ? "Exact SHA-256 equality of each compressed FLAC audio-frame payload before and after tag/art repair; final-path verification pending."
                    : $"Exact SHA-256 equality of each native {formatLabel} DSD data-chunk payload before and after tag/art repair; final-path verification pending.",
                ["audio_payload_equivalence"] = "passed_locally",
                ["sources_deleted"] = false,
                ["errors"] = new JsonArray()
            },
            ["metadata_lookup"] = new JsonObject
            {
                ["implementation"] = "deterministic_local_code",
                ["priority"] = new JsonArray("existing_tags_and_embedded_artwork", "external_exact_album_match", "corroborated_album_identity_fallback", "track_filename_fallback"),
                ["status"] = missing.Count == 0 ? "complete" : "partial",
                ["warnings"] = new JsonArray(warnings.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray())
            },
            ["job"] = new JsonObject
            {
                ["identifier"] = Path.GetFileName(staged.JobDirectory),
                ["local_staging_used"] = true,
                ["source_cache_used"] = staged.SourceCacheUsed,
                ["processor"] = "local_existing_track_tag_and_art_repair"
            }
        };
        if (genre.Value is not null)
        {
            report["genre"] = new JsonObject
            {
                ["value"] = genre.Value,
                ["source_type"] = genre.SourceType,
                ["confidence"] = genre.Confidence,
                ["rationale"] = genre.Rationale
            };
        }
        var composers = plans.Select(plan => Nonempty(plan.Composer)).Where(value => value is not null)
            .Select(value => value!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
        if (composers.Length == 1)
        {
            var composer = composers[0];
            var source = Unanimous(plans.Select(plan => plan.ComposerSource)) ?? "mixed_local_evidence";
            report["composer"] = new JsonObject
            {
                ["value"] = composer,
                ["source_type"] = source,
                ["confidence"] = "high",
                ["rationale"] = source == "existing_track_tag"
                    ? "Existing nonempty track composer tags have priority."
                    : source == "musicbrainz_work_relationship"
                        ? "The exact matched release identifies the same composer through each recording-to-work relationship."
                        : "Conservative local identity evidence resolved the same composer for every track."
            };
        }
        else if (composers.Length > 1)
        {
            report["composer"] = new JsonObject
            {
                ["status"] = "track_specific",
                ["values"] = new JsonArray(composers.Select(value => JsonValue.Create(value)).ToArray()),
                ["confidence"] = "high",
                ["rationale"] = "This compilation contains multiple composers; each value is attached to its matching track instead of inventing one album-level composer."
            };
        }
        if (cover is not null) report["cover"] = cover.ToReport();
        else report["artwork"] = new JsonObject { ["status"] = "incomplete", ["reason"] = "No usable embedded, local, or external front cover was available." };
        if (missing.Count > 0)
            ((JsonObject)report["verification"]!)["missing_metadata"] = new JsonArray(missing.Select(value => JsonValue.Create(value)).ToArray());
        await AtomicWriteAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static async Task WriteGapManifestAsync(
        string jobDirectory,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> evidence,
        CancellationToken token)
    {
        var root = new JsonObject
        {
            ["split_completed"] = true,
            ["requires_research"] = missing.Count > 0,
            ["missing_fields"] = new JsonArray(missing.Select(value => JsonValue.Create(value)).ToArray()),
            ["local_evidence"] = new JsonArray(evidence.Select(value => JsonValue.Create(value)).ToArray()),
            ["handled_by"] = "deterministic_existing_track_repair"
        };
        await AtomicWriteAsync(MetadataGapService.GetPath(jobDirectory), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static async Task AtomicWriteAsync(string path, string text, CancellationToken token)
    {
        var temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false), token);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static (string? Artist, string? Album) ParseFolderIdentity(string folderName)
    {
        var cleaned = Regex.Replace(folderName, "\\s*\\[[^\\]]+\\]\\s*", " ").Trim();
        cleaned = Regex.Replace(cleaned, "\\s+", " ");
        var separator = cleaned.IndexOf(" - ", StringComparison.Ordinal);
        if (separator >= 1 && separator + 3 < cleaned.Length)
        {
            var artist = Nonempty(cleaned[..separator]);
            var album = Regex.Replace(cleaned[(separator + 3)..], "^(?:HD|HI[- ]?RES(?:OLUTION)?|24[- ]?BIT)\\s+", string.Empty,
                RegexOptions.IgnoreCase).Trim();
            return new(artist, Nonempty(album));
        }
        return new(null, Nonempty(cleaned));
    }

    internal static string? ResolveTrackAlbumArtist(IEnumerable<string?> trackArtists)
    {
        var artists = trackArtists.Select(Nonempty).Where(value => value is not null).Select(value => value!).ToArray();
        if (artists.Length == 0) return null;

        var leadingArtists = artists.Select(LeadingArtist).ToArray();
        if (leadingArtists.All(value => IdentityKey(value).Equals(IdentityKey(leadingArtists[0]), StringComparison.Ordinal)))
            return leadingArtists[0];

        return artists.Length >= 3 && artists.Select(IdentityKey).Distinct(StringComparer.Ordinal).Count() >= 3
            ? "Various Artists"
            : null;
    }

    private static string LeadingArtist(string value)
    {
        var separator = Regex.Match(value, "\\s*(?:,|;|\\bfeat(?:uring)?\\.?\\s+)\\s*", RegexOptions.IgnoreCase);
        return separator.Success ? value[..separator.Index].Trim() : value.Trim();
    }

    internal static string? InferKnownComposerFromFileTitle(string fileTitle)
    {
        var match = Regex.Match(fileTitle, "\\((?<surname>[\\p{L}\\p{M}'’.-]{4,})\\b", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return KnownClassicalComposers.GetValueOrDefault(IdentityKey(match.Groups["surname"].Value));
    }

    internal static bool IsKnownClassicalComposerCredit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var identity = IdentityKey(value);
        return KnownClassicalComposers.Keys.Any(identity.Contains);
    }

    internal static IReadOnlyList<string?> InferReviewedTrackComposers(
        string? album,
        string albumFolderName,
        IReadOnlyList<(string? Title, string FileTitle)> tracks)
    {
        var unresolved = Enumerable.Repeat<string?>(null, tracks.Count).ToArray();
        var albumIdentity = IdentityKey(album ?? string.Empty);
        var folderIdentity = IdentityKey(albumFolderName);
        if (albumIdentity is "opus3dsdshowcase1" or "opus3dsdshowcase01" &&
            folderIdentity is "opus3dsdshowcase1" or "opus3dsdshowcase01" &&
            ReviewedTitlesMatch(tracks, Opus3DsdShowcase1Credits))
            return Opus3DsdShowcase1Credits.Select(credit => credit.Composer).ToArray();

        if (albumIdentity.Contains("superaudiosurroundcollectionvol3", StringComparison.Ordinal) &&
            folderIdentity.Contains("superaudiocollectionvolume3", StringComparison.Ordinal) &&
            ReviewedTitlesMatch(tracks, LinnSuperAudioCollectionVolume3Credits))
            return LinnSuperAudioCollectionVolume3Credits.Select(credit => credit.Composer).ToArray();

        if (albumIdentity.Contains("superaudiosoundcollectionvol4", StringComparison.Ordinal) &&
            folderIdentity.Contains("superaudiocollectionvolume4", StringComparison.Ordinal) &&
            ReviewedTitlesMatch(tracks, LinnSuperAudioCollectionVolume4Credits))
            return LinnSuperAudioCollectionVolume4Credits.Select(credit => credit.Composer).ToArray();

        if (albumIdentity.Contains("24bitsofchristmas2014", StringComparison.Ordinal) &&
            folderIdentity.Contains("xmasgifts2014", StringComparison.Ordinal) &&
            ReviewedTitlesMatch(tracks, LinnXmasGifts2014Credits))
            return LinnXmasGifts2014Credits.Select(credit => credit.Composer).ToArray();

        return unresolved;
    }

    private static bool ReviewedTitlesMatch(
        IReadOnlyList<(string? Title, string FileTitle)> tracks,
        IReadOnlyList<(string Title, string? Composer)> credits) =>
        tracks.Count == credits.Count && tracks.Select(ReviewedCreditTitleIdentity)
            .SequenceEqual(credits.Select(credit => IdentityKey(credit.Title)), StringComparer.Ordinal);

    private static IReadOnlyList<string> ReviewedComposerSources(string? album, string albumFolderName)
    {
        var albumIdentity = IdentityKey(album ?? string.Empty);
        var folderIdentity = IdentityKey(albumFolderName);
        if (albumIdentity is "opus3dsdshowcase1" or "opus3dsdshowcase01" &&
            folderIdentity is "opus3dsdshowcase1" or "opus3dsdshowcase01")
            return
            [
                "reviewed exact compilation identity and selections: Opus 3 DSD Showcase 1, https://positive-feedback.com/Issue63/opus3.htm",
                "reviewed source-work credits: https://musicbrainz.org/work/ad3199e2-5395-3254-842f-dde2db33efca, https://musicbrainz.org/work/385ad09b-45dd-3a33-9c01-9a6b7e307b28, https://www.allmusic.com/album/tiny-island-mw0000667838, https://www.opus3records.com/artists/blues/19603.htm"
            ];
        if (albumIdentity.Contains("superaudiosurroundcollectionvol3", StringComparison.Ordinal) &&
            folderIdentity.Contains("superaudiocollectionvolume3", StringComparison.Ordinal))
            return
            [
                "reviewed exact Linn AKP 305 track sequence and work credits: https://tower.jp/item/2332589"
            ];
        if (albumIdentity.Contains("superaudiosoundcollectionvol4", StringComparison.Ordinal) &&
            folderIdentity.Contains("superaudiocollectionvolume4", StringComparison.Ordinal))
            return
            [
                "reviewed Diego Ortiz work credit for Pamela Thorby's exact Recercada selection: https://www.prestomusic.com/classical/products/7964859--garden-of-early-delights"
            ];
        if (albumIdentity.Contains("24bitsofchristmas2014", StringComparison.Ordinal) &&
            folderIdentity.Contains("xmasgifts2014", StringComparison.Ordinal))
            return
            [
                "reviewed exact 25-track Linn 24-bits of Christmas 2014 sequence and source-album identities: https://pro-jazz.com/audio/tr24-of-va-linn-records-xmas-gifts-2014-2014-jazz-classic-101289.html",
                "reviewed source-work credits: https://www.chandos.net/products/catalogue/LN%200420, https://www.chandos.net/products/catalogue/LN%200434"
            ];
        return [];
    }

    private static string ReviewedCreditTitleIdentity((string? Title, string FileTitle) track)
    {
        var title = Nonempty(track.Title) ?? track.FileTitle;
        var numbered = Regex.Match(title, @"^\s*\d{1,3}\s*[-._]+\s*(?<title>.+?)\s*$",
            RegexOptions.CultureInvariant);
        return IdentityKey(numbered.Success ? numbered.Groups["title"].Value : title);
    }

    internal static IReadOnlyList<string?> InferClassicalTrackComposers(
        string? album,
        string albumFolderName,
        string? genre,
        IReadOnlyList<(string? Title, string FileTitle)> tracks,
        bool isCompilation = false)
    {
        var unresolved = Enumerable.Repeat<string?>(null, tracks.Count).ToArray();
        if (genre?.Contains("classical", StringComparison.OrdinalIgnoreCase) != true &&
            genre?.Contains("opera", StringComparison.OrdinalIgnoreCase) != true)
            return unresolved;

        var albumValue = Nonempty(album);
        var albumComposers = KnownComposersInText(albumValue).ToArray();
        var folderComposers = KnownComposersInText(albumFolderName).ToArray();
        for (var index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            if (!ClassicalMetadataPolicy.RequiresComposer(genre, track.Title ?? track.FileTitle, isCompilation)) continue;
            var explicitComposers = KnownComposersInText(track.FileTitle).ToArray();
            if (explicitComposers.Length == 1)
            {
                unresolved[index] = explicitComposers[0];
                continue;
            }

            var workComposer = albumValue is null
                ? null
                : InferComposerFromAlbumWork(albumValue, $"{track.Title} {track.FileTitle}", albumComposers);
            if (workComposer is not null)
            {
                unresolved[index] = workComposer;
                continue;
            }

            var catalogComposer = InferComposerFromCatalogEvidence($"{track.Title} {track.FileTitle}", albumComposers);
            if (catalogComposer is not null)
            {
                unresolved[index] = catalogComposer;
                continue;
            }

            if (albumComposers.Length == 1)
                unresolved[index] = albumComposers[0];
            else if (albumComposers.Length == 0 && folderComposers.Length == 1)
                unresolved[index] = folderComposers[0];
        }


        if (albumComposers.Length == 2)
        {
            var identified = unresolved.Where(value => value is not null).Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (identified.Length == 1)
            {
                var remainingComposer = albumComposers
                    .SingleOrDefault(value => !value.Equals(identified[0], StringComparison.OrdinalIgnoreCase));
                if (remainingComposer is not null)
                    for (var index = 0; index < tracks.Count; index++)
                        if (unresolved[index] is null &&
                            ClassicalMetadataPolicy.RequiresComposer(genre, tracks[index].Title ?? tracks[index].FileTitle, isCompilation))
                            unresolved[index] = remainingComposer;
            }
        }
        return unresolved;
    }

    private static string? InferComposerFromCatalogEvidence(string track, IReadOnlyCollection<string> albumComposers)
    {
        if (albumComposers.Contains("Wolfgang Amadeus Mozart", StringComparer.OrdinalIgnoreCase) &&
            Regex.IsMatch(track, "\\bK[.\\s-]?\\d{2,4}\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "Wolfgang Amadeus Mozart";
        return null;
    }

    internal static int? InferReviewedReleaseYear(string? album, string folderName)
    {
        var albumIdentity = album is null ? string.Empty : IdentityKey(album);
        var folderIdentity = IdentityKey(folderName);
        return albumIdentity.Equals("mar0509shostakovichshchedrinpianoconcertos", StringComparison.Ordinal) &&
               folderIdentity.Contains("shostakovichshchedrinpianoconcertos", StringComparison.Ordinal)
            ? 2012
            : null;
    }

    private static IEnumerable<string> KnownComposersInText(string? value)
    {
        if (Nonempty(value) is not { } text) return [];
        var identity = IdentityKey(text);
        return KnownClassicalComposers
            .Select(pair => new { pair.Key, Composer = pair.Value, Index = identity.IndexOf(pair.Key, StringComparison.Ordinal) })
            .Where(match => match.Index >= 0)
            .OrderBy(match => match.Index)
            .ThenByDescending(match => match.Key.Length)
            .Select(match => match.Composer)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? InferComposerFromAlbumWork(
        string album,
        string track,
        IReadOnlyCollection<string> albumComposers)
    {
        if (albumComposers.Count < 2) return null;
        var albumIdentity = IdentityKey(album);
        var trackIdentity = IdentityKey(track);
        var markers = KnownClassicalComposers
            .Where(pair => albumComposers.Contains(pair.Value, StringComparer.OrdinalIgnoreCase))
            .Select(pair => new { pair.Key, Composer = pair.Value, Index = albumIdentity.IndexOf(pair.Key, StringComparison.Ordinal) })
            .Where(marker => marker.Index >= 0)
            .OrderBy(marker => marker.Index)
            .ThenByDescending(marker => marker.Key.Length)
            .GroupBy(marker => marker.Composer, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(marker => marker.Index)
            .ToArray();
        var matches = new List<string>();
        for (var index = 0; index < markers.Length; index++)
        {
            var marker = markers[index];
            var start = marker.Index + marker.Key.Length;
            var end = index + 1 < markers.Length ? markers[index + 1].Index : albumIdentity.Length;
            if (end <= start) continue;
            var workIdentity = albumIdentity[start..end];
            if (workIdentity.Length >= 4 && trackIdentity.Contains(workIdentity, StringComparison.Ordinal))
                matches.Add(marker.Composer);
        }
        return matches.Distinct(StringComparer.OrdinalIgnoreCase).ToArray() is [var composer] ? composer : null;
    }

    internal static string? InferGenreFromTrackEvidence(IEnumerable<(string? Title, string FileTitle)> tracks)
    {
        var evidence = tracks.ToArray();
        if (evidence.Length == 0) return null;
        var knownComposerCount = evidence.Count(track => InferKnownComposerFromFileTitle(track.FileTitle) is not null);
        if (knownComposerCount < Math.Max(2, (evidence.Length * 3 + 3) / 4)) return null;
        var actCount = evidence.Count(track => Regex.IsMatch(track.Title ?? string.Empty, "\\bAct\\s*\\d+\\b", RegexOptions.IgnoreCase));
        return actCount >= Math.Max(2, evidence.Length / 2) ? "Opera" : "Classical";
    }

    private static ResolvedValue ResolveGenre(
        string? existing,
        ExternalAlbumMetadata? external,
        string? libraryFolder,
        string? trackContent,
        bool isMultiArtistCompilation)
    {
        if (Nonempty(existing) is { } existingValue)
            return new(existingValue, "existing_track_tag", "high",
                "Existing nonempty track genre tags have priority over external and filename fallback evidence.");
        if (Nonempty(external?.Genre) is { } externalValue)
            return new(externalValue, external?.GenreSourceType ?? "external_album_metadata", external?.GenreConfidence ?? "medium",
                external?.GenreRationale ?? "Broad genre resolved from exact external album metadata.");
        if (Nonempty(libraryFolder) is { } folderValue)
            return new(folderValue, "recognized_library_genre_folder", "high",
                "A recognized library category supplied genre only; it was not used as artist metadata.");
        if (Nonempty(trackContent) is { } contentValue)
            return new(contentValue, "corroborated_track_content", "high",
                "At least three quarters of the filenames name recognized classical composers, and the tagged titles distinguish opera excerpts from other classical works.");
        if (isMultiArtistCompilation)
            return new("Compilation", "multi_artist_compilation", "high",
                "Distinct artists across the track list establish a mixed compilation; no unsupported single musical genre was invented.");
        return new(null, null, null, null);
    }

    internal static string? InferClassicalComposer(
        string? album,
        string albumFolderName,
        string? genre,
        IEnumerable<string?> performingArtists)
    {
        if (genre?.Contains("classical", StringComparison.OrdinalIgnoreCase) != true &&
            genre?.Contains("opera", StringComparison.OrdinalIgnoreCase) != true ||
            Nonempty(album) is not { } albumValue)
            return null;

        var separator = albumValue.IndexOf(" - ", StringComparison.Ordinal);
        if (separator is < 2 or > 80) return null;
        var candidate = Nonempty(albumValue[..separator]);
        if (candidate is null || candidate.Any(char.IsDigit)) return null;
        var normalizedDisplay = Regex.Replace(candidate, "(?<=\\.)\\s*(?=\\p{L})", " ");

        var candidateIdentity = IdentityKey(normalizedDisplay);
        if (candidateIdentity.Length < 4 || performingArtists
                .Select(Nonempty)
                .Where(value => value is not null)
                .Any(value => IdentityKey(value!).Equals(candidateIdentity, StringComparison.Ordinal)))
            return null;

        var surname = normalizedDisplay.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (surname is null) return null;
        var surnameIdentity = IdentityKey(surname.Trim('.', ',', ';', ':'));
        return surnameIdentity.Length >= 4 && IdentityKey(albumFolderName).Contains(surnameIdentity, StringComparison.Ordinal)
            ? normalizedDisplay
            : null;
    }

    internal static (uint? Number, string Title) ParseFileName(string value)
    {
        var match = TrackFileName().Match(value);
        if (!match.Success) match = EmbeddedTrackFileName().Match(value);
        if (!match.Success) return (null, value.Trim());
        return (uint.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null,
            match.Groups["title"].Value.Trim());
    }

    private static IReadOnlyList<PositionedTrack> PositionTracks(IReadOnlyList<RepairTrack> tracks)
    {
        var candidates = tracks
            .Select(track => new
            {
                Track = track,
                DiscNumber = track.Evidence.DiscNumber > 0
                    ? track.Evidence.DiscNumber
                    : InferDiscNumber(track.RelativePath) ?? 1u,
                TaggedTrackNumber = track.Evidence.TrackNumber > 0 ? (uint?)track.Evidence.TrackNumber : null,
                track.Evidence.FileTrackNumber
            })
            .ToArray();

        var trackNumbers = ResolveTrackNumbers(candidates
            .Select(value => (value.DiscNumber, value.TaggedTrackNumber, value.FileTrackNumber, value.Track.RelativePath))
            .ToArray());
        return candidates
            .Select((value, index) => new PositionedTrack(value.Track, value.DiscNumber, trackNumbers[index]))
            .OrderBy(value => value.DiscNumber)
            .ThenBy(value => value.TrackNumber)
            .ThenBy(value => value.Track.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static uint[] ResolveTrackNumbers(
        IReadOnlyList<(uint DiscNumber, uint? TaggedTrackNumber, uint? FileTrackNumber, string SortKey)> evidence)
    {
        var resolved = evidence.Select(value => value.TaggedTrackNumber ?? value.FileTrackNumber).ToArray();

        // A complete 1..N filename sequence is stronger than internally contradictory tags. This is
        // deliberately all-or-nothing per disc so a few numbered filenames cannot silently reorder an album.
        foreach (var disc in Enumerable.Range(0, evidence.Count).GroupBy(index => evidence[index].DiscNumber))
        {
            var indexes = disc.ToArray();
            var filenameNumbers = indexes.Select(index => evidence[index].FileTrackNumber).ToArray();
            var hasDuplicateResolvedNumber = indexes
                .Where(index => resolved[index] is not null)
                .GroupBy(index => resolved[index]!.Value)
                .Any(group => group.Count() > 1);
            var completeFilenameSequence = filenameNumbers.All(value => value is not null) &&
                                           filenameNumbers.Select(value => value!.Value)
                                               .OrderBy(value => value)
                                               .SequenceEqual(Enumerable.Range(1, indexes.Length).Select(value => (uint)value));
            if (!hasDuplicateResolvedNumber || !completeFilenameSequence) continue;
            for (var index = 0; index < indexes.Length; index++)
                resolved[indexes[index]] = filenameNumbers[index];
        }

        var duplicateGroups = Enumerable.Range(0, evidence.Count)
            .Where(index => resolved[index] is not null)
            .GroupBy(index => (evidence[index].DiscNumber, TrackNumber: resolved[index]!.Value))
            .Where(group => group.Count() > 1)
            .ToArray();
        foreach (var duplicate in duplicateGroups)
        {
            var indexes = duplicate.ToArray();
            var filenameAnchors = indexes
                .Where(index => evidence[index].FileTrackNumber == duplicate.Key.TrackNumber)
                .ToArray();
            if (indexes.Length != 2 || filenameAnchors.Length != 1) continue;

            var displacedIndex = indexes.Single(index => index != filenameAnchors[0]);
            var filenameFallback = evidence[displacedIndex].FileTrackNumber;
            resolved[displacedIndex] = filenameFallback is not null &&
                                        filenameFallback != duplicate.Key.TrackNumber &&
                                        !Enumerable.Range(0, evidence.Count).Any(index =>
                                            index != displacedIndex &&
                                            evidence[index].DiscNumber == duplicate.Key.DiscNumber &&
                                            resolved[index] == filenameFallback)
                ? filenameFallback
                : null;
        }

        foreach (var disc in Enumerable.Range(0, evidence.Count)
                     .GroupBy(index => evidence[index].DiscNumber)
                     .OrderBy(group => group.Key))
        {
            var usedNumbers = disc.Where(index => resolved[index] is not null)
                .Select(index => resolved[index]!.Value)
                .ToHashSet();
            uint nextNumber = 1;
            foreach (var index in disc
                         .OrderBy(index => resolved[index] ?? uint.MaxValue)
                         .ThenBy(index => evidence[index].SortKey, StringComparer.OrdinalIgnoreCase))
            {
                if (resolved[index] is not null) continue;
                while (usedNumbers.Contains(nextNumber)) nextNumber++;
                resolved[index] = nextNumber;
                usedNumbers.Add(nextNumber);
                nextNumber++;
            }
        }

        return resolved.Select(value => value!.Value).ToArray();
    }

    private static uint? InferDiscNumber(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        foreach (var part in directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            var match = DiscDirectoryName().Match(part);
            if (match.Success && uint.TryParse(match.Groups["number"].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number) && number > 0)
                return number;
        }
        return null;
    }

    private static string? Consensus(IEnumerable<string?> values) => values
        .Select(Nonempty)
        .Where(value => value is not null)
        .Select(value => value!)
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Min(value => value), StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .FirstOrDefault();

    private static string? Unanimous(IEnumerable<string?> values)
    {
        var distinct = values.Select(Nonempty).Where(value => value is not null).Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static int? ConsensusNumber(IEnumerable<uint> values) => values
        .Where(value => value > 0)
        .GroupBy(value => value)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key)
        .Select(group => (int?)group.Key)
        .FirstOrDefault();

    private static string? First(IEnumerable<string> values) => values.Select(Nonempty).FirstOrDefault(value => value is not null);
    private static string[] Values(string? value) => Nonempty(value) is { } result ? [result] : [];
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsVariousArtists(string value) =>
        Regex.IsMatch(value.Trim(), @"^various(?:\s+artists?)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static string IdentityKey(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }
    private static int? FindYear(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, "(?:19|20)\\d{2}");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }
    private static string JsonPath(string path) => path.Replace('\\', '/');
    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) =>
        new(phase, percent, "running", detail, DateTimeOffset.UtcNow);

    private static readonly IReadOnlyDictionary<string, string> KnownClassicalComposers =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["bach"] = "Johann Sebastian Bach",
            ["balakirev"] = "Mily Balakirev",
            ["bellini"] = "Vincenzo Bellini",
            ["beethoven"] = "Ludwig van Beethoven",
            ["berlioz"] = "Hector Berlioz",
            ["brahms"] = "Johannes Brahms",
            ["britten"] = "Benjamin Britten",
            ["bizet"] = "Georges Bizet",
            ["catalani"] = "Alfredo Catalani",
            ["chopin"] = "Frédéric Chopin",
            ["corbetta"] = "Francesco Corbetta",
            ["corelli"] = "Arcangelo Corelli",
            ["debussy"] = "Claude Debussy",
            ["delius"] = "Frederick Delius",
            ["donizetti"] = "Gaetano Donizetti",
            ["dicapua"] = "Eduardo Di Capua",
            ["decurtis"] = "Ernesto De Curtis",
            ["curtis"] = "Ernesto De Curtis",
            ["denza"] = "Luigi Denza",
            ["dvorak"] = "Antonín Dvořák",
            ["elgar"] = "Edward Elgar",
            ["erskine"] = "Thomas Erskine",
            ["fasch"] = "Johann Friedrich Fasch",
            ["faure"] = "Gabriel Fauré",
            ["flotow"] = "Friedrich von Flotow",
            ["geminiani"] = "Francesco Geminiani",
            ["gluck"] = "Christoph Willibald Gluck",
            ["granados"] = "Enrique Granados",
            ["handel"] = "George Frideric Handel",
            ["haydn"] = "Joseph Haydn",
            ["johnward"] = "John Ward",
            ["giordani"] = "Tommaso Giordani",
            ["leoncavallo"] = "Ruggero Leoncavallo",
            ["liszt"] = "Franz Liszt",
            ["mahler"] = "Gustav Mahler",
            ["mazzucchi"] = "Alfredo Mazzucchi",
            ["mendelssohn"] = "Felix Mendelssohn",
            ["mozart"] = "Wolfgang Amadeus Mozart",
            ["neilsen"] = "Carl Nielsen",
            ["nielsen"] = "Carl Nielsen",
            ["orff"] = "Carl Orff",
            ["puccini"] = "Giacomo Puccini",
            ["rachmaninoff"] = "Sergei Rachmaninoff",
            ["rachmaninov"] = "Sergei Rachmaninoff",
            ["rossini"] = "Gioachino Rossini",
            ["schubert"] = "Franz Schubert",
            ["schumann"] = "Robert Schumann",
            ["scriabin"] = "Alexander Scriabin",
            ["shchedrin"] = "Rodion Shchedrin",
            ["shostakovich"] = "Dmitri Shostakovich",
            ["sibelius"] = "Jean Sibelius",
            ["strauss"] = "Richard Strauss",
            ["tchaikovsky"] = "Pyotr Ilyich Tchaikovsky",
            ["tosti"] = "Francesco Paolo Tosti",
            ["vaughanwilliams"] = "Ralph Vaughan Williams",
            ["walton"] = "William Walton",
            ["verdi"] = "Giuseppe Verdi",
            ["vieuxtemps"] = "Henri Vieuxtemps",
            ["vivaldi"] = "Antonio Vivaldi",
            ["wagner"] = "Richard Wagner",
            ["warlock"] = "Peter Warlock",
            ["butterworth"] = "George Butterworth"
        };

    private static readonly (string Title, string? Composer)[] Opus3DsdShowcase1Credits =
    [
        ("Here's that rainy day", "Jimmy Van Heusen"),
        ("Teach me Tonight", "Gene de Paul"),
        ("Black Beauty", "Duke Ellington"),
        ("Where the green grass grows", "Eric Bibb"),
        ("Vaquero", "Göran Wennerbrandt"),
        ("La Maja de Goya", "Enrique Granados"),
        ("Nun komm der Heiden Heiland", "Johann Sebastian Bach"),
        ("Scherzo from Symphony no.2 in D Major", "Ludwig van Beethoven"),
        ("Overture to Carmen", "Georges Bizet")
    ];

    private static readonly (string Title, string? Composer)[] LinnSuperAudioCollectionVolume3Credits =
    [
        ("He never mentioned love", null),
        ("Painting by Numbers", null),
        ("Beautiful Life", null),
        ("Yes I Know When I ve Had It", null),
        ("Love At Last", null),
        ("A Case of You", null),
        ("Grandes Etudes de Paganini Etude III", "Franz Liszt"),
        ("March K 189", "Wolfgang Amadeus Mozart"),
        ("Capriccio Espagnol Op 34 Scena e canto gitano", "Nikolai Rimsky-Korsakov"),
        ("Ludlow and Teme - When I was one and twenty", "Ivor Gurney"),
        ("Messiah And the Glory of the Lord chorus", "George Frideric Handel"),
        ("A Chloris", "Reynaldo Hahn"),
        ("Missa Brevis - Kyrie", "James MacMillan"),
        ("Sonata for Clarinet and Piano Allegro Con Fuoco", "Francis Poulenc"),
        ("Sonata Graz No 3 for violin continuo in D RV 11 Allegro", "Antonio Vivaldi"),
        ("Cumbees", "Santiago de Murcia")
    ];

    private static readonly (string Title, string? Composer)[] LinnSuperAudioCollectionVolume4Credits =
    [
        ("Pray It Never Happens", null),
        ("Everything I've got belongs to you", null),
        ("A Good and Simple Man", null),
        ("Stars Fell on Alabama", null),
        ("For Alun Lewis", null),
        ("Moonlight's Back in Style", null),
        ("Recercada segunda de tenore ('Trattado de glosas')", "Diego Ortiz"),
        ("Sonata I in B minor - Largo", null),
        ("Aria Sehet, Jesus hat die Hand; Chorus Wohin", null),
        ("In Nomine No. 2 a6 (VdGS 2)", null),
        ("Sonatine - Anime", null),
        ("Pulcinella Suite - II. Serenata", null),
        ("Quatuor pour la fin du Temps - I. Liturgie de cristal", null),
        ("Piano Concerto No. 4 in G major, Op.58 - II. Andante con mo", null),
        ("Symphony No. 38 in D major ('Prague'), K.504 - III. Finale", null)
    ];

    private static readonly (string Title, string? Composer)[] LinnXmasGifts2014Credits =
    [
        ("Almost Like Being In Love", null),
        ("24 Preludes Op 28 No 15 in D flat Major Raindrop", "Frédéric Chopin"),
        ("The Man Who Sold The World", null),
        ("Brandenburg Concerto No 2 in F Major BWV 1047 - III Allegro", "Johann Sebastian Bach"),
        ("Many Rivers To Cross", null),
        ("Symphony No 2 in C major Op 61 - III Adagio espressivo", "Robert Schumann"),
        ("Secret Love", null),
        ("The Well-Tempered Clavier Book I Prelude Fugue No 21 in B flat Major BWV 866", "Johann Sebastian Bach"),
        ("Old Greenwich Time", null),
        ("Recorder Concerto in F Major III Allegro", "Johann Friedrich Fasch"),
        ("Twitter and Bisted", null),
        ("Flute Concerto II Alla Marcia", "Christopher Rouse"),
        ("Forty-two", null),
        ("Symphonie No 2 in C minor III Scherzo Massig schnell", "Anton Bruckner"),
        ("We ll Never Have Manhattan", null),
        ("Sonnerie de Sainte Genevieve du Mont de Paris", "Marin Marais"),
        ("Giant Steps", null),
        ("The End Of A Love Affair", null),
        ("Nicholas Drake", null),
        ("Requiem in D minor K 626 I Requiem aeternam", "Wolfgang Amadeus Mozart"),
        ("Pause", null),
        ("L Envie Vocalise No 28", "Gabriel Fauré"),
        ("Toccata and Fugue in A minor after Bach BWV 565", "Johann Sebastian Bach"),
        ("Both Sides Now", null),
        ("Caledonia", null)
    ];

    [GeneratedRegex("^\\s*(?<number>\\d{1,3})\\s*[-._ ]+\\s*(?<title>.+?)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackFileName();

    [GeneratedRegex("^.+?[-._ ]+(?<number>\\d{1,3})\\s*[-._ ]+\\s*(?<title>.+?)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedTrackFileName();

    [GeneratedRegex("^\\s*(?:cd|disc|disk)\\s*[-._ ]*(?<number>\\d{1,3})(?:\\s*[-._ ].*)?\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscDirectoryName();

    private sealed record TrackEvidence(
        string? Title, string? Album, string? Artist, string? AlbumArtist,
        uint TrackNumber, uint TrackCount, uint DiscNumber, uint DiscCount, uint Year,
        string? Genre, string? Composer, DownloadedArtwork? EmbeddedArtwork,
        uint? FileTrackNumber, string FileTitle);
    private sealed record RepairTrack(
        string RelativePath, string Path, TrackEvidence Evidence, string AudioPayloadBefore, string FullFileSha256, long OriginalSize);
    private sealed record DeduplicatedTrack(string RemovedRelativePath, string RetainedRelativePath, string FullFileSha256);
    private sealed record DeduplicationResult(IReadOnlyList<RepairTrack> Tracks, IReadOnlyList<DeduplicatedTrack> Duplicates);
    private sealed record PositionedTrack(RepairTrack Track, uint DiscNumber, uint TrackNumber);
    private sealed record ResolvedValue(string? Value, string? SourceType, string? Confidence, string? Rationale);
    private sealed class RepairPlan(
        RepairTrack track, string? title, string? album, string? artist, string? albumArtist,
        uint trackNumber, uint discNumber, uint year, string? genre, string? composer, string? composerSource,
        string titleSource, string artistSource)
    {
        public RepairTrack Track { get; } = track;
        public string? Title { get; } = title;
        public string? Album { get; } = album;
        public string? Artist { get; } = artist;
        public string? AlbumArtist { get; } = albumArtist;
        public uint TrackNumber { get; } = trackNumber;
        public uint DiscNumber { get; } = discNumber;
        public uint Year { get; } = year;
        public string? Genre { get; } = genre;
        public string? Composer { get; } = composer;
        public string? ComposerSource { get; } = composerSource;
        public string TitleSource { get; } = titleSource;
        public string ArtistSource { get; } = artistSource;
        public string AudioPayloadAfter { get; set; } = string.Empty;
    }
}

internal static class TrackAudioPayload
{
    public static Task<string> Sha256Async(string path, CancellationToken token = default) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".flac" => FlacAudioPayload.Sha256Async(path, token),
            ".dsf" => DsfAudioPayload.Sha256Async(path, token),
            ".dff" => DffMetadata.AudioSha256Async(path, token),
            _ => throw new InvalidDataException($"Unsupported existing-track repair format: {path}")
        };
}

internal static class DsfAudioPayload
{
    public static async Task<string> Sha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[28];
        await stream.ReadExactlyAsync(header, token);
        if (!Encoding.ASCII.GetString(header, 0, 4).Equals("DSD ", StringComparison.Ordinal))
            throw new InvalidDataException($"The file does not contain a native DSF header: {path}");
        var metadataOffset = checked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20, 8)));
        while (stream.Position + 12 <= stream.Length && (metadataOffset == 0 || stream.Position < metadataOffset))
        {
            var chunk = new byte[12];
            await stream.ReadExactlyAsync(chunk, token);
            var chunkName = Encoding.ASCII.GetString(chunk, 0, 4);
            var chunkSize = checked((long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(4, 8)));
            var payloadLength = chunkSize - 12;
            if (payloadLength < 0 || stream.Position + payloadLength > stream.Length)
                throw new InvalidDataException($"Invalid DSF chunk size in {path}.");
            if (!chunkName.Equals("data", StringComparison.Ordinal))
            {
                stream.Position += payloadLength;
                continue;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var remaining = payloadLength;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0) throw new EndOfStreamException($"Unexpected end of DSF audio payload: {path}");
                hash.AppendData(buffer.AsSpan(0, read));
                remaining -= read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        throw new InvalidDataException($"The DSF audio data chunk is missing from {path}.");
    }
}

internal static class FlacAudioPayload
{
    public static async Task<string> Sha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var first = new byte[10];
        await stream.ReadExactlyAsync(first, token);
        if (first[0] == (byte)'I' && first[1] == (byte)'D' && first[2] == (byte)'3')
        {
            var size = (first[6] & 0x7f) << 21 | (first[7] & 0x7f) << 14 | (first[8] & 0x7f) << 7 | first[9] & 0x7f;
            stream.Position = 10L + size + ((first[5] & 0x10) != 0 ? 10 : 0);
            var marker = new byte[4];
            await stream.ReadExactlyAsync(marker, token);
            RequireMarker(marker, path);
        }
        else
        {
            RequireMarker(first.AsSpan(0, 4), path);
            stream.Position = 4;
        }

        var last = false;
        var header = new byte[4];
        while (!last)
        {
            await stream.ReadExactlyAsync(header, token);
            last = (header[0] & 0x80) != 0;
            var length = header[1] << 16 | header[2] << 8 | header[3];
            stream.Seek(length, SeekOrigin.Current);
        }
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(await sha256.ComputeHashAsync(stream, token));
    }

    private static void RequireMarker(ReadOnlySpan<byte> marker, string path)
    {
        if (marker.Length < 4 || marker[0] != (byte)'f' || marker[1] != (byte)'L' || marker[2] != (byte)'a' || marker[3] != (byte)'C')
            throw new InvalidDataException($"The file does not contain a native FLAC stream marker: {path}");
    }
}
