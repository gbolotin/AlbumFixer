using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TagFile = TagLib.File;

namespace AlbumFixer.Core;

public sealed class LocalMetadataEnrichmentService
{
    private readonly ExternalMetadataService _externalMetadata;
    private readonly InMemoryArtworkService _artwork = new();

    public LocalMetadataEnrichmentService(ExternalMetadataService externalMetadata)
    {
        _externalMetadata = externalMetadata ?? throw new ArgumentNullException(nameof(externalMetadata));
    }

    public async Task<MetadataGapResult> EnrichAsync(
        ScanResult scan,
        StagedJob staged,
        LocalSplitResult split,
        IProgress<ProgressSnapshot> progress,
        CancellationToken token = default)
    {
        var requested = split.Metadata.MissingFields
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (requested.Length == 0) return split.Metadata;

        JsonObject report;
        await using (var reportStream = File.OpenRead(split.ReportPath))
        {
            report = await JsonNode.ParseAsync(reportStream, cancellationToken: token) as JsonObject
                ?? throw new JsonException("The local conversion report is not a JSON object.");
        }
        var album = Nonempty(report["album"]?.GetValue<string>());
        var artist = Nonempty(report["artist"]?.GetValue<string>());
        var tracks = ReportTracks(report, staged.AlbumRoot);
        var warnings = new List<string>();
        ExternalAlbumMetadata? external = null;

        if (album is null || artist is null)
        {
            warnings.Add("Deterministic metadata lookup requires a locally known album and artist; no speculative search was attempted.");
        }
        else
        {
            progress.Report(Snapshot(JobPhase.Tagging, 42,
                $"Resolving only the missing fields in local code: {string.Join(", ", requested)}."));
            var editionYear = FindYear(scan.AlbumName);
            external = await _externalMetadata.ResolveAsync(
                new(album, artist, tracks.Count, EditionYear: editionYear),
                includeTrackTitles: requested.Contains("TITLE", StringComparer.OrdinalIgnoreCase),
                token: token);
            warnings.AddRange(external.Warnings);
        }

        var resolved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preparedCover = await TryPrepareExternalCoverAsync(requested, external, staged, warnings, token);
        if (preparedCover is not null) resolved.Add("COVER");

        var date = Nonempty(external?.OriginalDate) ?? Nonempty(external?.ReleaseDate);
        var year = FindYear(date);
        if (requested.Contains("DATE", StringComparer.OrdinalIgnoreCase) && year is not null) resolved.Add("DATE");
        if (requested.Contains("GENRE", StringComparer.OrdinalIgnoreCase) && Nonempty(external?.Genre) is not null) resolved.Add("GENRE");
        if (requested.Contains("TITLE", StringComparer.OrdinalIgnoreCase) && external?.TrackTitles.Count == tracks.Count)
            resolved.Add("TITLE");

        for (var index = 0; index < tracks.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            using var file = TagFile.Create(tracks[index].Path);
            if (resolved.Contains("TITLE") && string.IsNullOrWhiteSpace(file.Tag.Title))
                file.Tag.Title = external!.TrackTitles[index];
            if (resolved.Contains("DATE")) file.Tag.Year = (uint)year!.Value;
            if (resolved.Contains("GENRE")) file.Tag.Genres = [external!.Genre!];
            if (resolved.Contains("COVER"))
            {
                file.Tag.Pictures = [InMemoryArtworkService.CreatePicture(preparedCover!)];
            }
            file.Save();
            if (resolved.Contains("TITLE")) tracks[index].Report["title"] = file.Tag.Title;
        }

        UpdateReport(report, requested, resolved, external, preparedCover, warnings, year);
        await AtomicWriteAsync(split.ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), token);

        var unresolved = requested.Where(field => !resolved.Contains(field)).ToArray();
        await WriteGapManifestAsync(staged.JobDirectory, unresolved, split.Metadata.LocalEvidence, token);
        progress.Report(Snapshot(JobPhase.Tagging, unresolved.Length == 0 ? 46 : 44,
            unresolved.Length == 0
                ? "Deterministic local metadata enrichment completed; no agent process was started."
                : $"Local metadata lookup completed; unresolved fields remain: {string.Join(", ", unresolved)}."));
        return new(true, unresolved.Length > 0, unresolved, split.Metadata.LocalEvidence);
    }

    private async Task<PreparedArtwork?> TryPrepareExternalCoverAsync(
        IReadOnlyList<string> requested,
        ExternalAlbumMetadata? external,
        StagedJob staged,
        ICollection<string> warnings,
        CancellationToken token)
    {
        if (!requested.Contains("COVER", StringComparer.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(external?.MusicBrainzReleaseId)) return null;

        try
        {
            var downloaded = await _externalMetadata.DownloadFrontCoverAsync(external.MusicBrainzReleaseId, token);
            return await _artwork.PrepareDownloadedAsync(downloaded, staged.FfmpegPath, staged.FfprobePath, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            warnings.Add($"Cover Art Archive lookup did not produce usable front artwork ({error.GetType().Name}).");
            return null;
        }
    }

    private static void UpdateReport(
        JsonObject report,
        IReadOnlyList<string> requested,
        ISet<string> resolved,
        ExternalAlbumMetadata? external,
        PreparedArtwork? cover,
        IReadOnlyList<string> warnings,
        int? year)
    {
        var unresolved = requested.Where(field => !resolved.Contains(field)).ToArray();
        var verification = report["verification"] as JsonObject ?? new JsonObject();
        report["verification"] = verification;
        verification["status"] = unresolved.Length == 0 ? "pending" : "awaiting_metadata";
        if (unresolved.Length == 0) verification.Remove("missing_metadata");
        else verification["missing_metadata"] = new JsonArray(unresolved.Select(value => JsonValue.Create(value)).ToArray());

        if (resolved.Contains("GENRE"))
        {
            report["genre"] = new JsonObject
            {
                ["value"] = external!.Genre,
                ["source_type"] = external.GenreSourceType ?? "external_catalog",
                ["confidence"] = external.GenreConfidence ?? "medium",
                ["rationale"] = external.GenreRationale ?? "Broad genre selected from a deterministic exact-album catalog match."
            };
        }
        if (resolved.Contains("DATE") && year is not null)
        {
            var release = report["release_metadata"] as JsonObject ?? new JsonObject();
            report["release_metadata"] = release;
            release["original_date"] = external?.OriginalDate;
            release["release_date"] = external?.ReleaseDate;
            release["tag_year"] = year.Value;
        }
        if (cover is not null)
        {
            report["schema_version"] = "2.0";
            report["cover"] = cover.ToReport();
            report.Remove("artwork");
        }

        var sources = report["metadata_sources"] as JsonArray ?? new JsonArray();
        report["metadata_sources"] = sources;
        foreach (var source in (external?.Sources ?? []).Append(cover?.Source).Where(value => !string.IsNullOrWhiteSpace(value)))
            if (!sources.Any(value => value?.GetValue<string>().Equals(source, StringComparison.OrdinalIgnoreCase) == true)) sources.Add(source);
        report["metadata_lookup"] = new JsonObject
        {
            ["implementation"] = "deterministic_local_code",
            ["status"] = unresolved.Length == 0 ? "complete" : resolved.Count > 0 ? "partial" : "unresolved",
            ["requested_fields"] = new JsonArray(requested.Select(value => JsonValue.Create(value)).ToArray()),
            ["resolved_fields"] = new JsonArray(resolved.OrderBy(value => value).Select(value => JsonValue.Create(value)).ToArray()),
            ["warnings"] = new JsonArray(warnings.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => JsonValue.Create(value)).ToArray())
        };
    }

    private static IReadOnlyList<ReportTrack> ReportTracks(JsonObject report, string albumRoot)
    {
        if (report["discs"] is not JsonArray discs) throw new JsonException("The FLAC report has no discs array.");
        var result = new List<ReportTrack>();
        foreach (var discNode in discs)
        {
            if (discNode is not JsonObject disc || disc["tracks"] is not JsonArray tracks)
                throw new JsonException("A FLAC report disc has no tracks array.");
            foreach (var trackNode in tracks)
            {
                if (trackNode is not JsonObject track) throw new JsonException("A FLAC report track is not an object.");
                var relative = track["file"]?.GetValue<string>() ?? throw new JsonException("A FLAC report track path is missing.");
                var path = HostStagingService.SafeCombine(albumRoot, relative);
                if (!File.Exists(path)) throw new FileNotFoundException("A locally split track is missing.", path);
                result.Add(new(track, path));
            }
        }
        return result;
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
            ["handled_by"] = "deterministic_local_code"
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

    private static int? FindYear(string? value)
    {
        if (value is null) return null;
        var match = Regex.Match(value, "(?:19|20)\\d{2}");
        return match.Success && int.TryParse(match.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : null;
    }

    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ProgressSnapshot Snapshot(JobPhase phase, int percent, string detail) =>
        new(phase, percent, "running", detail, DateTimeOffset.UtcNow);

    private sealed record ReportTrack(JsonObject Report, string Path);
}
