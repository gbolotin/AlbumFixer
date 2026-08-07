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
            throw new NotSupportedException("The deterministic local processor currently supports FLAC + CUE image splits only.");

        progress.Report(Snapshot(JobPhase.Processing, 19, "Reading the staged CUE locally. No Codex process is needed for splitting."));
        var cuePaths = Directory.EnumerateFiles(staged.AlbumRoot, "*.cue", SearchOption.AllDirectories).ToArray();
        if (cuePaths.Length != 1)
            throw new InvalidOperationException($"The local splitter requires exactly one CUE sheet; found {cuePaths.Length}.");

        var cue = await ParseCueAsync(cuePaths[0], token);
        var source = ResolveSource(staged.AlbumRoot, cuePaths[0], cue.SourceFiles);
        var probe = await ProbeAudioAsync(staged.FfprobePath, source, token);
        ValidateBoundaries(cue.Tracks, probe.SampleRate, probe.TotalSamples);

        var metadata = ResolveMetadata(scan, cue);
        var cover = await PrepareCoverAsync(staged, token);
        var missing = FindMissing(metadata, cue.Tracks, cover is not null);
        var outputRoot = HostStagingService.SafeCombine(staged.AlbumRoot, Path.Combine("Tracks", "CD1"));
        if (Directory.Exists(outputRoot) && Directory.EnumerateFileSystemEntries(outputRoot).Any())
            throw new IOException("The local output folder is not empty. Album Fixer will not merge or overwrite an earlier result.");
        Directory.CreateDirectory(outputRoot);

        var outputs = BuildOutputs(cue.Tracks, outputRoot);
        progress.Report(Snapshot(JobPhase.Processing, 21, $"Splitting {outputs.Count} tracks locally in one FFmpeg process."));
        await SplitOnceAsync(staged.FfmpegPath, source, cover?.Path, probe.SampleRate, cue.Tracks, outputs, metadata, progress, token);
        progress.Report(Snapshot(JobPhase.Tagging, 36, cover is null
            ? "Local tags were written. Required artwork is missing and was deferred."
            : "Local tags and front-cover artwork were written during the split."));

        var reportPath = Path.Combine(staged.AlbumRoot, "conversion-report.json");
        await WriteReportAsync(scan, staged, cue, source, outputs, metadata, cover, missing, reportPath, token);
        var evidence = LocalEvidence(scan, cuePaths[0], cover);
        await WriteGapManifestAsync(staged.JobDirectory, missing, evidence, token);
        var gapResult = new MetadataGapResult(true, missing.Count > 0, missing, evidence);

        progress.Report(Snapshot(JobPhase.Tagging, missing.Count > 0 ? 40 : 44, missing.Count > 0
            ? $"Local split complete. Only these required fields need metadata fallback: {string.Join(", ", missing)}."
            : "Local split, tags, and artwork are complete. Metadata research was skipped."));
        return new(outputs.Count, reportPath, gapResult);
    }

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
            throw new InvalidDataException($"The local splitter supports one FLAC image per CUE; this CUE names {sheet.SourceFiles.Count} source files.");
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
        if (!File.Exists(candidate)) throw new FileNotFoundException("The staged CUE source does not exist.", candidate);
        if (!Path.GetExtension(candidate).Equals(".flac", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("The deterministic local CUE splitter currently accepts FLAC sources only.");
        return candidate;
    }

    private static async Task<AudioProbe> ProbeAudioAsync(string ffprobe, string source, CancellationToken token)
    {
        var output = await RunProcessAsync(ffprobe,
            ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=sample_rate,duration_ts", "-of", "json", source],
            null, token);
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
            throw new InvalidDataException("ffprobe found no audio stream in the staged FLAC source.");
        var stream = streams[0];
        var sampleText = stream.TryGetProperty("sample_rate", out var rateNode) ? rateNode.GetString() : null;
        if (!int.TryParse(sampleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate) || sampleRate <= 0)
            throw new InvalidDataException("ffprobe did not return a valid FLAC sample rate.");
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
            if (totalSamples is not null && sample >= totalSamples.Value) throw new InvalidDataException("A CUE boundary lies beyond the FLAC audio stream.");
            previous = sample;
        }
    }

    private static ResolvedMetadata ResolveMetadata(ScanResult scan, CueSheet cue)
    {
        var year = FolderYear.Match(scan.AlbumName);
        var album = Nonempty(cue.AlbumTitle) ?? CleanAlbumName(scan.AlbumName);
        var artist = Nonempty(cue.AlbumPerformer) ?? Nonempty(Directory.GetParent(scan.AlbumRoot)?.Name);
        var date = Nonempty(cue.Date) ?? (year.Success ? year.Groups["year"].Value : null);
        var genre = Nonempty(cue.Genre) ?? InferGenreFromFolders(scan.AlbumRoot);
        return new(album, artist, date, genre, Nonempty(cue.Composer), cue.Genre is null && genre is not null);
    }

    private static string CleanAlbumName(string name)
    {
        var value = FolderYear.Replace(name, string.Empty);
        value = EditionSuffix.Replace(value, " ");
        return Regex.Replace(value, "\\s+", " ").Trim();
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
        {
            foreach (var mapping in mappings)
                if (directory.Name.Contains(mapping.Needle, StringComparison.OrdinalIgnoreCase)) return mapping.Genre;
        }
        return null;
    }

    private static async Task<LocalCover?> PrepareCoverAsync(StagedJob staged, CancellationToken token)
    {
        var candidates = Directory.EnumerateFiles(staged.AlbumRoot, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => !HostStagingService.SafeRelative(staged.AlbumRoot, path).StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => CoverRank(staged.AlbumRoot, path))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidates.Length == 0) return null;

        var source = candidates[0];
        var target = Path.Combine(staged.AlbumRoot, "cover.jpg");
        if (!source.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target)) throw new IOException("A different root cover.jpg already exists in local staging.");
            if (Path.GetExtension(source).Equals(".jpg", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(source).Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, overwrite: false);
            else
                await RunProcessAsync(staged.FfmpegPath, ["-hide_banner", "-nostdin", "-loglevel", "error", "-n", "-i", source, "-frames:v", "1", "-q:v", "2", target], null, token);
        }
        return new(target, $"local file: {JsonPath(HostStagingService.SafeRelative(staged.AlbumRoot, source))}");
    }

    private static int CoverRank(string root, string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("cover", StringComparison.OrdinalIgnoreCase) || name.Equals("folder", StringComparison.OrdinalIgnoreCase) || name.Equals("front", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) || name.Contains("cover", StringComparison.OrdinalIgnoreCase)) return 1;
        if (Path.GetDirectoryName(path)?.Contains($"{Path.DirectorySeparatorChar}Covers", StringComparison.OrdinalIgnoreCase) == true) return 2;
        return Path.GetDirectoryName(path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? 3 : 4;
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

    private static async Task SplitOnceAsync(
        string ffmpeg,
        string source,
        string? cover,
        int sampleRate,
        IReadOnlyList<CueTrack> tracks,
        IReadOnlyList<TrackOutput> outputs,
        ResolvedMetadata metadata,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token)
    {
        var arguments = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-n", "-i", source };
        if (cover is not null) { arguments.Add("-i"); arguments.Add(cover); }

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
            if (cover is not null) { arguments.Add("-map"); arguments.Add("1:v:0"); }
            arguments.Add("-map_metadata"); arguments.Add("-1");
            arguments.Add("-c:a"); arguments.Add("flac");
            arguments.Add("-compression_level"); arguments.Add("5");
            if (cover is not null)
            {
                arguments.Add("-c:v"); arguments.Add("copy");
                arguments.Add("-disposition:v:0"); arguments.Add("attached_pic");
                AddMetadata(arguments, "title", "Album cover", "-metadata:s:v:0");
                AddMetadata(arguments, "comment", "Cover (front)", "-metadata:s:v:0");
            }
            AddMetadata(arguments, "TITLE", track.Title);
            AddMetadata(arguments, "ALBUM", metadata.Album);
            AddMetadata(arguments, "ARTIST", Nonempty(track.Performer) ?? metadata.AlbumArtist);
            AddMetadata(arguments, "ALBUMARTIST", metadata.AlbumArtist);
            AddMetadata(arguments, "TRACKNUMBER", $"{track.Number}/{tracks.Count}");
            AddMetadata(arguments, "TRACKTOTAL", tracks.Count.ToString(CultureInfo.InvariantCulture));
            AddMetadata(arguments, "DISCNUMBER", "1/1");
            AddMetadata(arguments, "DISCTOTAL", "1");
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
            progress.Report(Snapshot(JobPhase.Processing, percent, $"FFmpeg is splitting {tracks.Count} tracks locally in one pass."));
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
        if ((metadata.Genre?.Contains("classical", StringComparison.OrdinalIgnoreCase) == true || metadata.Genre?.Contains("opera", StringComparison.OrdinalIgnoreCase) == true) &&
            tracks.Any(track => Nonempty(track.Composer) is null && Nonempty(metadata.Composer) is null)) missing.Add("COMPOSER");
        if (!hasCover) missing.Add("COVER");
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IReadOnlyList<string> LocalEvidence(ScanResult scan, string cuePath, LocalCover? cover)
    {
        var evidence = new List<string>
        {
            $"CUE: {Path.GetFileName(cuePath)}",
            $"album folder: {scan.AlbumName}"
        };
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
        CueSheet cue,
        string source,
        IReadOnlyList<TrackOutput> outputs,
        ResolvedMetadata metadata,
        LocalCover? cover,
        IReadOnlyList<string> missing,
        string reportPath,
        CancellationToken token)
    {
        var tracks = new JsonArray();
        for (var index = 0; index < outputs.Count; index++)
        {
            var cueTrack = outputs[index].Track;
            tracks.Add(new JsonObject
            {
                ["disc"] = 1,
                ["track"] = cueTrack.Number,
                ["title"] = cueTrack.Title ?? string.Empty,
                ["file"] = JsonPath(HostStagingService.SafeRelative(staged.AlbumRoot, outputs[index].Path))
            });
        }

        var report = new JsonObject
        {
            ["schema_version"] = "1.0",
            ["album"] = metadata.Album ?? string.Empty,
            ["edition"] = scan.AlbumName,
            ["format"] = "flac",
            ["source_type"] = "flac_cue",
            ["workflow_mode"] = "flac_cue_split",
            ["generated_by"] = "Album Fixer deterministic local processor",
            ["generated_at_utc"] = DateTimeOffset.UtcNow,
            ["metadata_sources"] = new JsonArray("local CUE, album folder, rip sidecars, and local artwork"),
            ["discs"] = new JsonArray(new JsonObject
            {
                ["disc"] = 1,
                ["source"] = JsonPath(HostStagingService.SafeRelative(staged.AlbumRoot, source)),
                ["tracks"] = tracks
            }),
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
                ["staging_path"] = staged.JobDirectory,
                ["processor"] = "local_ffmpeg_single_process"
            }
        };
        if (metadata.Genre is not null)
        {
            report["genre"] = new JsonObject
            {
                ["value"] = metadata.Genre,
                ["source_type"] = metadata.GenreInferred ? "inferred_from_library_folder" : "local_cue",
                ["confidence"] = "high",
                ["rationale"] = metadata.GenreInferred ? "Album is stored beneath the matching library genre folder." : "Genre was read from the local CUE."
            };
        }
        if (cover is not null)
        {
            report["cover"] = new JsonObject
            {
                ["file"] = "cover.jpg",
                ["size"] = new JsonArray(0, 0),
                ["source"] = cover.Source
            };
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
    private sealed record LocalCover(string Path, string Source);
    private sealed record TrackOutput(CueTrack Track, string Path);
}
