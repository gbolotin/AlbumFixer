using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AlbumFixer.Core;

internal sealed record DffTag(
    string? Title, string? Album, string? Artist, string? AlbumArtist,
    uint Track, uint TrackCount, uint Disc, uint DiscCount, uint Year,
    string? Genre, string? Composer, byte[]? Picture, string? PictureMimeType,
    int SampleRate, int Channels);

internal sealed record DffTagUpdate(
    string? Title, string? Album, string? Artist, string? AlbumArtist,
    uint Track, uint TrackCount, uint Disc, uint DiscCount, uint Year,
    string? Genre, string? Composer, byte[]? Picture,
    string PictureMimeType = "image/jpeg");

/// <summary>
/// Fail-closed DSDIFF metadata support. DFF has no TagLibSharp provider and FFmpeg
/// only demuxes it, so repair replaces the conventional ID3 chunk in a staged copy
/// while preserving every native DSDIFF chunk and verifying the DSD payload hash.
/// </summary>
internal static class DffMetadata
{
    private const long MaximumMetadataChunk = 32L * 1024 * 1024;

    public static DffTag Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.SequentialScan);
        RequireRoot(stream, path);

        var id3 = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? nativeTitle = null;
        string? nativeArtist = null;
        byte[]? picture = null;
        string? pictureMime = null;
        var sampleRate = 0;
        var channels = 0;
        var hasDsd = false;

        while (stream.Position + 12 <= stream.Length)
        {
            var header = ReadHeader(stream, path);
            var payloadEnd = CheckedEnd(stream.Position, header.Size, stream.Length, path);
            switch (header.Id)
            {
                case "DSD ":
                    hasDsd = header.Size > 0;
                    break;
                case "DIIN":
                    ReadDiin(stream, payloadEnd, path, ref nativeTitle, ref nativeArtist);
                    break;
                case "PROP":
                    ReadProp(stream, payloadEnd, path, id3, ref picture, ref pictureMime, ref sampleRate, ref channels);
                    break;
                case "ID3 ":
                    ReadId3(stream, header.Size, path, id3, ref picture, ref pictureMime);
                    break;
            }
            stream.Position = payloadEnd + (long)(header.Size & 1);
        }

        if (!hasDsd) throw new InvalidDataException($"The DFF audio DSD chunk is missing or empty: {path}");
        var track = ParsePair(Value(id3, "TRCK"));
        var disc = ParsePair(Value(id3, "TPOS"));
        return new(
            Value(id3, "TIT2") ?? nativeTitle, Value(id3, "TALB"),
            Value(id3, "TPE1") ?? nativeArtist, Value(id3, "TPE2"),
            track.Number, track.Total, disc.Number, disc.Total,
            ParseYear(Value(id3, "TDRC") ?? Value(id3, "TYER")),
            Value(id3, "TCON"), Value(id3, "TCOM"), picture, pictureMime,
            sampleRate, channels);
    }

    public static async Task SaveAsync(string path, DffTagUpdate update, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        _ = Read(path);
        var temporary = path + $".albumfixer-dff-{Guid.NewGuid():N}.tmp";
        try
        {
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            RequireRoot(input, path);
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await output.WriteAsync("FRM8"u8.ToArray(), token);
            await output.WriteAsync(new byte[8], token);
            await output.WriteAsync("DSD "u8.ToArray(), token);

            while (input.Position + 12 <= input.Length)
            {
                var header = ReadHeader(input, path);
                var payloadEnd = CheckedEnd(input.Position, header.Size, input.Length, path);
                if (header.Id == "ID3 ")
                {
                    input.Position = payloadEnd + (long)(header.Size & 1);
                    continue;
                }

                if (header.Id == "PROP" && header.Size <= MaximumMetadataChunk)
                {
                    var payload = new byte[checked((int)header.Size)];
                    await input.ReadExactlyAsync(payload, token);
                    await WriteChunkAsync(output, "PROP", RemoveNestedId3FromProp(payload, path), token);
                    if ((header.Size & 1) != 0) input.Position++;
                    continue;
                }

                await WriteHeaderAsync(output, header.Id, header.Size, token);
                await CopyExactlyAsync(input, output, header.Size, token);
                if ((header.Size & 1) != 0)
                {
                    var padding = input.ReadByte();
                    if (padding < 0) throw new EndOfStreamException($"Missing DFF chunk padding in {path}.");
                    output.WriteByte((byte)padding);
                }
            }

            await WriteChunkAsync(output, "ID3 ", BuildId3(update), token);
            output.Position = 4;
            var rootSize = new byte[8];
            BinaryPrimitives.WriteUInt64BigEndian(rootSize, checked((ulong)(output.Length - 12)));
            await output.WriteAsync(rootSize, token);
            await output.FlushAsync(token);
            output.Close();
            input.Close();
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
            throw;
        }
    }

    public static async Task<string> AudioSha256Async(string path, CancellationToken token = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        RequireRoot(stream, path);
        while (stream.Position + 12 <= stream.Length)
        {
            var header = ReadHeader(stream, path);
            var payloadEnd = CheckedEnd(stream.Position, header.Size, stream.Length, path);
            if (header.Id != "DSD ")
            {
                stream.Position = payloadEnd + (long)(header.Size & 1);
                continue;
            }
            if (header.Size == 0) throw new InvalidDataException($"The DFF audio DSD chunk is empty: {path}");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            var remaining = header.Size;
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min((ulong)buffer.Length, remaining)), token);
                if (read == 0) throw new EndOfStreamException($"Unexpected end of DFF audio payload: {path}");
                hash.AppendData(buffer.AsSpan(0, read));
                remaining -= (ulong)read;
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        throw new InvalidDataException($"The DFF audio DSD chunk is missing: {path}");
    }

    private static void RequireRoot(Stream stream, string path)
    {
        Span<byte> root = stackalloc byte[16];
        stream.ReadExactly(root);
        if (!root[..4].SequenceEqual("FRM8"u8) || !root[12..16].SequenceEqual("DSD "u8))
            throw new InvalidDataException($"The file does not contain a native DSDIFF/DSD form: {path}");
        var declaredEnd = checked((long)BinaryPrimitives.ReadUInt64BigEndian(root[4..12]) + 12);
        if (declaredEnd != stream.Length)
            throw new InvalidDataException($"The DFF FRM8 size does not match the file length: {path}");
    }

    private static ChunkHeader ReadHeader(Stream stream, string path)
    {
        Span<byte> bytes = stackalloc byte[12];
        stream.ReadExactly(bytes);
        var id = Encoding.ASCII.GetString(bytes[..4]);
        if (id.Any(value => value < 0x20 || value > 0x7e))
            throw new InvalidDataException($"Invalid DFF chunk identifier in {path}.");
        return new(id, BinaryPrimitives.ReadUInt64BigEndian(bytes[4..12]));
    }

    private static long CheckedEnd(long start, ulong size, long length, string path)
    {
        if (size > long.MaxValue || start > length - (long)size)
            throw new InvalidDataException($"Invalid DFF chunk size in {path}.");
        return start + (long)size;
    }

    private static void ReadProp(Stream stream, long end, string path, IDictionary<string, string> id3,
        ref byte[]? picture, ref string? pictureMime, ref int sampleRate, ref int channels)
    {
        if (end - stream.Position < 4) throw new InvalidDataException($"Invalid DFF PROP chunk in {path}.");
        Span<byte> type = stackalloc byte[4];
        stream.ReadExactly(type);
        if (!type.SequenceEqual("SND "u8)) return;
        while (stream.Position + 12 <= end)
        {
            var child = ReadHeader(stream, path);
            var childEnd = CheckedEnd(stream.Position, child.Size, end, path);
            if (child.Id == "FS  " && child.Size == 4)
            {
                var value = new byte[4];
                stream.ReadExactly(value);
                sampleRate = checked((int)BinaryPrimitives.ReadUInt32BigEndian(value));
            }
            else if (child.Id == "CHNL" && child.Size >= 2)
            {
                var value = new byte[2];
                stream.ReadExactly(value);
                channels = BinaryPrimitives.ReadUInt16BigEndian(value);
            }
            else if (child.Id == "ID3 ")
                ReadId3(stream, child.Size, path, id3, ref picture, ref pictureMime);
            stream.Position = childEnd + (long)(child.Size & 1);
        }
    }

    private static void ReadDiin(Stream stream, long end, string path, ref string? title, ref string? artist)
    {
        while (stream.Position + 12 <= end)
        {
            var child = ReadHeader(stream, path);
            var childEnd = CheckedEnd(stream.Position, child.Size, end, path);
            if (child.Id is "DITI" or "DIAR" && child.Size >= 4 && child.Size <= MaximumMetadataChunk)
            {
                var size = new byte[4];
                stream.ReadExactly(size);
                var length = BinaryPrimitives.ReadUInt32BigEndian(size);
                if (length <= child.Size - 4)
                {
                    var bytes = new byte[length];
                    stream.ReadExactly(bytes);
                    var value = Clean(Encoding.UTF8.GetString(bytes));
                    if (child.Id == "DITI") title ??= value;
                    else artist ??= value;
                }
            }
            stream.Position = childEnd + (long)(child.Size & 1);
        }
    }

    private static void ReadId3(Stream stream, ulong chunkSize, string path, IDictionary<string, string> values,
        ref byte[]? picture, ref string? pictureMime)
    {
        if (chunkSize < 10 || chunkSize > MaximumMetadataChunk)
            throw new InvalidDataException($"Invalid DFF ID3 chunk size in {path}.");
        var bytes = new byte[checked((int)chunkSize)];
        stream.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 3).SequenceEqual("ID3"u8) || bytes[3] is not (3 or 4)) return;
        var declared = SyncSafe(bytes.AsSpan(6, 4));
        var end = Math.Min(bytes.Length, 10 + declared);
        var offset = 10;
        while (offset + 10 <= end)
        {
            var id = Encoding.ASCII.GetString(bytes, offset, 4);
            if (id.All(value => value == '\0')) break;
            var size = bytes[3] == 4
                ? SyncSafe(bytes.AsSpan(offset + 4, 4))
                : checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4, 4)));
            offset += 10;
            if (size < 0 || offset > end - size) break;
            var data = bytes.AsSpan(offset, size);
            if (id is "TIT2" or "TALB" or "TPE1" or "TPE2" or "TRCK" or "TPOS" or "TYER" or "TDRC" or "TCON" or "TCOM")
                values[id] = DecodeText(data);
            else if (id == "APIC")
                ReadPicture(data, ref picture, ref pictureMime);
            offset += size;
        }
    }

    private static void ReadPicture(ReadOnlySpan<byte> data, ref byte[]? picture, ref string? pictureMime)
    {
        if (data.Length < 5) return;
        var encoding = data[0];
        var mimeLength = data[1..].IndexOf((byte)0);
        if (mimeLength < 0) return;
        var mimeStart = 1;
        var mimeEnd = mimeStart + mimeLength;
        var mime = Encoding.ASCII.GetString(data[mimeStart..mimeEnd]);
        var position = mimeEnd + 2;
        if (position > data.Length) return;
        var terminator = encoding is 1 or 2 ? 2 : 1;
        var descriptionEnd = FindTerminator(data[position..], terminator);
        if (descriptionEnd < 0) return;
        position += descriptionEnd + terminator;
        if (position >= data.Length) return;
        picture = data[position..].ToArray();
        pictureMime = Clean(mime) ?? "image/jpeg";
    }

    private static byte[] BuildId3(DffTagUpdate update)
    {
        using var frames = new MemoryStream();
        WriteTextFrame(frames, "TIT2", update.Title);
        WriteTextFrame(frames, "TALB", update.Album);
        WriteTextFrame(frames, "TPE1", update.Artist);
        WriteTextFrame(frames, "TPE2", update.AlbumArtist);
        WriteTextFrame(frames, "TRCK", Pair(update.Track, update.TrackCount));
        WriteTextFrame(frames, "TPOS", Pair(update.Disc, update.DiscCount));
        WriteTextFrame(frames, "TYER", update.Year == 0 ? null : update.Year.ToString("0000"));
        WriteTextFrame(frames, "TCON", update.Genre);
        WriteTextFrame(frames, "TCOM", update.Composer);
        if (update.Picture is { Length: > 0 }) WritePictureFrame(frames, update.Picture, update.PictureMimeType);

        var payload = frames.ToArray();
        using var tag = new MemoryStream(payload.Length + 10);
        tag.Write("ID3"u8);
        tag.WriteByte(3);
        tag.WriteByte(0);
        tag.WriteByte(0);
        tag.Write(ToSyncSafe(payload.Length));
        tag.Write(payload);
        return tag.ToArray();
    }

    private static void WriteTextFrame(Stream output, string id, string? value)
    {
        value = Clean(value);
        if (value is null) return;
        var text = Encoding.Unicode.GetBytes(value);
        var payload = new byte[text.Length + 3];
        payload[0] = 1;
        payload[1] = 0xff;
        payload[2] = 0xfe;
        text.CopyTo(payload, 3);
        WriteFrame(output, id, payload);
    }

    private static void WritePictureFrame(Stream output, byte[] picture, string mimeType)
    {
        var mime = Encoding.ASCII.GetBytes(Clean(mimeType) ?? "image/jpeg");
        var description = Encoding.ASCII.GetBytes("Cover (front)");
        var payload = new byte[1 + mime.Length + 1 + 1 + description.Length + 1 + picture.Length];
        var offset = 1;
        mime.CopyTo(payload, offset);
        offset += mime.Length + 1;
        payload[offset++] = 3;
        description.CopyTo(payload, offset);
        offset += description.Length + 1;
        picture.CopyTo(payload, offset);
        WriteFrame(output, "APIC", payload);
    }

    private static void WriteFrame(Stream output, string id, byte[] payload)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(size, checked((uint)payload.Length));
        output.Write(size);
        output.WriteByte(0);
        output.WriteByte(0);
        output.Write(payload);
    }

    private static byte[] RemoveNestedId3FromProp(byte[] payload, string path)
    {
        if (payload.Length < 4 || !payload.AsSpan(0, 4).SequenceEqual("SND "u8)) return payload;
        using var result = new MemoryStream(payload.Length);
        result.Write(payload, 0, 4);
        var offset = 4;
        while (offset + 12 <= payload.Length)
        {
            var id = Encoding.ASCII.GetString(payload, offset, 4);
            var size = BinaryPrimitives.ReadUInt64BigEndian(payload.AsSpan(offset + 4, 8));
            var total = checked(12L + (long)size + ((long)size & 1));
            if (offset > payload.Length - total) throw new InvalidDataException($"Invalid nested PROP chunk in {path}.");
            if (id != "ID3 ") result.Write(payload, offset, checked((int)total));
            offset += checked((int)total);
        }
        if (offset != payload.Length) throw new InvalidDataException($"Trailing bytes in DFF PROP chunk in {path}.");
        return result.ToArray();
    }

    private static async Task WriteChunkAsync(Stream output, string id, byte[] payload, CancellationToken token)
    {
        await WriteHeaderAsync(output, id, checked((ulong)payload.Length), token);
        await output.WriteAsync(payload, token);
        if ((payload.Length & 1) != 0) output.WriteByte(0);
    }

    private static async Task WriteHeaderAsync(Stream output, string id, ulong size, CancellationToken token)
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes(id), token);
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, size);
        await output.WriteAsync(bytes, token);
    }

    private static async Task CopyExactlyAsync(Stream input, Stream output, ulong count, CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        while (count > 0)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min((ulong)buffer.Length, count)), token);
            if (read == 0) throw new EndOfStreamException("Unexpected end of DFF chunk.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            count -= (ulong)read;
        }
    }

    private static string DecodeText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var text = data[1..];
        var value = data[0] switch
        {
            0 => Encoding.Latin1.GetString(text),
            1 => DecodeUtf16(text),
            2 => Encoding.BigEndianUnicode.GetString(text),
            3 => Encoding.UTF8.GetString(text),
            _ => string.Empty
        };
        return Clean(value) ?? string.Empty;
    }

    private static string DecodeUtf16(ReadOnlySpan<byte> data) =>
        data.StartsWith(new byte[] { 0xff, 0xfe }) ? Encoding.Unicode.GetString(data[2..]) :
        data.StartsWith(new byte[] { 0xfe, 0xff }) ? Encoding.BigEndianUnicode.GetString(data[2..]) :
        Encoding.Unicode.GetString(data);

    private static int FindTerminator(ReadOnlySpan<byte> value, int width)
    {
        if (width == 1) return value.IndexOf((byte)0);
        for (var index = 0; index + 1 < value.Length; index += 2)
            if (value[index] == 0 && value[index + 1] == 0) return index;
        return -1;
    }

    private static int SyncSafe(ReadOnlySpan<byte> value) =>
        (value[0] & 0x7f) << 21 | (value[1] & 0x7f) << 14 | (value[2] & 0x7f) << 7 | value[3] & 0x7f;

    private static byte[] ToSyncSafe(int value) =>
    [
        (byte)((value >> 21) & 0x7f), (byte)((value >> 14) & 0x7f),
        (byte)((value >> 7) & 0x7f), (byte)(value & 0x7f)
    ];

    private static (uint Number, uint Total) ParsePair(string? value)
    {
        var parts = value?.Split('/', 2, StringSplitOptions.TrimEntries) ?? [];
        return (parts.Length > 0 && uint.TryParse(parts[0], out var number) ? number : 0,
            parts.Length > 1 && uint.TryParse(parts[1], out var total) ? total : 0);
    }

    private static uint ParseYear(string? value) =>
        value is { Length: >= 4 } && uint.TryParse(value[..4], out var year) ? year : 0;

    private static string? Pair(uint number, uint total) =>
        number == 0 ? null : total > 0 ? $"{number}/{total}" : number.ToString();

    private static string? Value(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? Clean(value) : null;

    private static string? Clean(string? value)
    {
        value = value?.Trim('\0', ' ', '\t', '\r', '\n');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private sealed record ChunkHeader(string Id, ulong Size);
}
