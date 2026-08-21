using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AlbumFixer.Core;

internal enum ArtworkSelectionMode
{
    Flac,
    Dsd
}

public sealed record DownloadedArtwork(byte[] Data, string MimeType, string Source);

internal sealed record PreparedArtwork(
    byte[] JpegBytes,
    string Source,
    int Width,
    int Height,
    string Sha256)
{
    public const string MimeType = "image/jpeg";
    public int ByteSize => JpegBytes.Length;

    public JsonObject ToReport() => new()
    {
        ["storage"] = "embedded_only",
        ["source"] = Source,
        ["mime_type"] = MimeType,
        ["width"] = Width,
        ["height"] = Height,
        ["byte_size"] = ByteSize,
        ["sha256"] = Sha256
    };
}

internal sealed record ArtworkPreparation(PreparedArtwork? Artwork, string? Issue);

internal sealed class InMemoryArtworkService
{
    internal const int MaximumDimension = 600;
    internal const int MaximumPreparedBytes = 1 * 1024 * 1024;
    private const int MaximumPipeOutputBytes = 4 * 1024 * 1024;
    private const int MaximumPdfArtworkBytes = 25 * 1024 * 1024;
    private const int MaximumInMemoryTiffRepairBytes = 64 * 1024 * 1024;

    public async Task<ArtworkPreparation> PrepareLocalAsync(
        string sourceAlbumRoot,
        string ffmpeg,
        string ffprobe,
        ArtworkSelectionMode mode,
        CancellationToken token = default)
    {
        try
        {
            var selection = SelectLocalArtwork(sourceAlbumRoot, mode);
            if (selection is null)
            {
                var pdfArtwork = await TryExtractSinglePagePdfArtworkAsync(sourceAlbumRoot, token);
                if (pdfArtwork is not null)
                    return new(await PrepareDownloadedAsync(pdfArtwork, ffmpeg, ffprobe, token), null);

                var issue = mode == ArtworkSelectionMode.Flac
                    ? "No dedicated front-cover image, recognizable first scan, or single-image cover PDF was found in the album scans."
                    : "SACD extraction requires local front-cover artwork.";
                return new(null, issue);
            }

            byte[]? repairedTiff = null;
            if (IsTiff(selection.Path) && new FileInfo(selection.Path).Length <= MaximumInMemoryTiffRepairBytes)
            {
                var sourceBytes = await File.ReadAllBytesAsync(selection.Path, token);
                repairedTiff = RepairMissingTiffRowsPerStrip(sourceBytes);
            }

            var sourceSize = await ProbeImageAsync(ffprobe, selection.Path, null, token);
            var crop = Math.Min(sourceSize.Width, sourceSize.Height);
            var output = Math.Min(crop, MaximumDimension);
            string filter;
            if (selection.DerivesBookletPanel)
            {
                if (sourceSize.Width < sourceSize.Height * 3 / 2)
                {
                    var relative = JsonPath(HostStagingService.SafeRelative(sourceAlbumRoot, selection.Path));
                    return new(null, $"The first booklet scan is not a recognizable landscape cover spread: {relative}.");
                }
                crop = Math.Min(sourceSize.Height, sourceSize.Width / 2);
                output = Math.Min(crop, MaximumDimension);
                filter = $"crop={crop}:{crop}:{sourceSize.Width - crop}:0,scale={output}:{output}";
            }
            else
            {
                filter = $"crop={crop}:{crop}:{(sourceSize.Width - crop) / 2}:{(sourceSize.Height - crop) / 2},scale={output}:{output}";
            }

            var relativePath = JsonPath(HostStagingService.SafeRelative(sourceAlbumRoot, selection.Path));
            var description = selection.DerivesBookletPanel
                ? $"local file: {relativePath} (right-side front-panel crop; normalized JPEG in memory)"
                : $"local file: {relativePath} (normalized JPEG in memory)";
            if (repairedTiff is not null)
                description += " (missing TIFF RowsPerStrip metadata repaired in memory)";
            var artwork = repairedTiff is null
                ? await NormalizeAsync(ffmpeg, ["-i", selection.Path], null, filter, output, description, token)
                : await NormalizeRepairedTiffAsync(ffmpeg, repairedTiff, filter, output, description, token);
            return new(artwork, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or JsonException)
        {
            return new(null, $"The local front cover could not be prepared safely in memory: {error.Message}");
        }
    }

    public async Task<PreparedArtwork> PrepareDownloadedAsync(
        DownloadedArtwork downloaded,
        string ffmpeg,
        string ffprobe,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(downloaded);
        if (downloaded.Data.Length == 0) throw new InvalidDataException("The downloaded cover is empty.");
        var sourceSize = await ProbeImageAsync(ffprobe, null, downloaded.Data, token);
        var crop = Math.Min(sourceSize.Width, sourceSize.Height);
        var output = Math.Min(crop, MaximumDimension);
        var filter = $"crop={crop}:{crop}:{(sourceSize.Width - crop) / 2}:{(sourceSize.Height - crop) / 2},scale={output}:{output}";
        return await NormalizeAsync(
            ffmpeg,
            ["-f", "image2pipe", "-i", "pipe:0"],
            downloaded.Data,
            filter,
            output,
            $"{downloaded.Source} (normalized JPEG in memory)",
            token);
    }

    public async Task<ArtworkPreparation> PrepareExternalAsync(
        ExternalMetadataService externalMetadata,
        string? musicBrainzReleaseId,
        string ffmpeg,
        string ffprobe,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(externalMetadata);
        if (string.IsNullOrWhiteSpace(musicBrainzReleaseId))
            return new(null, "No exact MusicBrainz release match was available for external front-cover lookup.");
        try
        {
            var downloaded = await externalMetadata.DownloadFrontCoverAsync(musicBrainzReleaseId, token);
            return new(await PrepareDownloadedAsync(downloaded, ffmpeg, ffprobe, token), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or InvalidOperationException)
        {
            return new(null, $"External front-cover lookup did not produce usable artwork ({error.GetType().Name}): {error.Message}");
        }
    }

    public async Task<ArtworkPreparation> PrepareExternalUrlAsync(
        ExternalMetadataService externalMetadata,
        string? artworkUrl,
        string ffmpeg,
        string ffprobe,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(externalMetadata);
        if (string.IsNullOrWhiteSpace(artworkUrl))
            return new(null, "No verified Discogs primary image was available for external front-cover lookup.");
        try
        {
            var downloaded = await externalMetadata.DownloadArtworkAsync(artworkUrl, token);
            return new(await PrepareDownloadedAsync(downloaded, ffmpeg, ffprobe, token), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or
                                            InvalidOperationException or JsonException)
        {
            return new(null, $"Discogs front-cover lookup did not produce usable artwork ({error.GetType().Name}): {error.Message}");
        }
    }

    public async Task<ArtworkPreparation> PrepareLocalThenExternalAsync(
        string sourceAlbumRoot,
        string ffmpeg,
        string ffprobe,
        ArtworkSelectionMode mode,
        ExternalMetadataService externalMetadata,
        string? musicBrainzReleaseId,
        CancellationToken token = default)
    {
        var local = await PrepareLocalAsync(sourceAlbumRoot, ffmpeg, ffprobe, mode, token);
        if (local.Artwork is not null) return local;
        ArtworkPreparation? embedded = null;
        if (mode == ArtworkSelectionMode.Dsd)
        {
            embedded = await PrepareEmbeddedLocalAsync(sourceAlbumRoot, ffmpeg, ffprobe, token);
            if (embedded.Artwork is not null) return embedded;
        }
        var external = await PrepareExternalAsync(externalMetadata, musicBrainzReleaseId, ffmpeg, ffprobe, token);
        if (external.Artwork is not null) return external;
        return new(null, string.Join(" ", new[] { local.Issue, embedded?.Issue, external.Issue }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    private async Task<ArtworkPreparation> PrepareEmbeddedLocalAsync(
        string sourceAlbumRoot,
        string ffmpeg,
        string ffprobe,
        CancellationToken token)
    {
        foreach (var path in Directory.EnumerateFiles(sourceAlbumRoot, "*", SearchOption.AllDirectories)
                     .Where(path => new[] { ".flac", ".dsf", ".dff" }
                         .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase))
                {
                    var dff = DffMetadata.Read(path);
                    if (dff.Picture is not { Length: > 0 }) continue;
                    var dffRelative = JsonPath(HostStagingService.SafeRelative(sourceAlbumRoot, path));
                    return new(await PrepareDownloadedAsync(
                        new DownloadedArtwork(dff.Picture, dff.PictureMimeType ?? "image/jpeg",
                            $"embedded artwork from retained local DFF track: {dffRelative}"),
                        ffmpeg, ffprobe, token), null);
                }
                using var file = TagLib.File.Create(path);
                var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
                              ?? file.Tag.Pictures.FirstOrDefault();
                if (picture is null || picture.Data.Count == 0) continue;
                var relative = JsonPath(HostStagingService.SafeRelative(sourceAlbumRoot, path));
                return new(await PrepareDownloadedAsync(
                    new DownloadedArtwork(picture.Data.Data.ToArray(), picture.MimeType,
                        $"embedded artwork from retained local track: {relative}"),
                    ffmpeg, ffprobe, token), null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                                          TagLib.CorruptFileException or InvalidDataException or InvalidOperationException)
            {
                // A stale or unrelated audio file cannot supply artwork; continue to the next local candidate.
            }
        }
        return new(null, "No usable embedded artwork was found in retained local audio tracks.");
    }

    public static TagLib.Picture CreatePicture(PreparedArtwork artwork) => new(new TagLib.ByteVector(artwork.JpegBytes))
    {
        Type = TagLib.PictureType.FrontCover,
        Description = "Cover (front)",
        MimeType = PreparedArtwork.MimeType
    };

    public static string? ReadFrontCoverSha256(string path)
    {
        if (Path.GetExtension(path).Equals(".dff", StringComparison.OrdinalIgnoreCase))
        {
            var dffPicture = DffMetadata.Read(path).Picture;
            return dffPicture is null ? null : Convert.ToHexString(SHA256.HashData(dffPicture));
        }
        using var file = TagLib.File.Create(path);
        var picture = file.Tag.Pictures.FirstOrDefault(value => value.Type == TagLib.PictureType.FrontCover)
            ?? file.Tag.Pictures.FirstOrDefault();
        return picture is null ? null : Convert.ToHexString(SHA256.HashData(picture.Data.Data));
    }

    private static async Task<PreparedArtwork> NormalizeAsync(
        string ffmpeg,
        IReadOnlyList<string> inputArguments,
        byte[]? standardInput,
        string filter,
        int outputDimension,
        string source,
        CancellationToken token)
    {
        var bytes = await EncodeAsync(ffmpeg, inputArguments, standardInput, filter, quality: 4, token);
        if (bytes.Length > MaximumPreparedBytes)
            bytes = await EncodeAsync(ffmpeg, inputArguments, standardInput, filter, quality: 7, token);
        if (bytes.Length == 0) throw new InvalidDataException("FFmpeg returned an empty normalized cover.");
        if (bytes.Length > MaximumPreparedBytes)
            throw new InvalidDataException($"The normalized cover exceeds the {MaximumPreparedBytes / (1024 * 1024)} MB embedding limit.");
        return new(bytes, source, outputDimension, outputDimension, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static Task<byte[]> EncodeAsync(
        string ffmpeg,
        IReadOnlyList<string> inputArguments,
        byte[]? standardInput,
        string filter,
        int quality,
        CancellationToken token)
    {
        var arguments = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (standardInput is null) arguments.Add("-nostdin");
        arguments.AddRange(inputArguments);
        arguments.AddRange(["-vf", filter, "-frames:v", "1", "-q:v", quality.ToString(), "-pix_fmt", "yuvj420p", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"]);
        return RunBinaryProcessAsync(ffmpeg, arguments, standardInput, MaximumPipeOutputBytes, token);
    }

    private static async Task<PreparedArtwork> NormalizeRepairedTiffAsync(
        string ffmpeg,
        byte[] repairedTiff,
        string filter,
        int outputDimension,
        string source,
        CancellationToken token)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"album-fixer-artwork-{Guid.NewGuid():N}.tif");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                await stream.WriteAsync(repairedTiff, token);
            return await NormalizeAsync(ffmpeg, ["-i", temporaryPath], null, filter, outputDimension, source, token);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    internal static byte[]? RepairMissingTiffRowsPerStrip(byte[] source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length < 8) return null;

        var littleEndian = source[0] == (byte)'I' && source[1] == (byte)'I';
        var bigEndian = source[0] == (byte)'M' && source[1] == (byte)'M';
        if (!littleEndian && !bigEndian || ReadUInt16(source.AsSpan(2, 2), littleEndian) != 42)
            return null;

        var ifdOffsetValue = ReadUInt32(source.AsSpan(4, 4), littleEndian);
        if (ifdOffsetValue > int.MaxValue) return null;
        var ifdOffset = (int)ifdOffsetValue;
        if (ifdOffset < 8 || ifdOffset > source.Length - 2) return null;
        var entryCount = ReadUInt16(source.AsSpan(ifdOffset, 2), littleEndian);
        if (entryCount == ushort.MaxValue) return null;
        var entriesOffset = ifdOffset + 2;
        var directoryEnd = (long)entriesOffset + entryCount * 12L + 4;
        if (directoryEnd > source.Length) return null;

        uint imageHeight = 0;
        var entries = new List<byte[]>(entryCount + 1);
        for (var index = 0; index < entryCount; index++)
        {
            var entryOffset = entriesOffset + index * 12;
            var entry = source.AsSpan(entryOffset, 12);
            var tag = ReadUInt16(entry[..2], littleEndian);
            if (tag == 278) return null;
            if (tag == 257 && ReadUInt32(entry.Slice(4, 4), littleEndian) == 1)
            {
                var type = ReadUInt16(entry.Slice(2, 2), littleEndian);
                imageHeight = type switch
                {
                    3 => ReadUInt16(entry.Slice(8, 2), littleEndian),
                    4 => ReadUInt32(entry.Slice(8, 4), littleEndian),
                    _ => 0
                };
            }
            entries.Add(entry.ToArray());
        }
        if (imageHeight == 0) return null;

        var rowsPerStrip = new byte[12];
        WriteUInt16(rowsPerStrip.AsSpan(0, 2), 278, littleEndian);
        WriteUInt16(rowsPerStrip.AsSpan(2, 2), 4, littleEndian);
        WriteUInt32(rowsPerStrip.AsSpan(4, 4), 1, littleEndian);
        WriteUInt32(rowsPerStrip.AsSpan(8, 4), imageHeight, littleEndian);
        entries.Add(rowsPerStrip);
        entries.Sort((left, right) => ReadUInt16(left.AsSpan(0, 2), littleEndian)
            .CompareTo(ReadUInt16(right.AsSpan(0, 2), littleEndian)));

        var newIfdOffset = (source.Length + 1L) & ~1L;
        var repairedLength = newIfdOffset + 2L + entries.Count * 12L + 4L;
        if (newIfdOffset > uint.MaxValue || repairedLength > int.MaxValue) return null;
        var repaired = new byte[(int)repairedLength];
        source.CopyTo(repaired, 0);
        WriteUInt32(repaired.AsSpan(4, 4), (uint)newIfdOffset, littleEndian);
        var writeOffset = (int)newIfdOffset;
        WriteUInt16(repaired.AsSpan(writeOffset, 2), (ushort)entries.Count, littleEndian);
        writeOffset += 2;
        foreach (var entry in entries)
        {
            entry.CopyTo(repaired, writeOffset);
            writeOffset += 12;
        }
        source.AsSpan((int)directoryEnd - 4, 4).CopyTo(repaired.AsSpan(writeOffset, 4));
        return repaired;
    }

    private static ushort ReadUInt16(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt16BigEndian(bytes);

    private static uint ReadUInt32(ReadOnlySpan<byte> bytes, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes)
        : BinaryPrimitives.ReadUInt32BigEndian(bytes);

    private static void WriteUInt16(Span<byte> bytes, ushort value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt16LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
    }

    private static void WriteUInt32(Span<byte> bytes, uint value, bool littleEndian)
    {
        if (littleEndian) BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        else BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
    }

    private static bool IsTiff(string path) =>
        Path.GetExtension(path).Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
        Path.GetExtension(path).Equals(".tiff", StringComparison.OrdinalIgnoreCase);

    private static async Task<ImageSize> ProbeImageAsync(string ffprobe, string? path, byte[]? bytes, CancellationToken token)
    {
        var arguments = new List<string> { "-v", "error" };
        if (path is not null) arguments.AddRange(["-i", path]);
        else arguments.AddRange(["-f", "image2pipe", "-i", "pipe:0"]);
        arguments.AddRange(["-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "json"]);
        var output = await RunBinaryProcessAsync(ffprobe, arguments, bytes, 1024 * 1024, token);
        using var document = JsonDocument.Parse(output);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array || streams.GetArrayLength() == 0)
            throw new InvalidDataException("No readable image stream was found.");
        var stream = streams[0];
        if (!stream.TryGetProperty("width", out var widthValue) || !widthValue.TryGetInt32(out var width) || width <= 0 ||
            !stream.TryGetProperty("height", out var heightValue) || !heightValue.TryGetInt32(out var height) || height <= 0)
            throw new InvalidDataException("The image dimensions are invalid.");
        return new(width, height);
    }

    private static LocalArtworkSelection? SelectLocalArtwork(string root, ArtworkSelectionMode mode)
    {
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => mode != ArtworkSelectionMode.Flac ||
                           !HostStagingService.SafeRelative(root, path).StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => CoverRank(root, path, mode))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mode == ArtworkSelectionMode.Dsd)
            return candidates.FirstOrDefault() is { } dsd ? new(dsd, false) : null;

        var explicitFront = candidates.FirstOrDefault(path => CoverRank(root, path, mode) < 2);
        var bookletFront = explicitFront is null ? candidates.FirstOrDefault(IsFirstBookletScan) : null;
        var numberedScanFront = explicitFront is null && bookletFront is null
            ? candidates.FirstOrDefault(IsRecognizableFirstCoverScan)
            : null;
        var pairedScanFront = explicitFront is null && bookletFront is null && numberedScanFront is null
            ? candidates.FirstOrDefault(path => IsPairedWithNamedNonFrontScan(path, candidates))
            : null;
        var soleUnambiguousImage = explicitFront is null && bookletFront is null && numberedScanFront is null && pairedScanFront is null && candidates.Length == 1 &&
                                   !HasNonFrontName(candidates[0])
            ? candidates[0]
            : null;
        var source = explicitFront ?? bookletFront ?? numberedScanFront ?? pairedScanFront ?? soleUnambiguousImage;
        return source is null ? null : new(source, bookletFront is not null);
    }

    private static int CoverRank(string root, string path, ArtworkSelectionMode mode)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("front", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("sleeve", StringComparison.OrdinalIgnoreCase) && !name.Contains("disc", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cover", StringComparison.OrdinalIgnoreCase) && !name.Contains("back", StringComparison.OrdinalIgnoreCase)) return 1;
        if (mode == ArtworkSelectionMode.Flac)
            return IsFirstBookletScan(path) || IsRecognizableFirstCoverScan(path) ? 2 : int.MaxValue;
        if (Path.GetDirectoryName(path)?.Contains($"{Path.DirectorySeparatorChar}Artwork", StringComparison.OrdinalIgnoreCase) == true) return 2;
        return Path.GetDirectoryName(path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? 3 : 4;
    }

    private static bool IsFirstBookletScan(string path)
    {
        var compact = Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return compact.Equals("booklet1", StringComparison.OrdinalIgnoreCase) ||
               compact.Equals("booklet01", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRecognizableFirstCoverScan(string path)
    {
        var compact = Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return compact.Equals("subjectdelta01", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DownloadedArtwork?> TryExtractSinglePagePdfArtworkAsync(
        string root,
        CancellationToken token)
    {
        var candidates = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        if (candidates.Length != 1) return null;

        var path = candidates[0];
        var size = new FileInfo(path).Length;
        if (size <= 0 || size > MaximumPdfArtworkBytes) return null;
        var bytes = await File.ReadAllBytesAsync(path, token);
        var text = Encoding.ASCII.GetString(bytes);
        if (!Regex.IsMatch(text, @"/Count\s+1(?!\d)") ||
            Regex.Matches(text, @"/Type\s*/Page(?!s)").Count != 1)
            return null;

        var markers = Regex.Matches(text, @"/DCTDecode\b").Cast<Match>().ToArray();
        if (markers.Length != 1) return null;
        var marker = markers[0].Index;
        var objectStart = text.LastIndexOf(" obj", marker, StringComparison.Ordinal);
        var stream = text.IndexOf("stream", marker, StringComparison.Ordinal);
        if (objectStart < 0 || stream < 0 || stream - objectStart > 8192) return null;
        var dictionary = text.Substring(objectStart, stream - objectStart);
        if (!Regex.IsMatch(dictionary, @"/Subtype\s*/Image\b") ||
            !Regex.IsMatch(dictionary, @"/Type\s*/XObject\b")) return null;
        var lengthMatch = Regex.Match(dictionary, @"/Length\s+(?<length>\d+)\b");
        if (!lengthMatch.Success || !int.TryParse(lengthMatch.Groups["length"].Value, out var length) ||
            length <= 0 || length > MaximumPdfArtworkBytes) return null;

        var dataStart = stream + "stream".Length;
        if (dataStart < bytes.Length && bytes[dataStart] == (byte)'\r') dataStart++;
        if (dataStart < bytes.Length && bytes[dataStart] == (byte)'\n') dataStart++;
        if (dataStart < 0 || dataStart > bytes.Length - length) return null;
        var jpeg = bytes.AsSpan(dataStart, length).ToArray();
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8 ||
            jpeg[^2] != 0xFF || jpeg[^1] != 0xD9) return null;

        var relative = JsonPath(HostStagingService.SafeRelative(root, path));
        return new(jpeg, "image/jpeg", $"single-image one-page cover PDF: {relative}");
    }

    private static bool HasNonFrontName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return new[] { "back", "inlay", "tray", "disc", "disk", "cd", "inside", "booklet" }
            .Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPairedWithNamedNonFrontScan(string path, IReadOnlyList<string> candidates)
    {
        if (HasNonFrontName(path)) return false;
        var directory = Path.GetDirectoryName(path);
        var frontStem = Path.GetFileNameWithoutExtension(path);
        return candidates.Any(candidate =>
            !candidate.Equals(path, StringComparison.OrdinalIgnoreCase) &&
            Path.GetDirectoryName(candidate)?.Equals(directory, StringComparison.OrdinalIgnoreCase) == true &&
            HasNonFrontName(candidate) &&
            Regex.Replace(Path.GetFileNameWithoutExtension(candidate),
                    @"(?:[\s_-]+)(?:back|inlay|tray|inside|booklet)$", string.Empty,
                    RegexOptions.IgnoreCase)
                .Equals(frontStem, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<byte[]> RunBinaryProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        byte[]? standardInput,
        int maximumOutputBytes,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            throw new FileNotFoundException("The required artwork tool is unavailable.", executable);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new InvalidOperationException($"Could not start {Path.GetFileName(executable)}.");
        using var registration = token.Register(() => TryKill(process));
        var inputTask = standardInput is null ? Task.CompletedTask : WriteInputAsync(process, standardInput, token);
        var outputTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maximumOutputBytes, token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            await Task.WhenAll(inputTask, outputTask, errorTask, process.WaitForExitAsync(token));
        }
        catch (Exception inputError) when (inputError is IOException && !token.IsCancellationRequested)
        {
            TryKill(process);
            var processError = errorTask.IsCompletedSuccessfully ? await errorTask : null;
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {Nonempty(processError) ?? inputError.Message}", inputError);
        }
        catch
        {
            TryKill(process);
            throw;
        }
        var error = await errorTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed: {Nonempty(error) ?? "unknown error"}");
        return await outputTask;
    }

    private static async Task WriteInputAsync(Process process, byte[] input, CancellationToken token)
    {
        await process.StandardInput.BaseStream.WriteAsync(input, token);
        await process.StandardInput.BaseStream.FlushAsync(token);
        process.StandardInput.Close();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximumBytes, CancellationToken token)
    {
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (output.Length + read > maximumBytes)
                throw new InvalidDataException($"Artwork tool output exceeded the {maximumBytes} byte memory limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
        }
        return output.ToArray();
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    private static string JsonPath(string path) => path.Replace(Path.DirectorySeparatorChar, '/');
    private static string? Nonempty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record LocalArtworkSelection(string Path, bool DerivesBookletPanel);
    private sealed record ImageSize(int Width, int Height);
}
