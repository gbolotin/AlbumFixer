using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

public sealed record LocalSplitResult(
    int Tracks,
    string ReportPath,
    MetadataGapResult Metadata);

public sealed class LocalFlacProcessor
{
    private static readonly Regex FileLine = new("^\\s*FILE\\s+(?:\"(?<quoted>[^\"]+)\"|(?<plain>\\S+))\\s+\\S+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TrackLine = new("^\\s*TRACK\\s+(?<number>\\d+)\\s+AUDIO\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex IndexLine = new("^\\s*INDEX\\s+(?<index>\\d+)\\s+(?<minute>\\d+):(?<second>\\d+):(?<frame>\\d+)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex FolderYear = new("^\\s*\\((?<year>\\d{4})\\)", RegexOptions.Compiled);
    private static readonly Regex EditionSuffix = new("\\s*\\[[^\\]]+\\]\\s*", RegexOptions.Compiled);
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
        if (scan.Mode != WorkflowMode.FlacCueSplit)
            throw new NotSupportedException("The deterministic local processor currently supports FLAC/APE + CUE image splits only.");

        var inputAlbumRoot = staged.InputAlbumRoot;
        progress.Report(Snapshot(JobPhase.Processing, 19, staged.SourceCacheUsed
            ? "Reading the verified Temp-cached CUE locally."
            : "Reading the fixed-disk CUE in place without a source-cache copy."));
        var cuePaths = Directory.EnumerateFiles(inputAlbumRoot, "*.cue", SearchOption.AllDirectories)
            .OrderBy(path => CueSortKey(inputAlbumRoot, path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => HostStagingService.SafeRelative(inputAlbumRoot, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (cuePaths.Length == 0)
            throw new InvalidOperationException("The local splitter requires at least one CUE sheet.");

        var artworkService = new InMemoryArtworkService();
        var preparedCover = await artworkService.PrepareLocalAsync(
            inputAlbumRoot,
            staged.FfmpegPath,
            staged.FfprobePath,
            ArtworkSelectionMode.Flac,
            token);
        var cover = preparedCover.Artwork;
        var inputs = new List<LocalInput>();
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < cuePaths.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var cue = await ParseCueAsync(cuePaths[index], token);
            var source = ResolveSource(inputAlbumRoot, cuePaths[index], cue.SourceFiles);
            if (!sources.Add(source))
                throw new InvalidDataException($"More than one CUE sheet references the same audio image: {HostStagingService.SafeRelative(inputAlbumRoot, source)}.");
            var probe = await ProbeAudioAsync(staged.FfprobePath, source, token);
            ValidateBoundaries(cue.Tracks, probe.SampleRate, probe.TotalSamples);
            inputs.Add(new(cue, source, probe, ResolveMetadata(scan, cue)));
        }

        var sourceDirectories = inputs
            .Select(input => Path.GetDirectoryName(input.Source)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var useCdFolders = inputs.Count > 1 && sourceDirectories.Length == 1;
        var discs = new List<LocalDisc>();
        for (var index = 0; index < inputs.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            var input = inputs[index];
            var discNumber = index + 1;
            var sourceDirectory = Path.GetDirectoryName(input.Source)!;
            var sourceDirectoryRelative = HostStagingService.SafeRelative(inputAlbumRoot, sourceDirectory);
            var stagedSourceDirectory = sourceDirectoryRelative.Equals(".", StringComparison.Ordinal)
                ? staged.AlbumRoot
                : HostStagingService.SafeCombine(staged.AlbumRoot, sourceDirectoryRelative);
            var outputRoot = useCdFolders ? Path.Combine(stagedSourceDirectory, $"CD{discNumber}") : stagedSourceDirectory;
            HostStagingService.SafeRelative(staged.AlbumRoot, outputRoot);
            var outputs = BuildOutputs(input.Cue.Tracks, outputRoot);
            EnsureOutputsAvailable(outputs, staged.AlbumRoot);
            Directory.CreateDirectory(outputRoot);

            progress.Report(Snapshot(JobPhase.Processing, 21,
                inputs.Count == 1
                    ? $"Splitting {outputs.Count} tracks into the album folder in one FFmpeg process."
                    : useCdFolders
                        ? $"Splitting CD{discNumber} of {inputs.Count} ({outputs.Count} tracks) in one FFmpeg process."
                        : $"Splitting disc {discNumber} of {inputs.Count} beside its source ({outputs.Count} tracks)."));
            await SplitOnceAsync(staged.FfmpegPath, input.Source, input.Probe.SampleRate, input.Cue.Tracks, outputs,
                input.Metadata, discNumber, inputs.Count, progress, token);
            if (cover is not null)
            {
                foreach (var output in outputs)
                {
                    using var tagged = TagLib.File.Create(output.Path);
                    tagged.Tag.Pictures = [InMemoryArtworkService.CreatePicture(cover)];
                    tagged.Save();
                }
            }
            discs.Add(new(discNumber, input.Cue, input.Source, outputs, input.Metadata));
        }

        IReadOnlyList<string> missing = discs
            .SelectMany(disc => FindMissing(disc.Metadata, disc.Cue.Tracks, cover is not null))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        progress.Report(Snapshot(JobPhase.Tagging, 36, cover is null
            ? "Local tags were written. Required artwork is missing and was deferred."
            : "Local tags and front-cover artwork were written during the split."));

        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        await WriteReportAsync(scan, staged, discs, cover, preparedCover.Issue, missing, reportPath, token);
        var evidence = LocalEvidence(scan, cuePaths, cover);
        await WriteGapManifestAsync(staged.JobDirectory, missing, evidence, token);
        var gapResult = new MetadataGapResult(true, missing.Count > 0, missing, evidence);

        progress.Report(Snapshot(JobPhase.Tagging, missing.Count > 0 ? 40 : 44, missing.Count > 0
            ? $"Local split complete. Only these required fields need metadata fallback: {string.Join(", ", missing)}."
            : "Local split, tags, and artwork are complete. Metadata research was skipped."));
        return new(discs.Sum(disc => disc.Outputs.Count), reportPath, gapResult);
    }

    private static string CueSortKey(string albumRoot, string path) =>
        Regex.Replace(HostStagingService.SafeRelative(albumRoot, path), "\\d+",
            match => match.Value.PadLeft(20, '0'));

    private static async Task<CueSheet> ParseCueAsync(string path, CancellationToken token)
    {
        var sheet = new CueSheet();
        CueTrack? current = null;
        foreach (var raw in await File.ReadAllLinesAsync(path, token))
        {
            var file = FileLine.Match(raw);
            if (file.Success)
            {
                var value = file.Groups["quoted"].Success ? file.Groups["quoted"].Value : file.Groups["plain"].Value;
                if (!string.IsNullOrWhiteSpace(value)) sheet.SourceFiles.Add(value.Trim());
                continue;
            }

            var track = TrackLine.Match(raw);
            if (track.Success)
            {
                current = new CueTrack(int.Parse(track.Groups["number"].Value, CultureInfo.InvariantCulture));
                sheet.Tracks.Add(current);
                continue;
            }

            var index = IndexLine.Match(raw);
            if (index.Success && current is not null && index.Groups["index"].Value == "01")
            {
                var minute = int.Parse(index.Groups["minute"].Value, CultureInfo.InvariantCulture);
                var second = int.Parse(index.Groups["second"].Value, CultureInfo.InvariantCulture);
                var frame = int.Parse(index.Groups["frame"].Value, CultureInfo.InvariantCulture);
                if (second >= 60 || frame >= 75) throw new InvalidDataException($"Invalid CUE INDEX time in track {current.Number:00}.");
                current.Index01Frames = ((long)minute * 60 + second) * 75 + frame;
                continue;
            }

            if (TryValue(raw, "REM DATE", out var date)) sheet.Date ??= date;
            else if (TryValue(raw, "REM GENRE", out var genre)) sheet.Genre ??= genre;
            else if (TryValue(raw, "REM COMPOSER", out var remComposer)) sheet.Composer ??= remComposer;
            else if (TryValue(raw, "TITLE", out var title))
            {
                if (current is null) sheet.AlbumTitle ??= title; else current.Title ??= title;
            }
            else if (TryValue(raw, "PERFORMER", out var performer))
            {
                if (current is null) sheet.AlbumPerformer ??= performer; else current.Performer ??= performer;
            }
            else if (TryValue(raw, "SONGWRITER", out var songwriter))
            {
                if (current is null) sheet.Composer ??= songwriter; else current.Composer ??= songwriter;
            }
        }

        sheet.SourceFiles = sheet.SourceFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sheet.SourceFiles.Count != 1)
            throw new InvalidDataException($"The local splitter supports one FLAC or APE image per CUE; this CUE names {sheet.SourceFiles.Count} source files.");
        if (sheet.Tracks.Count == 0) throw new InvalidDataException("The CUE sheet contains no AUDIO tracks.");
        if (sheet.Tracks.Any(track => track.Index01Frames is null)) throw new InvalidDataException("Every CUE track must contain an INDEX 01 boundary.");
        if (sheet.Tracks.Select(track => track.Number).Distinct().Count() != sheet.Tracks.Count)
            throw new InvalidDataException("The CUE sheet contains duplicate track numbers.");
        return sheet;
    }

    private static bool TryValue(string line, string directive, out string value)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Length > directive.Length && !char.IsWhiteSpace(trimmed[directive.Length])))
        {
            value = string.Empty;
            return false;
        }
        value = trimmed[directive.Length..].Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
        value = value.Trim();
        return true;
    }

    private static string ResolveSource(string albumRoot, string cuePath, IReadOnlyList<string> sourceFiles)
    {
        var candidate = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cuePath)!, sourceFiles[0]));
        HostStagingService.SafeRelative(albumRoot, candidate);
        if (!File.Exists(candidate)) throw new FileNotFoundException("The CUE source does not exist.", candidate);
        if (!CueAudioImagePolicy.IsSupportedPath(candidate))
            throw new NotSupportedException("The deterministic local CUE splitter accepts FLAC and APE image sources only.");
        return candidate;
    }

    private static async Task<AudioProbe> ProbeAudioAsync(string ffprobe, string source, CancellationToken token)
    {
        var output = await RunProcessAsync(ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,duration_ts", "-of", "json", source],
            null, token);
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidDataException("ffprobe found no audio stream in the staged FLAC/APE source.");
        var stream = streams[0];
        var sampleText = stream.TryGetProperty("sample_rate", out var rateNode) ? rateNode.GetString() : null;
        if (!int.TryParse(sampleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate) || sampleRate <= 0)
            throw new InvalidDataException("ffprobe did not return a valid source sample rate.");
        long? totalSamples = null;
        if (stream.TryGetProperty("duration_ts", out var durationNode))
        {
            if (durationNode.ValueKind == JsonValueKind.Number && durationNode.TryGetInt64(out var numeric)) totalSamples = numeric;
            else if (durationNode.ValueKind == JsonValueKind.String && long.TryParse(durationNode.GetString(), out var text)) totalSamples = text;
        }
        return new(sampleRate, totalSamples);
    }

    private static void ValidateBoundaries(IReadOnlyList<CueTrack> tracks, int sampleRate, long? totalSamples)
    {
        long previous = -1;
        for (var index = 0; index < tracks.Count; index++)
        {
            var cueFrames = index == 0 ? 0 : tracks[index].Index01Frames!.Value;
            var numerator = cueFrames * sampleRate;
            if (numerator % 75 != 0) throw new InvalidDataException("A CUE boundary does not align to a whole audio sample.");
            var sample = numerator / 75;
            if (sample <= previous) throw new InvalidDataException("CUE INDEX 01 boundaries are not strictly increasing.");
            if (totalSamples is not null && sample >= totalSamples.Value) throw new InvalidDataException("A CUE boundary lies beyond the source audio stream.");
            previous = sample;
        }
    }

    private static ResolvedMetadata ResolveMetadata(ScanResult scan, CueSheet cue)
    {
        var year = FolderYear.Match(scan.AlbumName);
        var album = Nonempty(cue.AlbumTitle) ?? CleanAlbumName(scan.AlbumName);
        var artist = Nonempty(cue.AlbumPerformer);
        var date = Nonempty(cue.Date) ?? (year.Success ? year.Groups["year"].Value : null);
        var folderGenre = LibraryFolderMetadata.InferGenre(scan.AlbumRoot);
        var genre = Nonempty(cue.Genre) ?? folderGenre;
        var sheetComposer = Nonempty(cue.Composer);
        if (sheetComposer is null)
        {
            var inferred = LocalTrackRepairProcessor.InferClassicalTrackComposers(
                album, scan.AlbumName, genre,
                cue.Tracks.Select(track => (track.Title, track.Title ?? string.Empty)).ToArray(),
                ClassicalMetadataPolicy.IsCompilationArtist(artist));
            for (var index = 0; index < cue.Tracks.Count; index++)
                if (Nonempty(cue.Tracks[index].Composer) is null && Nonempty(inferred[index]) is { } composer)
                    cue.Tracks[index].Composer = composer;
            var distinct = cue.Tracks
                .Where(track => ClassicalMetadataPolicy.RequiresComposer(
                    genre, track.Title, ClassicalMetadataPolicy.IsCompilationArtist(artist)))
                .Select(track => Nonempty(track.Composer))
                .Where(value => value is not null)
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (distinct.Length == 1) sheetComposer = distinct[0];
        }
        return new(album, artist, date, genre, sheetComposer, cue.Genre is null && folderGenre is not null);
    }

    private static string CleanAlbumName(string name)
    {
        var value = FolderYear.Replace(name, string.Empty);
        value = EditionSuffix.Replace(value, " ");
        return Regex.Replace(value, "\\s+", " ").Trim();
    }

    private static IReadOnlyList<TrackOutput> BuildOutputs(IReadOnlyList<CueTrack> tracks, string outputRoot)
    {
        var width = tracks.Count >= 100 ? 3 : 2;
        return tracks.Select(track =>
        {
            var displayTitle = Nonempty(track.Title) ?? $"Track {track.Number.ToString($"D{width}", CultureInfo.InvariantCulture)}";
            var file = $"{track.Number.ToString($"D{width}", CultureInfo.InvariantCulture)} - {SafeFileName(displayTitle)}.flac";
            return new TrackOutput(track, Path.Combine(outputRoot, file));
        }).ToArray();
    }

    private static void EnsureOutputsAvailable(IEnumerable<TrackOutput> outputs, string albumRoot)
    {
        foreach (var output in outputs)
        {
            if (File.Exists(output.Path) || Directory.Exists(output.Path))
                throw new IOException($"The final track path already exists and will not be overwritten: {HostStagingService.SafeRelative(albumRoot, output.Path)}");
        }
    }

    private static async Task SplitOnceAsync(
        string ffmpeg,
        string source,
        int sampleRate,
        IReadOnlyList<CueTrack> tracks,
        IReadOnlyList<TrackOutput> outputs,
        ResolvedMetadata metadata,
        int discNumber,
        int discTotal,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-n", "-i", source };

        var graph = new StringBuilder();
        if (tracks.Count == 1) graph.Append("[0:a:0]atrim=start_sample=0,asetpts=PTS-STARTPTS[t0]");
        else
        {
            graph.Append("[0:a:0]asplit=").Append(tracks.Count);
            for (var index = 0; index < tracks.Count; index++) graph.Append("[a").Append(index).Append(']');
            graph.Append(';');
            for (var index = 0; index < tracks.Count; index++)
            {
                var startFrames = index == 0 ? 0 : tracks[index].Index01Frames!.Value;
                var startSample = startFrames * sampleRate / 75;
                graph.Append("[a").Append(index).Append("]atrim=start_sample=").Append(startSample);
                if (index + 1 < tracks.Count)
                {
                    var endSample = tracks[index + 1].Index01Frames!.Value * sampleRate / 75;
                    graph.Append(":end_sample=").Append(endSample);
                }
                graph.Append(",asetpts=PTS-STARTPTS[t").Append(index).Append(']');
                if (index + 1 < tracks.Count) graph.Append(';');
            }
        }
        arguments.Add("-filter_complex"); arguments.Add(graph.ToString());

        for (var index = 0; index < outputs.Count; index++)
        {
            var track = outputs[index].Track;
            arguments.Add("-map"); arguments.Add($"[t{index}]");
            arguments.Add("-map_metadata"); arguments.Add("-1");
            arguments.Add("-c:a"); arguments.Add("flac");
            arguments.Add("-compression_level"); arguments.Add("5");
            AddMetadata(arguments, "TITLE", track.Title);
            AddMetadata(arguments, "ALBUM", metadata.Album);
            AddMetadata(arguments, "ARTIST", Nonempty(track.Performer) ?? metadata.AlbumArtist);
            AddMetadata(arguments, "ALBUMARTIST", metadata.AlbumArtist);
            AddMetadata(arguments, "TRACKNUMBER", $"{track.Number}/{tracks.Count}");
            AddMetadata(arguments, "TRACKTOTAL", tracks.Count.ToString(CultureInfo.InvariantCulture));
            AddMetadata(arguments, "DISCNUMBER", $"{discNumber}/{discTotal}");
            AddMetadata(arguments, "DISCTOTAL", discTotal.ToString(CultureInfo.InvariantCulture));
            AddMetadata(arguments, "DATE", metadata.Date);
            AddMetadata(arguments, "GENRE", metadata.Genre);
            AddMetadata(arguments, "COMPOSER", Nonempty(track.Composer) ?? metadata.Composer);
            arguments.Add(outputs[index].Path);
        }

        var pulse = 0;
        await RunProcessAsync(ffmpeg, arguments, line =>
        {
            if (!line.Equals("progress=continue", StringComparison.OrdinalIgnoreCase)) return;
            pulse++;
            var percent = Math.Min(34, 21 + pulse);
            progress.Report(Snapshot(JobPhase.Processing, percent, discTotal == 1
                ? $"FFmpeg is splitting {tracks.Count} tracks locally in one pass."
                : $"FFmpeg is splitting CD{discNumber} of {discTotal} locally in one pass."));
        }, token);
        foreach (var output in outputs)
            if (!File.Exists(output.Path) || new FileInfo(output.Path).Length == 0) throw new IOException($"FFmpeg did not create '{Path.GetFileName(output.Path)}'.");
    }

    private static void AddMetadata(ICollection<string> arguments, string key, string? value, string option = "-metadata")
    {
        value = Nonempty(value);
        if (value is null) return;
        arguments.Add(option);
        arguments.Add($"{key}={value}");
    }

    private static IReadOnlyList<string> FindMissing(ResolvedMetadata metadata, IReadOnlyList<CueTrack> tracks, bool hasCover)
    {
        var missing = new List<string>();
        if (tracks.Any(track => Nonempty(track.Title) is null)) missing.Add("TITLE");
        if (Nonempty(metadata.Album) is null) missing.Add("ALBUM");
        if (tracks.Any(track => Nonempty(track.Performer) is null && Nonempty(metadata.AlbumArtist) is null)) missing.Add("ARTIST");
        if (Nonempty(metadata.AlbumArtist) is null) missing.Add("ALBUMARTIST");
        if (Nonempty(metadata.Date) is null) missing.Add("DATE");
        if (Nonempty(metadata.Genre) is null) missing.Add("GENRE");
        if (tracks.Any(track => ClassicalMetadataPolicy.RequiresComposer(
                                    metadata.Genre, track.Title,
                                    ClassicalMetadataPolicy.IsCompilationArtist(metadata.AlbumArtist)) &&
                                Nonempty(track.Composer) is null && Nonempty(metadata.Composer) is null))
            missing.Add("COMPOSER");
        if (!hasCover) missing.Add("COVER");
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> LocalEvidence(ScanResult scan, IReadOnlyList<string> cuePaths, PreparedArtwork? cover)
    {
        var evidence = new List<string>
        {
            $"album folder: {scan.AlbumName}"
        };
        evidence.InsertRange(0, cuePaths.Select(path => $"CUE: {Path.GetFileName(path)}"));
        if (cover is not null) evidence.Add(cover.Source);
        if (scan.Media.Any(item => item.Kind == "Provenance")) evidence.Add("local rip log, booklet, playlist, or sidecar");
        return evidence;
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

    private static async Task WriteReportAsync(
        ScanResult scan,
        StagedJob staged,
        IReadOnlyList<LocalDisc> discs,
        PreparedArtwork? cover,
        string? coverIssue,
        IReadOnlyList<string> missing,
        string reportPath,
        CancellationToken token)
    {
        var discReports = new JsonArray();
        foreach (var disc in discs)
        {
            var tracks = new JsonArray();
            for (var index = 0; index < disc.Outputs.Count; index++)
            {
                var cueTrack = disc.Outputs[index].Track;
                tracks.Add(new JsonObject
                {
                    ["disc"] = disc.Number,
                    ["track"] = cueTrack.Number,
                    ["title"] = cueTrack.Title ?? string.Empty,
                    ["file"] = JsonPath(HostStagingService.SafeRelative(staged.AlbumRoot, disc.Outputs[index].Path))
                });
            }
            discReports.Add(new JsonObject
            {
                ["disc"] = disc.Number,
                ["source"] = JsonPath(HostStagingService.SafeRelative(staged.InputAlbumRoot, disc.Source)),
                ["tracks"] = tracks
            });
        }

        var metadata = discs[0].Metadata;
        var sourceType = CueAudioImagePolicy.SourceType(discs.Select(disc => disc.Source));
        var workflowId = CueAudioImagePolicy.WorkflowId(discs.Select(disc => disc.Source));

        var report = new JsonObject
        {
            ["schema_version"] = "2.0",
            ["album"] = metadata.Album ?? string.Empty,
            ["artist"] = metadata.AlbumArtist ?? string.Empty,
            ["edition"] = scan.AlbumName,
            ["format"] = "flac",
            ["source_type"] = sourceType,
            ["workflow_mode"] = workflowId,
            ["generated_by"] = "Album Fixer deterministic local processor",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["metadata_sources"] = new JsonArray("local CUE, album folder, rip sidecars, and local artwork"),
            ["discs"] = discReports,
            ["verification"] = new JsonObject
            {
                ["status"] = missing.Count > 0 ? "awaiting_metadata" : "pending",
                ["method"] = "Quick verification pending; decoded PCM byte-count and MD5 comparison skipped by user.",
                ["pcm_equivalence"] = "skipped_by_user",
                ["sources_deleted"] = false,
                ["errors"] = new JsonArray()
            },
            ["job"] = new JsonObject
            {
                ["identifier"] = Path.GetFileName(staged.JobDirectory),
                ["local_staging_used"] = true,
                ["source_cache_used"] = staged.SourceCacheUsed,
                ["source_input_mode"] = staged.SourceCacheUsed ? "size_checked_temp_cache" : "local_fixed_disk_in_place",
                ["staging_path"] = staged.JobDirectory,
                ["processor"] = discs.Count == 1 ? "local_ffmpeg_single_process" : "local_ffmpeg_sequential_disc_processes"
            }
        };
        if (metadata.Genre is not null)
        {
            report["genre"] = new JsonObject
            {
                ["value"] = metadata.Genre,
                ["source_type"] = metadata.GenreInferred ? "recognized_library_genre_folder" : "local_cue",
                ["confidence"] = "high",
                ["rationale"] = metadata.GenreInferred
                    ? "A recognized library category supplied genre only; it was not used as artist metadata."
                    : "Genre was read from the local CUE."
            };
        }
        if (cover is not null)
        {
            report["cover"] = cover.ToReport();
        }
        else
        {
            report["artwork"] = new JsonObject
            {
                ["status"] = "incomplete",
                ["reason"] = coverIssue ?? "No usable front-cover artwork was available."
            };
            ((JsonObject)report["verification"]!)["warnings"] = new JsonArray(JsonValue.Create(coverIssue ?? "Front-cover artwork is missing."));
        }
        if (missing.Count > 0) ((JsonObject)report["verification"]!)["missing_metadata"] = new JsonArray(missing.Select(value => JsonValue.Create(value)).ToArray());
        await AtomicWriteAsync(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);
    }

    private static async Task<string> RunProcessAsync(string executable, IReadOnlyList<string> arguments, Action<string>? onOutput, CancellationToken token)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
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
        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                output.AppendLine(line);
                onOutput?.Invoke(line);
            }
        }, CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { throw; }
        await stdout;
        var error = await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {Nonempty(error) ?? "unknown error"}");
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

    private static string SafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var cleaned = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim().TrimEnd('.');
        cleaned = Regex.Replace(cleaned, "\\s+", " ");
        if (cleaned.Length == 0) cleaned = "Untitled";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned))) cleaned = "_" + cleaned;
        return cleaned.Length <= 120 ? cleaned : cleaned[..120].TrimEnd();
    }

    private static string JsonPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) => new(phase, percent, "running", detail, DateTimeOffset.UtcNow);

    private sealed class CueSheet
    {
        public List<string> SourceFiles { get; set; } = [];
        public List<CueTrack> Tracks { get; } = [];
        public string? AlbumTitle { get; set; }
        public string? AlbumPerformer { get; set; }
        public string? Date { get; set; }
        public string? Genre { get; set; }
        public string? Composer { get; set; }
    }

    private sealed class CueTrack(int number)
    {
        public int Number { get; } = number;
        public string? Title { get; set; }
        public string? Performer { get; set; }
        public string? Composer { get; set; }
        public long? Index01Frames { get; set; }
    }

    private sealed record AudioProbe(int SampleRate, long? TotalSamples);
    private sealed record ResolvedMetadata(string? Album, string? AlbumArtist, string? Date, string? Genre, string? Composer, bool GenreInferred);
    private sealed record TrackOutput(CueTrack Track, string Path);
    private sealed record LocalInput(CueSheet Cue, string Source, AudioProbe Probe, ResolvedMetadata Metadata);
    private sealed record LocalDisc(
        int Number,
        CueSheet Cue,
        string Source,
        IReadOnlyList<TrackOutput> Outputs,
        ResolvedMetadata Metadata);
}
