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
                var issue = mode == ArtworkSelectionMode.Flac
                    ? "No dedicated front-cover image or recognizable first booklet spread was found in the album scans."
                    : "SACD extraction requires local front-cover artwork.";
                return new(null, issue);
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
            var artwork = await NormalizeAsync(ffmpeg, ["-i", selection.Path], null, filter, output, description, token);
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
        var external = await PrepareExternalAsync(externalMetadata, musicBrainzReleaseId, ffmpeg, ffprobe, token);
        if (external.Artwork is not null) return external;
        return new(null, string.Join(" ", new[] { local.Issue, external.Issue }.Where(value => !string.IsNullOrWhiteSpace(value))));
    }

    public static TagLib.Picture CreatePicture(PreparedArtwork artwork) => new(new TagLib.ByteVector(artwork.JpegBytes))
    {
        Type = TagLib.PictureType.FrontCover,
        Description = "Cover (front)",
        MimeType = PreparedArtwork.MimeType
    };

    public static string? ReadFrontCoverSha256(string path)
    {
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
        arguments.AddRange(["-vf", filter, "-frames:v", "1", "-q:v", quality.ToString(), "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1"]);
        return RunBinaryProcessAsync(ffmpeg, arguments, standardInput, MaximumPipeOutputBytes, token);
    }

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
            .Where(path => new[] { ".jpg", ".jpeg", ".png" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            .Where(path => mode != ArtworkSelectionMode.Flac ||
                           !HostStagingService.SafeRelative(root, path).StartsWith($"Tracks{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => CoverRank(root, path, mode))
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mode == ArtworkSelectionMode.Dsd)
            return candidates.FirstOrDefault() is { } dsd ? new(dsd, false) : null;

        var explicitFront = candidates.FirstOrDefault(path => CoverRank(root, path, mode) < 2);
        var bookletFront = explicitFront is null ? candidates.FirstOrDefault(IsFirstBookletScan) : null;
        var source = explicitFront ?? bookletFront;
        return source is null ? null : new(source, bookletFront is not null);
    }

    private static int CoverRank(string root, string path, ArtworkSelectionMode mode)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.Equals("cover", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("folder", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("front", StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.Contains("front", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("cover", StringComparison.OrdinalIgnoreCase) && !name.Contains("back", StringComparison.OrdinalIgnoreCase)) return 1;
        if (mode == ArtworkSelectionMode.Flac) return IsFirstBookletScan(path) ? 2 : int.MaxValue;
        if (Path.GetDirectoryName(path)?.Contains($"{Path.DirectorySeparatorChar}Artwork", StringComparison.OrdinalIgnoreCase) == true) return 2;
        return Path.GetDirectoryName(path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? 3 : 4;
    }

    private static bool IsFirstBookletScan(string path)
    {
        var compact = Regex.Replace(Path.GetFileNameWithoutExtension(path), "[^a-z0-9]", string.Empty, RegexOptions.IgnoreCase);
        return compact.Equals("booklet1", StringComparison.OrdinalIgnoreCase) ||
               compact.Equals("booklet01", StringComparison.OrdinalIgnoreCase);
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
