using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
        var iso = HostStagingService.SafeCombine(staged.AlbumRoot, source.RelativePath);
        var workingDirectory = Path.GetDirectoryName(staged.SacdExtractPath)!;
        var toolHash = await HostStagingService.Sha256Async(staged.SacdExtractPath, token);
        var versionOutput = await RunProcessAsync(staged.SacdExtractPath, ["-v"], workingDirectory, null, token, allowNonzero: false);
        var version = versionOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Contains("sacd_extract client", StringComparison.OrdinalIgnoreCase))?.Trim()
            ?? "sacd_extract version not reported";

        progress.Report(Snapshot(JobPhase.Processing, 19, "Reading the verified local SACD layout."));
        var layoutCommand = CommandText(staged.SacdExtractPath, ["-P", "-i", iso]);
        var layoutOutput = await RunProcessAsync(staged.SacdExtractPath, ["-P", "-i", iso], workingDirectory, null, token);
        var layoutPath = Path.Combine(staged.AlbumRoot, "sacd_extract-layout.txt");
        await File.WriteAllTextAsync(layoutPath, layoutOutput, new UTF8Encoding(false), token);
        var layout = ParseLayout(layoutOutput);
        if (layout.Areas.Count == 0) throw new InvalidDataException("sacd_extract reported no playable SACD areas.");

        var years = ResolveYears(scan.AlbumName, layout.CreationDate);
        progress.Report(Snapshot(JobPhase.Processing, 20, "Searching Discogs, MusicBrainz, and Apple Music for missing SACD metadata; lookup failures will not stop extraction."));
        var external = await _externalMetadata.ResolveAsync(new(
            layout.AlbumTitle,
            layout.AlbumArtist,
            layout.Areas.Max(area => area.Tracks.Count),
            years.Original,
            years.Edition,
            layout.CatalogNumber), token);
        var metadata = ResolveMetadata(scan.AlbumRoot, layout, years, external);
        var cover = await PrepareCoverAsync(staged.AlbumRoot, token);
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
            var areaFlag = area.IsStereo ? "-2" : "-m";
            var primaryArguments = new[] { areaFlag, "-s", "-c", "-i", iso, "-o", primaryRoot };
            var independentArguments = new[] { areaFlag, "-s", "-c", "-i", iso, "-o", independentRoot };

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
                var primaryHash = await HostStagingService.Sha256Async(primary, token);
                var independentHash = await HostStagingService.Sha256Async(independent, token);
                if (new FileInfo(primary).Length != new FileInfo(independent).Length ||
                    !primaryHash.Equals(independentHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Independent extraction mismatch for {area.DisplayName} track {track.Number:00}.");

                var outputName = $"{track.Number:00} - {SafeFileName(track.Title)}.dsf";
                var finalTrack = Path.Combine(finalAreaRoot, outputName);
                File.Move(primary, finalTrack, overwrite: false);
                var payloadBefore = await DsfPayloadSha256Async(finalTrack, token);
                await WriteTagsAsync(finalTrack, cover.Path, layout, area, track, metadata, token);
                var payloadAfter = await DsfPayloadSha256Async(finalTrack, token);
                if (!payloadBefore.Equals(payloadAfter, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"DSD audio payload changed while tagging {areaFolderName}/{outputName}.");

                var probe = await VerifyDsfAsync(staged.FfprobePath, finalTrack, layout, area, track, metadata, cover.Path, token);
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
                    ["untagged_sha256"] = primaryHash,
                    ["independent_extraction_sha256"] = independentHash,
                    ["dsd_payload_sha256_before_tags"] = payloadBefore,
                    ["dsd_payload_sha256_after_tags"] = payloadAfter,
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

        progress.Report(Snapshot(JobPhase.Tagging, 45, "DSF tags and front-cover artwork were written; every DSD payload hash is unchanged."));
        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        var report = new JsonObject
        {
            ["schema_version"] = "1.0",
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
                ["size"] = source.Size,
                ["sha256"] = source.Sha256
            },
            ["extraction_tool"] = version,
            ["extraction_tool_sha256"] = toolHash,
            ["disc_layout_command"] = layoutCommand,
            ["disc_layout_file"] = "sacd_extract-layout.txt",
            ["metadata_sources"] = new JsonArray(new[] { "SACD disc text and track table", "album folder", cover.Source }
                .Concat(metadata.Sources).Select(value => JsonValue.Create(value)).ToArray()),
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
                ["status"] = metadata.MissingFields.Count == 0 ? "complete" : metadata.Sources.Count > 0 ? "partial" : "unresolved",
                ["nonblocking"] = true,
                ["sources"] = new JsonArray(metadata.Sources.Select(value => JsonValue.Create(value)).ToArray()),
                ["warnings"] = new JsonArray(metadata.Warnings.Select(value => JsonValue.Create(value)).ToArray())
            },
            ["cover"] = new JsonObject
            {
                ["file"] = "cover.jpg",
                ["source"] = cover.Source
            },
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
                ["status"] = metadata.MissingFields.Count == 0 ? "passed" : "incomplete",
                ["method"] = "Two deterministic untagged extractions per reported area; exact per-track size/SHA-256 equality; DSF/DSD probe and signal checks; unchanged DSD payload through ID3/artwork tagging.",
                ["independent_extraction"] = "passed",
                ["tag_payload_verification"] = "passed",
                ["audio_and_tags"] = "passed",
                ["source_deletion_eligible"] = metadata.MissingFields.Count == 0,
                ["sources_deleted"] = false,
                ["errors"] = new JsonArray(),
                ["warnings"] = new JsonArray(metadata.Warnings.Select(value => JsonValue.Create(value)).ToArray()),
                ["missing_metadata"] = new JsonArray(metadata.MissingFields.Select(value => JsonValue.Create(value)).ToArray())
            },
            ["job"] = new JsonObject
            {
                ["identifier"] = Path.GetFileName(staged.JobDirectory),
                ["local_staging_used"] = true,
                ["staging_path"] = staged.JobDirectory,
                ["processor"] = "local_sacd_extract_sequential_areas"
            }
        };
        report["work_status"] = metadata.MissingFields.Count == 0 ? "complete" : "incomplete";
        if (metadata.MissingFields.Count > 0)
        {
            report["incomplete_work"] = new JsonObject
            {
                ["reason"] = "External and local metadata sources did not resolve every desired field. Verified DSF tracks remain usable.",
                ["repairable_without_source_image"] = true,
                ["issues"] = new JsonArray(metadata.MissingFields.Select(value => JsonValue.Create($"Missing metadata: {value}")).ToArray())
            };
        }
        await AtomicWriteAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
        var evidence = new[] { "SACD disc text and track table", $"local artwork: {cover.Source}" }.Concat(metadata.Sources).ToArray();
        await WriteGapManifestAsync(staged.JobDirectory, metadata.MissingFields, evidence, token);
        progress.Report(Snapshot(JobPhase.Tagging, 48, $"SACD extraction and deletion-grade local verification completed for {totalTracks} tracks."));
        return new(totalTracks, reportPath, new MetadataGapResult(true, metadata.MissingFields.Count > 0, metadata.MissingFields, evidence));
    }

    internal static async Task<string> DsfPayloadSha256Async(string path, CancellationToken token = default)
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
                return await HashRangeAsync(stream, chunkSize - 12, token);
            stream.Position += chunkSize - 12;
        }
        throw new InvalidDataException($"The DSF audio data chunk is missing from {path}.");
    }

    private static async Task WriteTagsAsync(
        string path, string coverPath, SacdLayout layout, SacdArea area, SacdTrack track, ResolvedDsdMetadata metadata, CancellationToken token)
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
            new TagLib.Picture(coverPath)
            {
                Type = TagLib.PictureType.FrontCover,
                Description = "Cover (front)",
                MimeType = "image/jpeg"
            }
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
        string ffprobe, string path, SacdLayout layout, SacdArea area, SacdTrack track, ResolvedDsdMetadata metadata, string coverPath,
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
        if (!File.Exists(coverPath)) throw new FileNotFoundException("The album cover is missing after DSF tagging.", coverPath);
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
        string ffprobe, string path, string expectedPayloadSha256, CancellationToken token = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A report-listed DSF track is missing.", path);
        var payload = await DsfPayloadSha256Async(path, token);
        if (!payload.Equals(expectedPayloadSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The final DSD payload differs from local verification: {path}");
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
    }

    private static SacdLayout ParseLayout(string output)
    {
        var albumTitle = Matches(output, "^\\s*Title:\\s*(?<value>.+?)\\s*$").Select(match => match.Groups["value"].Value.Trim()).LastOrDefault();
        var albumArtist = Matches(output, "^\\s*Artist:\\s*(?<value>.+?)\\s*$").Select(match => match.Groups["value"].Value.Trim()).LastOrDefault();
        var catalog = MatchValue(output, "^\\s*Album Catalog Number:\\s*(?<value>.+?)\\s*$") ?? MatchValue(output, "^\\s*Disc Catalog Number:\\s*(?<value>.+?)\\s*$");
        var creationDate = MatchValue(output, "^\\s*Creation date:\\s*(?<value>.+?)\\s*$");
        if (string.IsNullOrWhiteSpace(albumTitle) || string.IsNullOrWhiteSpace(albumArtist))
            throw new InvalidDataException("The SACD disc text has no album title or artist.");

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
            var totalPlayTime = MatchValue(block, "^\\s*Total play time:\\s*(?<value>[^\\r\\n]+)") ?? string.Empty;
            var titles = Matches(block, "^\\s*Title\\[(?<index>\\d+)\\]:\\s*(?<value>.*?)\\s*$").ToDictionary(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture), match => match.Groups["value"].Value.Trim());
            var performers = Matches(block, "^\\s*Performer\\[(?<index>\\d+)\\]:\\s*(?<value>.*?)\\s*$").ToDictionary(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture), match => match.Groups["value"].Value.Trim());
            var durations = Matches(block, "^\\s*Duration:\\s*(?<value>\\d+:\\d+:\\d+)").Select(match => ParseTimeCode(match.Groups["value"].Value)).ToArray();
            var isrcs = Matches(block, "^\\s*ISRC Track \\[(?<index>\\d+)\\]:\\s*\\r?\\n\\s*Country:\\s*(?<country>[^,]+),\\s*Owner:\\s*(?<owner>[^,]+),\\s*Year:\\s*(?<year>[^,]+),\\s*Designation:\\s*(?<designation>\\S+)")
                .ToDictionary(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture), match => string.Concat(match.Groups["country"].Value, match.Groups["owner"].Value, match.Groups["year"].Value, match.Groups["designation"].Value).Replace(" ", string.Empty));
            if (titles.Count != trackCount || durations.Length != trackCount)
                throw new InvalidDataException($"The SACD area lists {trackCount} tracks but its title/duration table is incomplete.");
            var tracks = Enumerable.Range(0, trackCount).Select(index => new SacdTrack(index + 1, titles[index], performers.GetValueOrDefault(index, albumArtist), durations[index], isrcs.GetValueOrDefault(index))).ToArray();
            areas.Add(new(speakerConfig.Contains("2 Channel", StringComparison.OrdinalIgnoreCase), speakerConfig, totalPlayTime, tracks));
        }
        return new(albumTitle, albumArtist, catalog?.Trim(), creationDate, areas);
    }

    private static async Task<LocalCover> PrepareCoverAsync(string albumRoot, CancellationToken token)
    {
        var candidates = Directory.EnumerateFiles(albumRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".jpg", ".jpeg" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .OrderBy(path => CoverRank(albumRoot, path)).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        if (candidates.Length == 0) throw new InvalidDataException("SACD extraction requires local JPEG cover artwork.");
        var source = candidates[0];
        var target = Path.Combine(albumRoot, "cover.jpg");
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase)) File.Copy(source, target, overwrite: false);
        await Task.CompletedTask;
        return new(target, $"local file: {JsonPath(HostStagingService.SafeRelative(albumRoot, source))}");
    }

    private static int CoverRank(string root, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("cover", StringComparison.OrdinalIgnoreCase) || name.Equals("folder", StringComparison.OrdinalIgnoreCase) || name.Equals("front", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) || name.Contains("cover", StringComparison.OrdinalIgnoreCase)) return 1;
        if (Path.GetDirectoryName(path)?.Contains($"{Path.DirectorySeparatorChar}Artwork", StringComparison.OrdinalIgnoreCase) == true) return 2;
        return Path.GetDirectoryName(path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? 3 : 4;
    }

    private static IReadOnlyList<string> FindDsfFiles(string root) => Directory.EnumerateFiles(root, "*.dsf", SearchOption.AllDirectories)
        .OrderBy(path => NumericSortKey(Path.GetFileName(path)), StringComparer.OrdinalIgnoreCase).ThenBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();

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
        var folderGenre = InferGenreFromFolders(albumRoot);
        var genre = folderGenre ?? external.Genre ?? "Unknown";
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
            warnings.Add("Genre remained unresolved after local-folder, Discogs, MusicBrainz, and Apple Music lookup; an explicit Unknown placeholder was written so extraction could continue.");
        return new(
            years.Original,
            genre,
            folderGenre is not null ? "inferred_from_library_folder" : external.GenreSourceType ?? "unresolved_placeholder",
            folderGenre is not null ? "high" : external.GenreConfidence ?? "none",
            folderGenre is not null ? "Album is stored beneath the matching library genre folder." : external.GenreRationale ?? "No sufficiently exact metadata source supplied a conservative genre.",
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

    private static string? InferGenreFromFolders(string albumRoot)
    {
        var mappings = new (string Needle, string Genre)[]
        {
            ("opera", "Opera"), ("classical", "Classical"), ("jazz", "Jazz"), ("rock", "Rock"),
            ("pop", "Pop"), ("folk", "Folk"), ("electronic", "Electronic"), ("soundtrack", "Soundtrack"),
            ("spoken word", "Spoken Word"), ("blues", "Blues"), ("country", "Country"), ("reggae", "Reggae"),
            ("metal", "Metal"), ("soul", "Soul"), ("funk", "Funk")
        };
        for (var directory = Directory.GetParent(albumRoot); directory is not null; directory = directory.Parent)
            foreach (var mapping in mappings)
                if (directory.Name.Contains(mapping.Needle, StringComparison.OrdinalIgnoreCase)) return mapping.Genre;
        return null;
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

    private static async Task<string> HashRangeAsync(Stream stream, long bytes, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        var remaining = bytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
            if (read == 0) throw new EndOfStreamException();
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
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
    internal sealed record SacdLayout(string AlbumTitle, string AlbumArtist, string? CatalogNumber, string? CreationDate, IReadOnlyList<SacdArea> Areas);
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
    private sealed record LocalCover(string Path, string Source);

    [GeneratedRegex("^\\s*Area Information \\[\\d+\\]:", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex AreaStart();
    [GeneratedRegex("^\\d+")]
    private static partial Regex NumberAtStart();
    [GeneratedRegex("(?:19|20)\\d{2}")]
    private static partial Regex Year();
    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
    [GeneratedRegex("Total:\\s*(?<value>\\d+)%", RegexOptions.IgnoreCase)]
    private static partial Regex ExtractionPercent();
}
