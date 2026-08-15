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
        if (scan.Mode != WorkflowMode.ExistingTrackRepair || scan.CueCount != 0 || scan.ImageCount != 0)
            throw new NotSupportedException("Verified existing-track repair requires standalone FLAC tracks without a CUE sheet or source image.");

        var inventory = scan.Media
            .Where(item => item.Kind.Equals("Existing FLAC", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (inventory.Length < 2)
            throw new InvalidOperationException("Existing-track repair requires at least two inventoried FLAC tracks.");

        progress.Report(Snapshot(JobPhase.Processing, 19,
            "Reading existing FLAC tags, embedded artwork, and filename evidence from the verified local source."));
        var tracks = new List<RepairTrack>(inventory.Length);
        foreach (var item in inventory)
        {
            token.ThrowIfCancellationRequested();
            var input = HostStagingService.SafeCombine(staged.InputAlbumRoot, item.RelativePath);
            var output = HostStagingService.SafeCombine(staged.AlbumRoot, item.RelativePath);
            if (!File.Exists(input)) throw new FileNotFoundException("An inventoried FLAC track is missing from the verified source.", input);
            if (!staged.SourceCacheUsed)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                await HostStagingService.CopyFileAsync(input, output, null, token);
            }
            else if (!Path.GetFullPath(input).Equals(Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The network source cache does not map to the repair output tree.");
            }

            var evidence = ReadEvidence(output, item.RelativePath);
            var payload = await FlacAudioPayload.Sha256Async(output, token);
            tracks.Add(new(item.RelativePath, output, evidence, payload, item.Size));
        }
        var positionedTracks = PositionTracks(tracks);

        var folderIdentity = ParseFolderIdentity(scan.AlbumName);
        var album = Consensus(tracks.Select(track => track.Evidence.Album)) ?? folderIdentity.Album;
        var albumArtist = Consensus(tracks.Select(track => track.Evidence.AlbumArtist))
            ?? Consensus(tracks.Select(track => track.Evidence.Artist))
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
                new(album, albumArtist, tracks.Count, OriginalYear: existingYear, RequireSacd: false, TrackTitleHints: titleHints),
                includeTrackTitles: true,
                token: token);
            warnings.AddRange(external.Warnings);
        }
        else
        {
            warnings.Add("External lookup requires a reliable album and artist from existing tags or the album-folder name.");
        }

        var preparedCover = await PrepareCoverAsync(tracks, staged, external, warnings, token);
        var consensusGenre = Consensus(tracks.Select(track => track.Evidence.Genre));
        var folderGenre = LibraryFolderMetadata.InferGenre(scan.AlbumRoot);
        var genre = consensusGenre ?? Nonempty(external?.Genre) ?? folderGenre;
        var externalYear = FindYear(Nonempty(external?.OriginalDate) ?? Nonempty(external?.ReleaseDate));
        var resolvedYear = existingYear ?? externalYear ?? FindYear(scan.AlbumName);
        var remoteTitles = external?.TrackTitles.Count == tracks.Count ? external.TrackTitles : [];
        var plans = new List<RepairPlan>(positionedTracks.Count);
        for (var index = 0; index < positionedTracks.Count; index++)
        {
            var positioned = positionedTracks[index];
            var evidence = positioned.Track.Evidence;
            var title = Nonempty(evidence.Title)
                ?? (remoteTitles.Count == tracks.Count ? Nonempty(remoteTitles[index]) : null)
                ?? evidence.FileTitle;
            plans.Add(new(
                positioned.Track,
                title,
                Nonempty(evidence.Album) ?? album,
                Nonempty(evidence.Artist) ?? albumArtist,
                Nonempty(evidence.AlbumArtist) ?? albumArtist,
                positioned.TrackNumber,
                positioned.DiscNumber,
                evidence.Year > 0 ? evidence.Year : resolvedYear is null ? 0u : (uint)resolvedYear.Value,
                Nonempty(evidence.Genre) ?? genre,
                Nonempty(evidence.Composer),
                Nonempty(evidence.Title) is not null ? "existing_tag" : remoteTitles.Count == tracks.Count ? "external_tracklist" : "filename"));
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
            using (var file = TagFile.Create(plan.Track.Path))
            {
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
            var after = await FlacAudioPayload.Sha256Async(plan.Track.Path, token);
            if (!after.Equals(plan.Track.AudioPayloadBefore, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The compressed FLAC audio payload changed while repairing '{plan.Track.RelativePath}'. The original remains untouched.");
            plan.AudioPayloadAfter = after;
        }

        var missing = MissingFields(plans, preparedCover is not null);
        var evidenceList = new List<string>
        {
            "existing embedded FLAC tags (highest metadata priority)",
            "track filenames (fallback track/title evidence)",
            $"album folder: {scan.AlbumName}"
        };
        if (tracks.Any(track => track.Evidence.EmbeddedArtwork is not null))
            evidenceList.Add("existing embedded track artwork (highest artwork priority)");
        evidenceList.AddRange(external?.Sources ?? []);
        if (preparedCover is not null) evidenceList.Add(preparedCover.Source);
        evidenceList = evidenceList.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        await WriteReportAsync(scan, staged, plans, preparedCover, genre, consensusGenre is not null,
            external, missing, evidenceList, warnings, reportPath, token);
        await WriteGapManifestAsync(staged.JobDirectory, missing, evidenceList, token);
        progress.Report(Snapshot(JobPhase.Tagging, missing.Count == 0 ? 46 : 44,
            missing.Count == 0
                ? "Track repair completed locally; exact compressed-audio payload hashes are unchanged."
                : $"Track repair evidence remains incomplete: {string.Join(", ", missing)}."));
        return new(plans.Count, reportPath, new(true, missing.Count > 0, missing, evidenceList));
    }

    private async Task<PreparedArtwork?> PrepareCoverAsync(
        IReadOnlyList<RepairTrack> tracks,
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

        var local = await _artwork.PrepareLocalAsync(staged.InputAlbumRoot, staged.FfmpegPath, staged.FfprobePath,
            ArtworkSelectionMode.Flac, token);
        if (local.Artwork is not null) return local.Artwork;
        if (local.Issue is not null) warnings.Add(local.Issue);

        var externalCover = await _artwork.PrepareExternalAsync(
            _externalMetadata, external?.MusicBrainzReleaseId, staged.FfmpegPath, staged.FfprobePath, token);
        if (externalCover.Artwork is not null) return externalCover.Artwork;
        if (externalCover.Issue is not null) warnings.Add(externalCover.Issue);
        return null;
    }

    private static TrackEvidence ReadEvidence(string path, string relativePath)
    {
        using var file = TagFile.Create(path);
        if (!file.Properties.MediaTypes.HasFlag(TagLib.MediaTypes.Audio) || file.Properties.AudioSampleRate <= 0)
            throw new InvalidDataException($"The existing track is not a readable audio FLAC: {relativePath}");
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
        if (plans.Any(plan => plan.Genre?.Contains("classical", StringComparison.OrdinalIgnoreCase) == true ||
                              plan.Genre?.Contains("opera", StringComparison.OrdinalIgnoreCase) == true) &&
            plans.Any(plan => plan.Composer is null)) missing.Add("COMPOSER");
        if (!hasCover) missing.Add("COVER");
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static async Task WriteReportAsync(
        ScanResult scan,
        StagedJob staged,
        IReadOnlyList<RepairPlan> plans,
        PreparedArtwork? cover,
        string? genre,
        bool genreWasExisting,
        ExternalAlbumMetadata? external,
        IReadOnlyList<string> missing,
        IReadOnlyList<string> evidence,
        IReadOnlyList<string> warnings,
        string reportPath,
        CancellationToken token)
    {
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
            ["format"] = "flac",
            ["source_type"] = "existing_flac_tracks",
            ["workflow_mode"] = "existing_track_repair",
            ["generated_by"] = "Album Fixer deterministic existing-track repair processor",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["metadata_sources"] = new JsonArray(evidence.Select(value => JsonValue.Create(value)).ToArray()),
            ["sources"] = new JsonArray(plans.Select(plan => (JsonNode)new JsonObject
            {
                ["path"] = JsonPath(plan.Track.RelativePath),
                ["type"] = "Existing FLAC track",
                ["size"] = plan.Track.OriginalSize
            }).ToArray()),
            ["discs"] = discs,
            ["verification"] = new JsonObject
            {
                ["status"] = missing.Count == 0 ? "pending" : "awaiting_metadata",
                ["method"] = "Exact SHA-256 equality of each compressed FLAC audio-frame payload before and after tag/art repair; final-path verification pending.",
                ["audio_payload_equivalence"] = "passed_locally",
                ["sources_deleted"] = false,
                ["errors"] = new JsonArray()
            },
            ["metadata_lookup"] = new JsonObject
            {
                ["implementation"] = "deterministic_local_code",
                ["priority"] = new JsonArray("existing_tags_and_embedded_artwork", "external_exact_album_match", "track_filename_fallback"),
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
        if (genre is not null)
        {
            report["genre"] = new JsonObject
            {
                ["value"] = genre,
                ["source_type"] = genreWasExisting ? "existing_track_tag" : external?.GenreSourceType ?? "recognized_library_genre_folder",
                ["confidence"] = genreWasExisting ? "high" : external?.GenreConfidence ?? "high",
                ["rationale"] = genreWasExisting
                    ? "Existing nonempty track genre tags have priority over filename and external fallback evidence."
                    : external?.GenreRationale ?? "A recognized library category supplied genre only; it was not used as artist metadata."
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

    private static (uint? Number, string Title) ParseFileName(string value)
    {
        var match = TrackFileName().Match(value);
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
    private static int? FindYear(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, "(?:19|20)\\d{2}");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }
    private static string JsonPath(string path) => path.Replace('\\', '/');
    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) =>
        new(phase, percent, "running", detail, DateTimeOffset.UtcNow);

    [GeneratedRegex("^\\s*(?<number>\\d{1,3})\\s*[-._ ]+\\s*(?<title>.+?)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrackFileName();

    [GeneratedRegex("^\\s*(?:cd|disc|disk)\\s*[-._ ]*(?<number>\\d{1,3})(?:\\s*[-._ ].*)?\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DiscDirectoryName();

    private sealed record TrackEvidence(
        string? Title, string? Album, string? Artist, string? AlbumArtist,
        uint TrackNumber, uint TrackCount, uint DiscNumber, uint DiscCount, uint Year,
        string? Genre, string? Composer, DownloadedArtwork? EmbeddedArtwork,
        uint? FileTrackNumber, string FileTitle);
    private sealed record RepairTrack(string RelativePath, string Path, TrackEvidence Evidence, string AudioPayloadBefore, long OriginalSize);
    private sealed record PositionedTrack(RepairTrack Track, uint DiscNumber, uint TrackNumber);
    private sealed class RepairPlan(
        RepairTrack track, string? title, string? album, string? artist, string? albumArtist,
        uint trackNumber, uint discNumber, uint year, string? genre, string? composer, string titleSource)
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
        public string TitleSource { get; } = titleSource;
        public string AudioPayloadAfter { get; set; } = string.Empty;
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
