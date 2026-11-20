using System.Buffers.Binary;

namespace AxamlLs.Analysis;

/// <summary>An image's size in pixels.</summary>
public readonly record struct ImageSize(int Width, int Height)
{
    public override string ToString() => $"{Width}×{Height}";
}

/// <summary>
/// Reads an image's pixel size from its header, without decoding it: PNG, JPEG, GIF, BMP, ICO and WebP. Returns null for
/// other formats and for files whose header is cut short or malformed.
/// </summary>
public static class ImageInfo
{
    private const int HeaderBytes = 32;

    public static ImageSize? Read(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Read(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <param name="stream">The image, positioned at its start; JPEG needs it to be seekable to skip segments.</param>
    public static ImageSize? Read(Stream stream)
    {
        Span<byte> head = stackalloc byte[HeaderBytes];
        var n = stream.ReadAtLeast(head, HeaderBytes, throwOnEndOfStream: false);
        head = head[..n];
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G'])) return Png(head);
        if (head.StartsWith("GIF8"u8)) return n >= 10 ? Positive(BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..])) : null;
        if (head.StartsWith("BM"u8)) return Bmp(head);
        if (head.StartsWith("RIFF"u8) && n >= 30 && head[8..12].SequenceEqual("WEBP"u8)) return WebP(head);
        if (head.StartsWith((ReadOnlySpan<byte>)[0, 0, 1, 0])) return Ico(stream, head);
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8])) return Jpeg(stream);
        return null;
    }

    private static ImageSize? Png(ReadOnlySpan<byte> head) =>
        head.Length >= 24 && head[12..16].SequenceEqual("IHDR"u8)
            ? Positive(BinaryPrimitives.ReadInt32BigEndian(head[16..]), BinaryPrimitives.ReadInt32BigEndian(head[20..]))
            : null;

    private static ImageSize? Bmp(ReadOnlySpan<byte> head)
    {
        if (head.Length < 26) return null;
        // BITMAPCOREHEADER (12 bytes) has 16-bit sizes; every later header has 32-bit ones, the height negative when top-down.
        return BinaryPrimitives.ReadUInt32LittleEndian(head[14..]) == 12
            ? Positive(BinaryPrimitives.ReadUInt16LittleEndian(head[18..]), BinaryPrimitives.ReadUInt16LittleEndian(head[20..]))
            : Positive(BinaryPrimitives.ReadInt32LittleEndian(head[18..]), Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(head[22..])));
    }

    private static ImageSize? WebP(ReadOnlySpan<byte> head)
    {
        var chunk = head[12..16];
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Key frame start code, then 14-bit width and height.
            if (!head[23..26].SequenceEqual((ReadOnlySpan<byte>)[0x9D, 0x01, 0x2A])) return null;
            return Positive(BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            if (head[20] != 0x2F) return null;
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return Positive((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }
        if (chunk.SequenceEqual("VP8X"u8))
            return Positive(1 + (head[24] | head[25] << 8 | head[26] << 16), 1 + (head[27] | head[28] << 8 | head[29] << 16));
        return null;
    }

    /// <summary>The largest picture of the icon directory (a width or height byte of 0 means 256).</summary>
    private static ImageSize? Ico(Stream stream, ReadOnlySpan<byte> head)
    {
        if (head.Length < 6) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(head[4..]);
        if (count == 0) return null;
        var directory = new byte[6 + count * 16];
        head.CopyTo(directory);
        var read = head.Length + stream.ReadAtLeast(directory.AsSpan(head.Length), directory.Length - head.Length, throwOnEndOfStream: false);
        ImageSize? best = null;
        for (var entry = 6; entry + 16 <= read; entry += 16)
        {
            var size = new ImageSize(directory[entry] == 0 ? 256 : directory[entry], directory[entry + 1] == 0 ? 256 : directory[entry + 1]);
            if (best == null || size.Width * size.Height > best.Value.Width * best.Value.Height) best = size;
        }
        return best;
    }

    /// <summary>Walks the segments (skipping each by its length) to the first start-of-frame marker.</summary>
    private static ImageSize? Jpeg(Stream stream)
    {
        if (!stream.CanSeek) return null;
        stream.Position = 2;
        Span<byte> segment = stackalloc byte[7];
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) return null;
            if (b != 0xFF) return null; // not at a marker: malformed
            var marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte(); // fill bytes
            if (marker < 0 || marker == 0xD9 || marker == 0xDA) return null; // end of image, or scan data before any frame
            if (marker is 0x01 or (>= 0xD0 and <= 0xD7)) continue; // no length
            if (stream.ReadAtLeast(segment[..2], 2, throwOnEndOfStream: false) < 2) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(segment);
            if (length < 2) return null;
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (stream.ReadAtLeast(segment[2..7], 5, throwOnEndOfStream: false) < 5) return null;
                return Positive(BinaryPrimitives.ReadUInt16BigEndian(segment[5..]), BinaryPrimitives.ReadUInt16BigEndian(segment[3..]));
            }
            stream.Seek(length - 2, SeekOrigin.Current);
        }
    }

    private static ImageSize? Positive(int width, int height) => width > 0 && height > 0 ? new ImageSize(width, height) : null;
}
