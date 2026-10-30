using System.Buffers.Binary;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace LanBridge;

/// <summary>Bounded, lossless clipboard PNG encoding and decoding.</summary>
public static class ClipboardImageCodec
{
    public const int MaxEncodedBytes = 32 * 1024 * 1024;
    public const int MaxDimension = 16384;
    public const long MaxPixels = 40_000_000;
    static ReadOnlySpan<byte> Signature => [137, 80, 78, 71, 13, 10, 26, 10];
    static readonly uint[] CrcTable = CreateCrcTable();

    public static byte[] Encode(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        ValidateDimensions((uint)image.Width, (uint)image.Height);
        using var stream = new BoundedPngStream();
        try { image.Save(stream, ImageFormat.Png); }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        { throw new InvalidDataException("Cannot encode the clipboard image as a bounded PNG.", ex); }
        byte[] png = stream.ToArray();
        Validate(png);
        return png;
    }

    /// <summary>Returns a fully detached bitmap which the caller must dispose.</summary>
    public static Bitmap Decode(byte[] png)
    {
        // Inspect the PNG header before GDI+ sees untrusted dimensions.
        Validate(png);
        using var stream = new MemoryStream(png, writable: false);
        Bitmap? result = null;
        try
        {
            using var decoded = Image.FromStream(stream, useEmbeddedColorManagement: false, validateImageData: true);
            if (decoded.Width != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) ||
                decoded.Height != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)))
                throw new InvalidDataException("PNG dimensions do not match its header.");
            result = new Bitmap(decoded.Width, decoded.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(result))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImage(decoded, new Rectangle(0, 0, result.Width, result.Height),
                    0, 0, result.Width, result.Height, GraphicsUnit.Pixel);
            }
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException or InvalidDataException)
        {
            result?.Dispose();
            throw new InvalidDataException("The clipboard PNG is invalid or cannot be decoded.", ex);
        }
    }

    /// <summary>Checks PNG framing, dimensions and chunk integrity without allocating an image.</summary>
    public static void Validate(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length is < 45 or > MaxEncodedBytes || !png.AsSpan(0, 8).SequenceEqual(Signature))
            throw new InvalidDataException("Clipboard image must be a PNG no larger than 32 MiB.");
        if (BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8, 4)) != 13 || !png.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException("PNG must begin with a 13-byte IHDR.");
        ValidateDimensions(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)), BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        byte bitDepth = png[24], colorType = png[25];
        bool validColor = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => bitDepth is 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => false
        };
        if (!validColor || png[26] != 0 || png[27] != 0 || png[28] > 1)
            throw new InvalidDataException("Unsupported PNG color, compression, filter or interlace header.");

        bool hasData = false, dataEnded = false, hasPalette = false;
        for (int offset = 8; offset < png.Length;)
        {
            if (png.Length - offset < 12) throw new InvalidDataException("Truncated PNG chunk.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset, 4));
            if (length > (uint)(png.Length - offset - 12)) throw new InvalidDataException("Truncated PNG chunk payload.");
            int count = (int)length;
            var type = png.AsSpan(offset + 4, 4);
            foreach (byte character in type)
                if (character is not (>= (byte)'A' and <= (byte)'Z') and not (>= (byte)'a' and <= (byte)'z'))
                    throw new InvalidDataException("Invalid PNG chunk type.");
            if ((type[2] & 0x20) != 0) throw new InvalidDataException("Invalid PNG reserved chunk bit.");
            uint expected = BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + count, 4));
            if (Crc(png.AsSpan(offset + 4, count + 4)) != expected) throw new InvalidDataException("PNG chunk checksum mismatch.");
            bool idat = type.SequenceEqual("IDAT"u8);
            if (type.SequenceEqual("IHDR"u8))
            {
                if (offset != 8) throw new InvalidDataException("Duplicate PNG header.");
            }
            else if (type.SequenceEqual("PLTE"u8))
            {
                if (hasPalette || hasData || count is 0 or > 768 || count % 3 != 0 || colorType is 0 or 4)
                    throw new InvalidDataException("Invalid PNG palette.");
                hasPalette = true;
            }
            else if (idat)
            {
                if (dataEnded || (colorType == 3 && !hasPalette)) throw new InvalidDataException("Invalid PNG image data ordering.");
                hasData |= count != 0;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (count != 0 || !hasData || offset + 12 != png.Length) throw new InvalidDataException("Invalid PNG end chunk.");
                ValidatePixelData(png, bitDepth, colorType);
                return;
            }
            else if ((type[0] & 0x20) == 0)
                throw new InvalidDataException("Unsupported critical PNG chunk.");
            if (hasData && !idat) dataEnded = true;
            offset += count + 12;
        }
        throw new InvalidDataException("PNG end chunk is missing.");
    }

    static void ValidatePixelData(byte[] png, byte bitDepth, byte colorType)
    {
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
        int channels = colorType switch { 0 or 3 => 1, 2 => 3, 4 => 2, 6 => 4, _ => throw new InvalidDataException("Invalid PNG color type.") };
        using var chunks = new PngIdatStream(png);
        using var pixels = new ZLibStream(chunks, CompressionMode.Decompress);
        var buffer = new byte[64 * 1024];
        if (png[28] == 0) ValidatePass(width, height);
        else
        {
            // Adam7 pass geometry. The row filter restarts independently in each pass.
            int[] startX = [0, 4, 0, 2, 0, 1, 0], startY = [0, 0, 4, 0, 2, 0, 1];
            int[] stepX = [8, 8, 4, 4, 2, 2, 1], stepY = [8, 8, 8, 4, 4, 2, 2];
            for (int pass = 0; pass < 7; pass++)
                ValidatePass(Math.Max(0, (width - startX[pass] + stepX[pass] - 1) / stepX[pass]),
                    Math.Max(0, (height - startY[pass] + stepY[pass] - 1) / stepY[pass]));
        }
        if (pixels.ReadByte() != -1) throw new InvalidDataException("PNG contains more pixel data than its dimensions allow.");

        void ValidatePass(int passWidth, int passHeight)
        {
            if (passWidth == 0 || passHeight == 0) return;
            int rowLength = (passWidth * channels * bitDepth + 7) / 8;
            for (int row = 0; row < passHeight; row++)
            {
                int filter = pixels.ReadByte();
                if (filter is < 0 or > 4) throw new InvalidDataException("Invalid or missing PNG scanline filter.");
                int remaining = rowLength;
                while (remaining > 0)
                {
                    int read = pixels.Read(buffer, 0, Math.Min(remaining, buffer.Length));
                    if (read == 0) throw new InvalidDataException("Truncated PNG pixel data.");
                    remaining -= read;
                }
            }
        }
    }

    static void ValidateDimensions(uint width, uint height)
    {
        if (width is 0 or > MaxDimension || height is 0 or > MaxDimension || (long)width * height > MaxPixels)
            throw new InvalidDataException("Clipboard PNG dimensions exceed 16384 pixels or 40 million total pixels.");
    }

    static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint value = uint.MaxValue;
        foreach (byte item in bytes) value = CrcTable[(value ^ item) & 255] ^ (value >> 8);
        return ~value;
    }

    static uint[] CreateCrcTable()
    {
        var result = new uint[256];
        for (uint i = 0; i < result.Length; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xedb88320 : value >> 1;
            result[i] = value;
        }
        return result;
    }

    // Stop the encoder at the byte limit, rather than growing an unbounded
    // MemoryStream and only noticing the limit after Image.Save has finished.
    sealed class BoundedPngStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { Check(1); base.WriteByte(value); }
        public override void SetLength(long value) { if (value > MaxEncodedBytes) throw TooLarge(); base.SetLength(value); }
        void Check(int count) { if (Position + count > MaxEncodedBytes) throw TooLarge(); }
        static InvalidDataException TooLarge() => new("Clipboard PNG exceeds 32 MiB.");
    }

    // Present IDAT payloads as a single read-only stream, without duplicating a
    // potentially 32 MiB encoded image or allocating per-chunk collections.
    sealed class PngIdatStream(byte[] png) : Stream
    {
        int nextChunk = 8, position, remaining;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                while (remaining == 0)
                {
                    if (nextChunk >= png.Length) return total;
                    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(nextChunk, 4));
                    bool idat = png.AsSpan(nextChunk + 4, 4).SequenceEqual("IDAT"u8);
                    position = nextChunk + 8; nextChunk += length + 12;
                    if (idat) remaining = length;
                }
                int copy = Math.Min(remaining, buffer.Length - total);
                png.AsSpan(position, copy).CopyTo(buffer[total..]);
                position += copy; remaining -= copy; total += copy;
            }
            return total;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
