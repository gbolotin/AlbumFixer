using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TagFile = TagLib.File;

namespace AlbumFixer.Core;

public sealed partial class LocalDsdProcessor
{
    private readonly ExternalMetadataService _externalMetadata;

    public LocalDsdProcessor() : this(new ExternalMetadataService()) { }

    public LocalDsdProcessor(ExternalMetadataService externalMetadata)
    {
        _externalMetadata = externalMetadata ?? throw new ArgumentNullException(nameof(externalMetadata));
    }

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public async Task<LocalSplitResult> ProcessAsync(
        ScanResult scan,
        StagedJob staged,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token = default)
    {
        if (scan.Mode != WorkflowMode.DsdExtraction)
            throw new NotSupportedException("The DSD processor requires a SACD ISO extraction workflow.");
        if (string.IsNullOrWhiteSpace(staged.SacdExtractPath) || !File.Exists(staged.SacdExtractPath))
            throw new FileNotFoundException("The staged sacd_extract tool is unavailable.", staged.SacdExtractPath);
        if (staged.Sources.Count != 1 || !Path.GetExtension(staged.Sources[0].RelativePath).Equals(".iso", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verified SACD processing requires exactly one inventoried ISO source.");

        var source = staged.Sources[0];
        var iso = HostStagingService.SafeCombine(staged.InputAlbumRoot, source.RelativePath);
        var workingDirectory = Path.GetDirectoryName(staged.SacdExtractPath)!;
        var versionOutput = await RunProcessAsync(staged.SacdExtractPath, ["-v"], workingDirectory, null, token, allowNonzero: false);
        var version = versionOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("sacd_extract client", StringComparison.OrdinalIgnoreCase))?.Trim()
            ?? "sacd_extract version not reported";

        progress.Report(Snapshot(JobPhase.Processing, 19, staged.SourceCacheUsed
            ? "Reading the verified Temp-cached SACD layout."
            : "Reading the fixed-disk SACD ISO in place without a source-cache copy."));
        var layoutCommand = CommandText(staged.SacdExtractPath, ["-P", "-i", iso]);
        var layoutOutput = await RunProcessAsync(staged.SacdExtractPath, ["-P", "-i", iso], workingDirectory, null, token);
        var layoutPath = Path.Combine(staged.AlbumRoot, "sacd_extract-layout.txt");
        await File.WriteAllTextAsync(layoutPath, layoutOutput, new UTF8Encoding(false), token);
        var structuralLayout = ParseLayout(layoutOutput);
        if (structuralLayout.Areas.Count == 0) throw new InvalidDataException("sacd_extract reported no playable SACD areas.");

        var years = ResolveYears(scan.AlbumName, structuralLayout.CreationDate);
        var localIdentity = ResolveLocalIdentity(scan, structuralLayout);
        var trackCount = structuralLayout.Areas.Max(area => area.Tracks.Count);
        progress.Report(Snapshot(JobPhase.Processing, 20,
            "Resolving missing SACD identity by exact catalog number, checksum filename, folder name, and matching external track listing."));
        ExternalAlbumIdentity? catalogIdentity = null;
        if (Nonempty(structuralLayout.CatalogNumber) is { } catalogNumber)
            catalogIdentity = await _externalMetadata.ResolveIdentityByCatalogAsync(
                catalogNumber, trackCount, years.Edition, requireSacd: true, token);
        var identifiedLayout = ApplyAlbumIdentity(structuralLayout, localIdentity, catalogIdentity);
        if (string.IsNullOrWhiteSpace(identifiedLayout.AlbumTitle) || string.IsNullOrWhiteSpace(identifiedLayout.AlbumArtist))
            throw new InvalidDataException(
                "The SACD disc text has no album title or artist, and exact catalog, checksum-filename, and folder-name fallback did not establish both fields unambiguously.");

        var existingTrackHints = identifiedLayout.Areas
            .OrderByDescending(area => area.Tracks.Count(track => !string.IsNullOrWhiteSpace(track.Title)))
            .First().Tracks.Select(track => track.Title).Where(title => !string.IsNullOrWhiteSpace(title)).ToArray();
        var external = await _externalMetadata.ResolveAsync(new(
            identifiedLayout.AlbumTitle,
            identifiedLayout.AlbumArtist,
            trackCount,
            years.Original,
            years.Edition,
            identifiedLayout.CatalogNumber,
            TrackTitleHints: existingTrackHints.Length == trackCount ? existingTrackHints : null),
            includeTrackTitles: true,
            token);
        if (catalogIdentity is not null)
            external = external with
            {
                Sources = external.Sources.Append(catalogIdentity.Source).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        var layout = ApplyExternalTrackListing(identifiedLayout, catalogIdentity, external);
        var metadata = ResolveMetadata(scan.AlbumRoot, layout, years, external);
        var requiredMissingMetadata = MetadataFieldPolicy.RequiredMissing(metadata.MissingFields);
        var optionalMissingMetadata = MetadataFieldPolicy.OptionalMissing(metadata.MissingFields);
        var metadataWarnings = metadata.Warnings
            .Concat(optionalMissingMetadata.Select(field => $"Optional metadata field remains unresolved: {field}."))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var requiredMetadataIncomplete = requiredMissingMetadata.Count > 0;
        var coverReleaseId = external.MusicBrainzReleaseId ?? catalogIdentity?.MusicBrainzReleaseId;
        var preparedCover = await new InMemoryArtworkService().PrepareLocalThenExternalAsync(
            staged.InputAlbumRoot,
            staged.FfmpegPath,
            staged.FfprobePath,
            ArtworkSelectionMode.Dsd,
            _externalMetadata,
            coverReleaseId,
            token);
        var cover = preparedCover.Artwork ?? throw new InvalidDataException(
            preparedCover.Issue ?? "SACD extraction requires usable local artwork or an exact external front-cover match.");
        if (cover.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            progress.Report(Snapshot(JobPhase.Processing, 20, "No usable local cover was found; an exact external front cover was normalized safely in memory."));
        var reportAreas = new JsonArray();
        var allTrackReports = new List<JsonObject>();
        var totalTracks = 0;

        for (var areaIndex = 0; areaIndex < layout.Areas.Count; areaIndex++)
        {
            token.ThrowIfCancellationRequested();
            var area = layout.Areas[areaIndex];
            var areaFolderName = area.IsStereo ? "Stereo" : "Multichannel";
            var finalAreaRoot = Path.Combine(staged.AlbumRoot, areaFolderName);
            if (Directory.Exists(finalAreaRoot) || File.Exists(finalAreaRoot))
                throw new IOException($"The final SACD area path already exists and will not be overwritten: {areaFolderName}");

            var primaryRoot = Path.Combine(staged.JobDirectory, "sacd-primary", areaFolderName);
            var independentRoot = Path.Combine(staged.JobDirectory, "sacd-independent", areaFolderName);
            Directory.CreateDirectory(primaryRoot);
            Directory.CreateDirectory(independentRoot);
            var primaryArguments = ExtractionArguments(area.IsStereo, iso, primaryRoot);
            var independentArguments = ExtractionArguments(area.IsStereo, iso, independentRoot);

            progress.Report(Snapshot(JobPhase.Processing, 21 + areaIndex * 8,
                $"Extracting the {area.DisplayName} area to untagged DSF tracks."));
            var primaryLog = await RunProcessAsync(staged.SacdExtractPath, primaryArguments, workingDirectory,
                percent => progress.Report(Snapshot(JobPhase.Processing, Math.Min(36, 22 + percent / 8),
                    $"Extracting {area.DisplayName}: {percent}%")), token);
            var primaryLogPath = Path.Combine(staged.AlbumRoot, $"sacd_extract-{areaFolderName.ToLowerInvariant()}.log");
            await File.WriteAllTextAsync(primaryLogPath, primaryLog, new UTF8Encoding(false), token);

            progress.Report(Snapshot(JobPhase.Processing, 37 + areaIndex * 3,
                $"Repeating the {area.DisplayName} extraction independently for deletion-grade verification."));
            var independentLog = await RunProcessAsync(staged.SacdExtractPath, independentArguments, workingDirectory, null, token);
            var independentLogPath = Path.Combine(staged.AlbumRoot, $"sacd_extract-{areaFolderName.ToLowerInvariant()}-independent.log");
            await File.WriteAllTextAsync(independentLogPath, independentLog, new UTF8Encoding(false), token);

            var primaryFiles = FindDsfFiles(primaryRoot);
            var independentFiles = FindDsfFiles(independentRoot);
            if (primaryFiles.Count != area.Tracks.Count || independentFiles.Count != area.Tracks.Count)
                throw new InvalidDataException($"{area.DisplayName} extraction produced {primaryFiles.Count} and {independentFiles.Count} tracks; the disc lists {area.Tracks.Count}.");

            Directory.CreateDirectory(finalAreaRoot);
            var trackReports = new JsonArray();
            for (var trackIndex = 0; trackIndex < area.Tracks.Count; trackIndex++)
            {
                token.ThrowIfCancellationRequested();
                var track = area.Tracks[trackIndex];
                var primary = primaryFiles[trackIndex];
                var independent = independentFiles[trackIndex];
                var primarySize = new FileInfo(primary).Length;
                var independentSize = new FileInfo(independent).Length;
                if (primarySize != independentSize)
                    throw new InvalidDataException($"Independent extraction size mismatch for {area.DisplayName} track {track.Number:00}.");

                var outputName = $"{track.Number:00} - {SafeFileName(track.Title)}.dsf";
                var finalTrack = Path.Combine(finalAreaRoot, outputName);
                File.Move(primary, finalTrack, overwrite: false);
                var payloadBytesBefore = await DsfPayloadLengthAsync(finalTrack, token);
                await WriteTagsAsync(finalTrack, cover, layout, area, track, metadata, token);
                var payloadBytesAfter = await DsfPayloadLengthAsync(finalTrack, token);
                if (payloadBytesBefore != payloadBytesAfter)
                    throw new InvalidDataException($"DSD audio payload size changed while tagging {areaFolderName}/{outputName}.");

                var probe = await VerifyDsfAsync(staged.FfprobePath, finalTrack, layout, area, track, metadata, cover.Sha256, token);
                var relative = JsonPath(HostStagingService.SafeRelative(staged.AlbumRoot, finalTrack));
                var trackReport = new JsonObject
                {
                    ["track"] = track.Number,
                    ["title"] = track.Title,
                    ["performer"] = track.Performer,
                    ["isrc"] = track.Isrc,
                    ["file"] = relative,
                    ["duration_seconds"] = probe.Duration.TotalSeconds,
                    ["container"] = "dsf",
                    ["encoding"] = "dsd",
                    ["dsd_rate_hz"] = probe.SampleRate,
                    ["channels"] = probe.Channels,
                    ["channel_layout"] = probe.ChannelLayout,
                    ["dst_decompressed"] = true,
                    ["untagged_size"] = primarySize,
                    ["independent_extraction_size"] = independentSize,
                    ["dsd_payload_bytes_before_tags"] = payloadBytesBefore,
                    ["dsd_payload_bytes_after_tags"] = payloadBytesAfter,
                    ["file_size"] = new FileInfo(finalTrack).Length,
                    ["metadata_version"] = "ID3v2"
                };
                trackReports.Add(trackReport);
                allTrackReports.Add(trackReport);
                totalTracks++;
            }

            reportAreas.Add(new JsonObject
            {
                ["area"] = area.IsStereo ? "stereo" : "multichannel",
                ["speaker_config"] = area.SpeakerConfig,
                ["track_count"] = area.Tracks.Count,
                ["total_play_time"] = area.TotalPlayTime,
                ["source"] = JsonPath(source.RelativePath),
                ["extraction_command"] = CommandText(staged.SacdExtractPath, primaryArguments),
                ["independent_extraction_command"] = CommandText(staged.SacdExtractPath, independentArguments),
                ["tracks"] = trackReports
            });
        }

        progress.Report(Snapshot(JobPhase.Tagging, 45, "DSF tags and front-cover artwork were written; every DSD payload size is unchanged."));
        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        var report = new JsonObject
        {
            ["schema_version"] = "2.0",
            ["album"] = layout.AlbumTitle,
            ["artist"] = layout.AlbumArtist,
            ["edition"] = string.Join(", ", new[] { metadata.CatalogNumber, "SACD", scan.AlbumName }.Where(value => !string.IsNullOrWhiteSpace(value))),
            ["format"] = "dsf",
            ["source_type"] = "sacd_iso",
            ["workflow_mode"] = "sacd_iso_extract",
            ["generated_by"] = "Album Fixer deterministic SACD processor",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["source"] = new JsonObject
            {
                ["file"] = JsonPath(source.RelativePath),
                ["size"] = source.Size
            },
            ["extraction_tool"] = version,
            ["disc_layout_command"] = layoutCommand,
            ["disc_layout_file"] = "sacd_extract-layout.txt",
            ["metadata_sources"] = new JsonArray((layout.IdentitySources ?? [])
                .Append(cover.Source)
                .Concat(metadata.Sources)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => JsonValue.Create(value)).ToArray()),
            ["release_metadata"] = new JsonObject
            {
                ["original_date"] = metadata.OriginalDate,
                ["release_date"] = metadata.ReleaseDate,
                ["label"] = metadata.Label,
                ["catalog_number"] = metadata.CatalogNumber,
                ["barcode"] = metadata.Barcode,
                ["release_country"] = metadata.ReleaseCountry
            },
            ["metadata_lookup"] = new JsonObject
            {
                ["status"] = requiredMetadataIncomplete
                    ? metadata.Sources.Count > 0 ? "partial" : "unresolved"
                    : optionalMissingMetadata.Count > 0 ? "complete_with_optional_gaps" : "complete",
                ["nonblocking"] = true,
                ["sources"] = new JsonArray(metadata.Sources.Select(value => JsonValue.Create(value)).ToArray()),
                ["warnings"] = new JsonArray(metadataWarnings.Select(value => JsonValue.Create(value)).ToArray())
            },
            ["cover"] = cover.ToReport(),
            ["genre"] = new JsonObject
            {
                ["value"] = metadata.Genre,
                ["source_type"] = metadata.GenreSourceType,
                ["confidence"] = metadata.GenreConfidence,
                ["rationale"] = metadata.GenreRationale
            },
            ["areas"] = reportAreas,
            ["artifacts"] = new JsonArray(Directory.EnumerateFiles(staged.AlbumRoot, "sacd_extract-*", SearchOption.TopDirectoryOnly)
                .Select(path => (JsonNode)JsonValue.Create(JsonPath(Path.GetFileName(path)))!).ToArray()),
            ["verification"] = new JsonObject
            {
                ["status"] = requiredMetadataIncomplete ? "incomplete" : "passed",
                ["method"] = "Two deterministic untagged extractions per reported area; per-track file-size equality; DSF/DSD probe and signal checks; unchanged DSD payload size through ID3/artwork tagging.",
                ["independent_extraction"] = "passed",
                ["tag_payload_size_verification"] = "passed",
                ["audio_and_tags"] = "passed",
                ["source_deletion_eligible"] = !requiredMetadataIncomplete,
                ["sources_deleted"] = false,
                ["errors"] = new JsonArray(),
                ["warnings"] = new JsonArray(metadataWarnings.Select(value => JsonValue.Create(value)).ToArray()),
                ["missing_metadata"] = new JsonArray(metadata.MissingFields.Select(value => JsonValue.Create(value)).ToArray()),
                ["missing_required_metadata"] = new JsonArray(requiredMissingMetadata.Select(value => JsonValue.Create(value)).ToArray()),
                ["missing_optional_metadata"] = new JsonArray(optionalMissingMetadata.Select(value => JsonValue.Create(value)).ToArray())
            },
            ["job"] = new JsonObject
            {
                ["identifier"] = Path.GetFileName(staged.JobDirectory),
                ["local_staging_used"] = true,
                ["source_cache_used"] = staged.SourceCacheUsed,
                ["source_input_mode"] = staged.SourceCacheUsed ? "size_checked_temp_cache" : "local_fixed_disk_in_place",
                ["staging_path"] = staged.JobDirectory,
                ["processor"] = "local_sacd_extract_sequential_areas"
            }
        };
        report["work_status"] = requiredMetadataIncomplete ? "incomplete" : "complete";
        if (requiredMetadataIncomplete)
        {
            report["incomplete_work"] = new JsonObject
            {
                ["reason"] = "Required metadata remains unresolved. Verified DSF tracks remain usable.",
                ["repairable_without_source_image"] = true,
                ["issues"] = new JsonArray(requiredMissingMetadata.Select(value => JsonValue.Create($"Missing required metadata: {value}")).ToArray())
            };
        }
        await AtomicWriteAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        var evidence = (layout.IdentitySources ?? [])
            .Append($"front-cover artwork: {cover.Source}")
            .Concat(metadata.Sources)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await WriteGapManifestAsync(staged.JobDirectory, metadata.MissingFields, evidence, token);
        progress.Report(Snapshot(JobPhase.Tagging, 48, $"SACD extraction and deletion-grade local verification completed for {totalTracks} tracks."));
        return new(totalTracks, reportPath, new MetadataGapResult(true, metadata.MissingFields.Count > 0, metadata.MissingFields, evidence));
    }

    internal static async Task<long> DsfPayloadLengthAsync(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[28];
        await ReadExactlyAsync(stream, header, token);
        if (!Encoding.ASCII.GetString(header, 0, 4).Equals("DSD ", StringComparison.Ordinal))
            throw new InvalidDataException($"Not a DSF file: {path}");
        var metadataOffset = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(20, 8)));
        while (stream.Position + 12 <= stream.Length && (metadataOffset == 0 || stream.Position < metadataOffset))
        {
            var chunk = new byte[12];
            await ReadExactlyAsync(stream, chunk, token);
            var chunkName = Encoding.ASCII.GetString(chunk, 0, 4);
            var chunkSize = checked((long)BinaryPrimitives.ReadUInt64LittleEndian(chunk.AsSpan(4, 8)));
            if (chunkSize < 12 || stream.Position - 12 + chunkSize > stream.Length)
                throw new InvalidDataException($"Invalid DSF chunk size in {path}.");
            if (chunkName.Equals("data", StringComparison.Ordinal))
                return chunkSize - 12;
            stream.Position += chunkSize - 12;
        }
        throw new InvalidDataException($"The DSF audio data chunk is missing from {path}.");
    }

    private static async Task WriteTagsAsync(
        string path, PreparedArtwork cover, SacdLayout layout, SacdArea area, SacdTrack track, ResolvedDsdMetadata metadata, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = TagFile.Create(path);
        file.Tag.Title = track.Title;
        file.Tag.Album = layout.AlbumTitle;
        file.Tag.Performers = [track.Performer];
        file.Tag.AlbumArtists = [layout.AlbumArtist];
        file.Tag.Track = (uint)track.Number;
        file.Tag.TrackCount = (uint)area.Tracks.Count;
        file.Tag.Disc = 1;
        file.Tag.DiscCount = 1;
        file.Tag.Year = (uint)metadata.OriginalYear;
        file.Tag.Genres = [metadata.Genre];
        file.Tag.Comment = $"MIX={area.DisplayName}";
        file.Tag.Pictures =
        [
            InMemoryArtworkService.CreatePicture(cover)
        ];
        if (file.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
        {
            SetUserText(id3, "MIX", area.DisplayName);
            SetUserText(id3, "LABEL", metadata.Label);
            SetUserText(id3, "CATALOGNUMBER", metadata.CatalogNumber);
            SetUserText(id3, "BARCODE", metadata.Barcode);
            SetUserText(id3, "RELEASECOUNTRY", metadata.ReleaseCountry);
            SetUserText(id3, "ORIGINALDATE", metadata.OriginalDate);
            SetUserText(id3, "RELEASEDATE", metadata.ReleaseDate);
        }
        file.Save();
        await Task.CompletedTask;
    }

    internal static async Task<DsfProbe> VerifyDsfAsync(
        string ffprobe, string path, SacdLayout layout, SacdArea area, SacdTrack track, ResolvedDsdMetadata metadata, string expectedArtworkSha256,
        CancellationToken token = default)
    {
        var output = await RunProcessAsync(ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], Path.GetDirectoryName(ffprobe)!, null, token);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var audio = root.GetProperty("streams").EnumerateArray().FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        if (audio.ValueKind == JsonValueKind.Undefined) throw new InvalidDataException($"No audio stream was found in {path}.");
        var codec = Text(audio, "codec_name") ?? string.Empty;
        if (!codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase) || codec.Contains("pcm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The output is not a native DSD stream: {path}");
        var reportedSampleRate = ParseInt(audio, "sample_rate");
        var sampleRate = NormalizeDsdRate(reportedSampleRate);
        var channels = ParseInt(audio, "channels");
        if (sampleRate == 0)
            throw new InvalidDataException($"Unexpected DSD rate in {path}: {reportedSampleRate}");
        if (area.IsStereo ? channels != 2 : channels <= 2)
            throw new InvalidDataException($"The channel count does not match the {area.DisplayName} area: {path}");
        var duration = ParseDuration(root, audio);
        if (Math.Abs(duration.TotalSeconds - track.Duration.TotalSeconds) > 2.0)
            throw new InvalidDataException($"The duration does not match the SACD table for track {track.Number:00}: {path}");

        using var tagged = TagFile.Create(path);
        var required = new Dictionary<string, string?>
        {
            ["TITLE"] = tagged.Tag.Title,
            ["ALBUM"] = tagged.Tag.Album,
            ["ARTIST"] = tagged.Tag.FirstPerformer,
            ["ALBUMARTIST"] = tagged.Tag.FirstAlbumArtist,
            ["GENRE"] = tagged.Tag.FirstGenre
        };
        var missing = required.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToArray();
        if (missing.Length > 0 || tagged.Tag.Track != track.Number || tagged.Tag.TrackCount != area.Tracks.Count ||
            tagged.Tag.Disc != 1 || tagged.Tag.DiscCount != 1 || tagged.Tag.Year != (uint)metadata.OriginalYear ||
            !tagged.Tag.FirstGenre.Equals(metadata.Genre, StringComparison.OrdinalIgnoreCase) || tagged.Tag.Pictures.Length == 0)
            throw new InvalidDataException($"Required DSF metadata or artwork did not read back correctly from {path}: {string.Join(", ", missing)}");
        if (tagged.GetTag(TagLib.TagTypes.Id3v2) is not TagLib.Id3v2.Tag id3 ||
            !UserTextEquals(id3, "MIX", area.DisplayName) ||
            !UserTextEquals(id3, "LABEL", metadata.Label) ||
            !UserTextEquals(id3, "CATALOGNUMBER", metadata.CatalogNumber) ||
            !UserTextEquals(id3, "BARCODE", metadata.Barcode) ||
            !UserTextEquals(id3, "RELEASECOUNTRY", metadata.ReleaseCountry) ||
            !UserTextEquals(id3, "ORIGINALDATE", metadata.OriginalDate) ||
            !UserTextEquals(id3, "RELEASEDATE", metadata.ReleaseDate))
            throw new InvalidDataException($"Extended DSF release metadata did not read back correctly from {path}.");
        var artworkSha256 = InMemoryArtworkService.ReadFrontCoverSha256(path);
        if (!string.Equals(artworkSha256, expectedArtworkSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The embedded DSF artwork does not match the prepared in-memory cover: {path}");
        return new(sampleRate, channels, Text(audio, "channel_layout") ?? (channels == 2 ? "stereo" : $"{channels} channels"), duration);
    }

    private static int NormalizeDsdRate(int reportedSampleRate)
    {
        // FFmpeg currently exposes packed DSD as bytes per second (for example,
        // 352800 for DSD64), while SACD rates are conventionally expressed in bits
        // per second (2822400). Accept either convention and report the DSD rate.
        ReadOnlySpan<int> nativeRates = [2_822_400, 5_644_800, 11_289_600, 22_579_200, 45_158_400];
        foreach (var rate in nativeRates)
        {
            if (reportedSampleRate == rate || reportedSampleRate > 0 && reportedSampleRate <= int.MaxValue / 8 && reportedSampleRate * 8 == rate)
                return rate;
        }
        return 0;
    }

    internal static async Task VerifyCommittedDsfAsync(
        string ffprobe,
        string path,
        long expectedFileSize,
        long expectedPayloadBytes,
        string expectedArtworkSha256,
        CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A report-listed DSF track is missing.", path);
        if (new FileInfo(path).Length != expectedFileSize)
            throw new InvalidDataException($"The final DSF file size differs from local staging: {path}");
        var payloadBytes = await DsfPayloadLengthAsync(path, token);
        if (payloadBytes != expectedPayloadBytes)
            throw new InvalidDataException($"The final DSD payload size differs from local verification: {path}");
        var output = await RunProcessAsync(ffprobe,
            ["-v", "error", "-show_streams", "-show_format", "-of", "json", path], Path.GetDirectoryName(ffprobe)!, null, token);
        using var document = JsonDocument.Parse(output);
        var streams = document.RootElement.GetProperty("streams");
        var audio = streams.EnumerateArray().FirstOrDefault(stream => Text(stream, "codec_type") == "audio");
        var codec = audio.ValueKind == JsonValueKind.Undefined ? null : Text(audio, "codec_name");
        if (codec is null || !codec.StartsWith("dsd", StringComparison.OrdinalIgnoreCase) || codec.Contains("pcm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The final file does not contain a native DSD stream: {path}");
        using var tagged = TagFile.Create(path);
        if (string.IsNullOrWhiteSpace(tagged.Tag.Title) || string.IsNullOrWhiteSpace(tagged.Tag.Album) ||
            string.IsNullOrWhiteSpace(tagged.Tag.FirstPerformer) || string.IsNullOrWhiteSpace(tagged.Tag.FirstAlbumArtist) ||
            string.IsNullOrWhiteSpace(tagged.Tag.FirstGenre) || tagged.Tag.Track == 0 || tagged.Tag.TrackCount == 0 ||
            tagged.Tag.Disc == 0 || tagged.Tag.DiscCount == 0 || tagged.Tag.Year == 0 || tagged.Tag.Pictures.Length == 0)
            throw new InvalidDataException($"Required DSF tags or embedded artwork are missing from the final file: {path}");
        var artworkSha256 = InMemoryArtworkService.ReadFrontCoverSha256(path);
        if (!string.Equals(artworkSha256, expectedArtworkSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The final DSF embedded artwork differs from local staging: {path}");
    }

    internal static SacdLayout ParseLayout(string output)
    {
        var albumTitle = Matches(output, "^\\s*Title:\\s*(?<value>.+?)\\s*$").Select(match => match.Groups["value"].Value.Trim()).LastOrDefault();
        var albumArtist = Matches(output, "^\\s*Artist:\\s*(?<value>.+?)\\s*$").Select(match => match.Groups["value"].Value.Trim()).LastOrDefault();
        var catalog = MatchValue(output, "^\\s*Album Catalog Number:\\s*(?<value>.+?)\\s*$") ?? MatchValue(output, "^\\s*Disc Catalog Number:\\s*(?<value>.+?)\\s*$");
        var creationDate = MatchValue(output, "^\\s*Creation date:\\s*(?<value>.+?)\\s*$");

        var areaStarts = AreaStart().Matches(output).Cast<Match>().ToArray();
        var areas = new List<SacdArea>();
        for (var areaIndex = 0; areaIndex < areaStarts.Length; areaIndex++)
        {
            var start = areaStarts[areaIndex].Index;
            var end = areaIndex + 1 < areaStarts.Length ? areaStarts[areaIndex + 1].Index : output.Length;
            var block = output[start..end];
            var speakerConfig = MatchValue(block, "^\\s*Speaker config:\\s*(?<value>.+?)\\s*$")
                ?? throw new InvalidDataException("A SACD area has no speaker configuration.");
            var trackCountText = MatchValue(block, "^\\s*Track Count:\\s*(?<value>\\d+)\\s*$")
                ?? throw new InvalidDataException("A SACD area has no track count.");
            var trackCount = int.Parse(trackCountText, CultureInfo.InvariantCulture);
            if (trackCount <= 0) throw new InvalidDataException("A SACD area has no playable tracks.");
            var totalPlayTime = MatchValue(block, "^\\s*Total play time:\\s*(?<value>[^\\r\\n]+)") ?? string.Empty;
            var titles = Matches(block, "^\\s*Title\\[(?<index>\\d+)\\]:\\s*(?<value>.*?)\\s*$").ToDictionary(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture), match => match.Groups["value"].Value.Trim());
            var performers = Matches(block, "^\\s*Performer\\[(?<index>\\d+)\\]:\\s*(?<value>.*?)\\s*$").ToDictionary(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture), match => match.Groups["value"].Value.Trim());
            var durations = Matches(block, "^\\s*Duration:\\s*(?<value>\\d+:\\d+:\\d+)").Select(match => ParseTimeCode(match.Groups["value"].Value)).ToArray();
            var isrcs = ParseIsrcs(block);
            if (titles.Count is > 0 && titles.Count != trackCount || durations.Length != trackCount)
                throw new InvalidDataException($"The SACD area lists {trackCount} tracks but its title/duration table is incomplete.");
            SacdTrack[] tracks;
            if (titles.Count == 0)
            {
                var sourceIndexOffset = isrcs.ContainsKey(0) ? 0 : 1;
                tracks = Enumerable.Range(0, trackCount).Select(trackIndex => new SacdTrack(
                    trackIndex + 1,
                    string.Empty,
                    performers.TryGetValue(trackIndex + sourceIndexOffset, out var performer) ? performer : albumArtist ?? string.Empty,
                    durations[trackIndex],
                    isrcs.GetValueOrDefault(trackIndex + sourceIndexOffset))).ToArray();
            }
            else
            {
                var titleIndexes = titles.Keys.OrderBy(index => index).ToArray();
                var firstTitleIndex = titleIndexes[0];
                if (firstTitleIndex is not 0 and not 1 ||
                    !titleIndexes.SequenceEqual(Enumerable.Range(firstTitleIndex, trackCount)))
                    throw new InvalidDataException($"The SACD area has unsupported or noncontiguous title indexes: {string.Join(", ", titleIndexes)}.");
                tracks = titleIndexes.Select((sourceIndex, trackIndex) => new SacdTrack(
                    trackIndex + 1,
                    titles[sourceIndex],
                    performers.TryGetValue(sourceIndex, out var performer) ? performer : albumArtist ?? string.Empty,
                    durations[trackIndex],
                    isrcs.GetValueOrDefault(sourceIndex))).ToArray();
            }
            areas.Add(new(speakerConfig.Contains("2 Channel", StringComparison.OrdinalIgnoreCase), speakerConfig, totalPlayTime, tracks));
        }
        return new(albumTitle ?? string.Empty, albumArtist ?? string.Empty, catalog?.Trim(), creationDate, areas);
    }

    internal static SacdLocalIdentity ResolveLocalIdentity(ScanResult scan, SacdLayout layout)
    {
        var checksumCandidates = scan.Media
            .Where(item => Path.GetExtension(item.Path) is { } extension &&
                           (extension.Equals(".md5", StringComparison.OrdinalIgnoreCase) ||
                            extension.Equals(".sfv", StringComparison.OrdinalIgnoreCase)))
            .Select(item => TryParseArtistAlbum(Path.GetFileNameWithoutExtension(item.Path)))
            .Where(identity => identity is not null)
            .Select(identity => identity!.Value)
            .GroupBy(identity => $"{IdentityKey(identity.Artist)}\0{IdentityKey(identity.Album)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
        if (checksumCandidates.Length > 1)
            throw new InvalidDataException("Checksum filenames disagree about the album artist or title; automatic SACD identity fallback is ambiguous.");

        var folderText = LeadingYearPrefix().Replace(scan.AlbumName, string.Empty).Trim();
        while (TrailingEditionBlock().IsMatch(folderText))
            folderText = TrailingEditionBlock().Replace(folderText, string.Empty).Trim();
        var folderIdentity = TryParseArtistAlbum(folderText);
        var checksum = checksumCandidates.FirstOrDefault();
        return new(
            checksum.Artist,
            checksum.Album,
            folderIdentity?.Artist,
            folderIdentity?.Album ?? Nonempty(folderText));
    }

    internal static SacdLayout ApplyAlbumIdentity(
        SacdLayout layout,
        SacdLocalIdentity local,
        ExternalAlbumIdentity? catalogIdentity)
    {
        if (catalogIdentity is not null &&
            ((Nonempty(layout.AlbumTitle) is { } discTitle && !IdentityEquivalent(catalogIdentity.Album, discTitle)) ||
             (Nonempty(layout.AlbumArtist) is { } discArtist && !IdentityEquivalent(catalogIdentity.Artist, discArtist))))
            throw new InvalidDataException(
                "The exact catalog-number match disagrees with SACD disc text; automatic release identification is ambiguous.");
        if (catalogIdentity is not null && local.ChecksumArtist is not null && local.ChecksumAlbum is not null &&
            (!IdentityEquivalent(catalogIdentity.Artist, local.ChecksumArtist) ||
             !IdentityEquivalent(catalogIdentity.Album, local.ChecksumAlbum)))
            throw new InvalidDataException(
                "The exact catalog-number match disagrees with the checksum filename artist/title; automatic SACD identity fallback is ambiguous.");

        var album = Nonempty(layout.AlbumTitle) ?? catalogIdentity?.Album ?? local.ChecksumAlbum ?? local.FolderAlbum ?? string.Empty;
        var artist = Nonempty(layout.AlbumArtist) ?? catalogIdentity?.Artist ?? local.ChecksumArtist ?? local.FolderArtist ?? string.Empty;
        var sources = new List<string>();
        if (Nonempty(layout.AlbumTitle) is not null || Nonempty(layout.AlbumArtist) is not null)
            sources.Add("SACD disc text");
        if (catalogIdentity is not null &&
            (Nonempty(layout.AlbumTitle) is null || Nonempty(layout.AlbumArtist) is null))
            sources.Add($"exact catalog-number match: {catalogIdentity.Source}");
        if ((Nonempty(layout.AlbumTitle) is null || Nonempty(layout.AlbumArtist) is null) &&
            (local.ChecksumAlbum is not null || local.ChecksumArtist is not null))
            sources.Add(catalogIdentity is null ? "checksum filename" : "checksum filename corroboration");
        if (Nonempty(layout.AlbumTitle) is null && local.FolderAlbum is not null)
            sources.Add(catalogIdentity is null && local.ChecksumAlbum is null
                ? "album folder name"
                : "album folder-name corroboration");
        return layout with
        {
            AlbumTitle = album,
            AlbumArtist = artist,
            CatalogNumber = catalogIdentity?.CatalogNumber ?? layout.CatalogNumber,
            IdentitySources = sources.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
        };
    }

    internal static SacdLayout ApplyExternalTrackListing(
        SacdLayout layout,
        ExternalAlbumIdentity? catalogIdentity,
        ExternalAlbumMetadata external)
    {
        var requiresTrackTitles = layout.Areas.Any(area => area.Tracks.Any(track => string.IsNullOrWhiteSpace(track.Title)));
        IReadOnlyList<string> fallbackTitles = [];
        string? listingSource = null;
        if (requiresTrackTitles && catalogIdentity?.TrackTitles.Count > 0)
        {
            fallbackTitles = catalogIdentity.TrackTitles;
            listingSource = catalogIdentity.Source;
        }
        else if (requiresTrackTitles && external.TrackTitles.Count > 0)
        {
            if (Nonempty(layout.CatalogNumber) is { } catalogNumber &&
                !ExternalMetadataService.CatalogsEquivalent(catalogNumber, external.CatalogNumber))
                throw new InvalidDataException(
                    "The external track listing does not prove the exact SACD catalog number; track-title fallback was rejected.");
            fallbackTitles = external.TrackTitles;
            listingSource = external.Sources.FirstOrDefault(source => source.Contains("musicbrainz.org/release/", StringComparison.OrdinalIgnoreCase));
        }

        if (requiresTrackTitles && fallbackTitles.Count == 0)
            throw new InvalidDataException(
                "The SACD disc text has no track titles, and no exact external release supplied a matching track listing.");
        if (requiresTrackTitles && layout.Areas.Any(area => area.Tracks.Count != fallbackTitles.Count))
            throw new InvalidDataException(
                "The external track listing count does not match every SACD audio area; track-title fallback was rejected.");

        var areas = layout.Areas.Select(area => area with
        {
            Tracks = area.Tracks.Select((track, index) => track with
            {
                Title = string.IsNullOrWhiteSpace(track.Title) ? fallbackTitles[index] : track.Title,
                Performer = string.IsNullOrWhiteSpace(track.Performer) ? layout.AlbumArtist : track.Performer
            }).ToArray()
        }).ToArray();
        var sources = (layout.IdentitySources ?? [])
            .Concat(listingSource is null ? [] : [$"external track listing: {listingSource}"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return layout with { Areas = areas, IdentitySources = sources };
    }

    private static (string Artist, string Album)? TryParseArtistAlbum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0 || separator + 3 >= value.Length) return null;
        var artist = Nonempty(value[..separator]);
        var album = Nonempty(value[(separator + 3)..]);
        return artist is null || album is null ? null : (artist, album);
    }

    private static bool IdentityEquivalent(string left, string right) =>
        IdentityKey(left).Equals(IdentityKey(right), StringComparison.Ordinal);

    private static string IdentityKey(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark && char.IsLetterOrDigit(character))
                builder.Append(char.ToLowerInvariant(character));
        return builder.ToString();
    }

    private static IReadOnlyDictionary<int, string> ParseIsrcs(string block)
    {
        var values = new Dictionary<int, string>();
        foreach (var match in Matches(block, "^\\s*ISRC Track \\[(?<index>\\d+)\\]:\\s*\\r?\\n\\s*Country:\\s*(?<country>[^,]+),\\s*Owner:\\s*(?<owner>[^,]+),\\s*Year:\\s*(?<year>[^,]+),\\s*Designation:\\s*(?<designation>\\S+)"))
        {
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            values[index] = string.Concat(
                match.Groups["country"].Value,
                match.Groups["owner"].Value,
                match.Groups["year"].Value,
                match.Groups["designation"].Value).Replace(" ", string.Empty);
        }
        foreach (var match in Matches(block, "^\\s*ISRC\\[(?<index>\\d+)\\]:\\s*(?<value>[A-Z0-9]{12})(?:\\s|$)"))
        {
            var index = int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture);
            values[index] = match.Groups["value"].Value;
        }
        return values;
    }

    private static IReadOnlyList<string> FindDsfFiles(string root) => Directory.EnumerateFiles(root, "*.dsf", SearchOption.AllDirectories)
        .OrderBy(path => NumericSortKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static string[] ExtractionArguments(bool isStereo, string iso, string outputRoot) =>
        [isStereo ? "-2" : "-m", "-s", "-c", "-i", iso, "-y", outputRoot];

    private static string NumericSortKey(string value) => NumberAtStart().Replace(value, match => match.Value.PadLeft(8, '0'));
    private static ResolvedYears ResolveYears(string albumName, string? creationDate)
    {
        var values = Year().Matches(albumName).Select(match => match.Value).ToList();
        if (values.Count == 0 && creationDate is not null) values.AddRange(Year().Matches(creationDate).Select(match => match.Value));
        var years = values.Select(value => int.TryParse(value, CultureInfo.InvariantCulture, out var year) ? year : 0).Where(year => year > 0).ToArray();
        if (years.Length == 0)
            throw new InvalidDataException("The album year is missing from the folder and SACD disc text.");
        return new(years[0], years.Length > 1 && years[^1] != years[0] ? years[^1] : null);
    }

    private static ResolvedDsdMetadata ResolveMetadata(string albumRoot, SacdLayout layout, ResolvedYears years, ExternalAlbumMetadata external)
    {
        var folderGenre = LibraryFolderMetadata.InferGenre(albumRoot);
        var genre = external.Genre ?? folderGenre ?? "Unknown";
        var catalogNumber = Nonempty(layout.CatalogNumber) ?? Nonempty(external.CatalogNumber);
        var originalDate = Nonempty(external.OriginalDate) ?? years.Original.ToString(CultureInfo.InvariantCulture);
        var releaseDate = Nonempty(external.ReleaseDate) ?? (years.Edition ?? years.Original).ToString(CultureInfo.InvariantCulture);
        var missing = new List<string>();
        if (genre.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) missing.Add("GENRE");
        if (Nonempty(external.Label) is null) missing.Add("LABEL");
        if (catalogNumber is null) missing.Add("CATALOGNUMBER");
        if (Nonempty(external.Barcode) is null) missing.Add("BARCODE");
        if (Nonempty(external.ReleaseCountry) is null) missing.Add("RELEASECOUNTRY");

        var warnings = external.Warnings.ToList();
        if (genre.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            warnings.Add("Genre remained unresolved after Discogs, MusicBrainz, and Apple Music lookup; an explicit Unknown placeholder was written so extraction could continue.");
        return new(
            years.Original,
            genre,
            external.GenreSourceType ?? (folderGenre is not null ? "recognized_library_genre_folder" : "unresolved_placeholder"),
            external.GenreConfidence ?? (folderGenre is not null ? "high" : "none"),
            external.GenreRationale ?? (folderGenre is not null
                ? "A recognized library category supplied genre only; it was not used as artist metadata."
                : "No sufficiently exact metadata source supplied a conservative genre."),
            originalDate,
            releaseDate,
            Nonempty(external.Label),
            catalogNumber,
            Nonempty(external.Barcode),
            Nonempty(external.ReleaseCountry),
            external.Sources,
            warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static void SetUserText(TagLib.Id3v2.Tag tag, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var frame = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, true);
        frame.Text = [value.Trim()];
    }

    private static bool UserTextEquals(TagLib.Id3v2.Tag tag, string name, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        var actual = TagLib.Id3v2.UserTextInformationFrame.Get(tag, name, false)?.Text?.FirstOrDefault();
        return actual?.Equals(expected.Trim(), StringComparison.OrdinalIgnoreCase) == true;
    }

    private static async Task WriteGapManifestAsync(string jobDirectory, IReadOnlyList<string> missing, IReadOnlyList<string> evidence, CancellationToken token)
    {
        var root = new JsonObject
        {
            ["split_completed"] = true,
            ["requires_research"] = missing.Count > 0,
            ["missing_fields"] = new JsonArray(missing.Select(value => JsonValue.Create(value)).ToArray()),
            ["local_evidence"] = new JsonArray(evidence.Select(value => JsonValue.Create(value)).ToArray())
        };
        await AtomicWriteAsync(MetadataGapService.GetPath(jobDirectory), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim().TrimEnd('.');
        cleaned = Whitespace().Replace(cleaned, " ");
        if (cleaned.Length == 0) cleaned = "Untitled";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned))) cleaned = "_" + cleaned;
        return cleaned.Length <= 120 ? cleaned : cleaned[..120].TrimEnd();
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        Action<int>? progress, CancellationToken token, bool allowNonzero = false)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        using var registration = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = new StringBuilder();
        var lastReportedPercent = -1;
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync(token) is { } line)
            {
                output.AppendLine(line);
                var match = ExtractionPercent().Match(line);
                if (match.Success && int.TryParse(match.Groups["value"].Value, out var percent) &&
                    Interlocked.Exchange(ref lastReportedPercent, percent) != percent)
                    progress?.Invoke(percent);
            }
        }
        var stdout = ReadAsync(process.StandardOutput);
        var stderr = ReadAsync(process.StandardError);
        await process.WaitForExitAsync(token);
        await Task.WhenAll(stdout, stderr);
        if (!allowNonzero && process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}: {output.ToString().Trim()}");
        return output.ToString();
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

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static string CommandText(string executable, IEnumerable<string> arguments) =>
        string.Join(" ", new[] { Quote(executable) }.Concat(arguments.Select(Quote)));
    private static string Quote(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
    private static string JsonPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int ParseInt(JsonElement element, string name) => element.TryGetProperty(name, out var value) && int.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var result) ? result : 0;
    private static TimeSpan ParseDuration(JsonElement root, JsonElement audio)
    {
        var value = Text(audio, "duration");
        if (value is null && root.TryGetProperty("format", out var format)) value = Text(format, "duration");
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero;
    }
    private static string? MatchValue(string text, string pattern) => Regex.Match(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase).Groups["value"].Value.Trim() is { Length: > 0 } value ? value : null;
    private static IEnumerable<Match> Matches(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase).Cast<Match>();
    private static TimeSpan ParseTimeCode(string value)
    {
        var parts = value.Split(':').Select(part => int.Parse(part, CultureInfo.InvariantCulture)).ToArray();
        return TimeSpan.FromSeconds(parts[0] * 60 + parts[1] + parts[2] / 75d);
    }
    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) => new(phase, percent, "running", detail, DateTimeOffset.UtcNow);

    internal sealed record DsfProbe(int SampleRate, int Channels, string ChannelLayout, TimeSpan Duration);
    internal sealed record SacdTrack(int Number, string Title, string Performer, TimeSpan Duration, string? Isrc);
    internal sealed record SacdArea(bool IsStereo, string SpeakerConfig, string TotalPlayTime, IReadOnlyList<SacdTrack> Tracks)
    {
        public string DisplayName => IsStereo ? "Stereo" : "Multichannel";
    }
    internal sealed record SacdLayout(
        string AlbumTitle,
        string AlbumArtist,
        string? CatalogNumber,
        string? CreationDate,
        IReadOnlyList<SacdArea> Areas,
        IReadOnlyList<string>? IdentitySources = null);
    internal sealed record SacdLocalIdentity(
        string? ChecksumArtist,
        string? ChecksumAlbum,
        string? FolderArtist,
        string? FolderAlbum);
    private sealed record ResolvedYears(int Original, int? Edition);
    internal sealed record ResolvedDsdMetadata(
        int OriginalYear,
        string Genre,
        string GenreSourceType,
        string GenreConfidence,
        string GenreRationale,
        string OriginalDate,
        string ReleaseDate,
        string? Label,
        string? CatalogNumber,
        string? Barcode,
        string? ReleaseCountry,
        IReadOnlyList<string> Sources,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<string> MissingFields);

    [GeneratedRegex("^\\s*Area Information \\[\\d+\\]:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AreaStart();
    [GeneratedRegex("^\\d+")]
    private static partial Regex NumberAtStart();
    [GeneratedRegex("^\\s*(?:\\(\\s*)?(?:19|20)\\d{2}(?:\\s*\\))?\\s*[-_.]+\\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingYearPrefix();
    [GeneratedRegex("\\s*(?:\\([^()]*(?:(?:19|20)\\d{2}|sacd|dsd|iso|remaster|mfsl|shm|bit|khz)[^()]*\\)|\\[[^\\[\\]]*(?:(?:19|20)\\d{2}|sacd|dsd|iso|remaster|mfsl|shm|bit|khz)[^\\[\\]]*\\])\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingEditionBlock();
    [GeneratedRegex("(?:19|20)\\d{2}")]
    private static partial Regex Year();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("Total:\\s*(?<value>\\d+)%", RegexOptions.IgnoreCase)]
    private static partial Regex ExtractionPercent();
}
