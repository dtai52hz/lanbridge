using LanBridge;
using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.IO.Compression;

// Synthetic images only. Never call Clipboard.Get*/Set*, even on the test machine.
int checks = 0;
using var source = new Bitmap(4, 3, PixelFormat.Format32bppArgb);
source.SetPixel(0, 0, Color.FromArgb(255, 231, 37, 91));
source.SetPixel(1, 0, Color.FromArgb(128, 255, 0, 0));
source.SetPixel(2, 0, Color.FromArgb(0, 0, 0, 0));
source.SetPixel(3, 2, Color.FromArgb(255, 7, 113, 211));
byte[] png = ClipboardImageCodec.Encode(source);
Assert(png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }), "encoder emits PNG");
ClipboardImageCodec.Validate(png);
using (var decoded = ClipboardImageCodec.Decode(png))
{
    Assert(decoded.Size == source.Size, "dimensions roundtrip");
    for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            Assert(decoded.GetPixel(x, y).ToArgb() == source.GetPixel(x, y).ToArgb(), $"RGBA pixel {x},{y}");
    // The returned bitmap remains usable after the codec has closed its decoder and stream.
    using var stream = new MemoryStream(); decoded.Save(stream, ImageFormat.Png);
    Assert(stream.Length > 0, "owned bitmap usable after decoder disposed");
    decoded.SetPixel(0, 0, Color.Green);
    Assert(source.GetPixel(0, 0).ToArgb() != decoded.GetPixel(0, 0).ToArgb(), "decoded storage independent of source");
}
var content = new ClipboardContent(ImagePng: png);
Assert(ReferenceEquals(content.ImagePng, png) && content.Text is null && content.Files is null, "image clipboard record");
IClipboardAccess fake = new NoImagesClipboard();
try { fake.SetImage(png); throw new Exception("Default provider accepted images"); }
catch (NotSupportedException) { checks++; }
Assert(fake.SequenceNumber == 0, "default image method leaves fake clipboard alone");

foreach (int length in new[] { 0, 1, 7, 8, 12, 20, 32, 44, png.Length - 1, png.Length - 12 })
    Reject(png[..Math.Min(length, png.Length)], "truncation " + length);
var corrupt = (byte[])png.Clone(); corrupt[0] = 0; Reject(corrupt, "bad signature");
corrupt = (byte[])png.Clone(); corrupt[8] = 127; Reject(corrupt, "oversize first chunk");
corrupt = (byte[])png.Clone(); corrupt[12] = (byte)'I'; corrupt[13] = (byte)'D'; Reject(corrupt, "IHDR must be first");
corrupt = (byte[])png.Clone(); corrupt[24] ^= 1; Reject(corrupt, "header checksum corruption");
corrupt = (byte[])png.Clone(); corrupt[^1] ^= 1; Reject(corrupt, "IEND checksum corruption");
corrupt = [.. png, 1]; Reject(corrupt, "trailing data");
Reject(new byte[ClipboardImageCodec.MaxEncodedBytes + 1], "encoded byte bound");

foreach ((uint width, uint height, string why) in new[]
{
    (0u, 1u, "zero width"), (1u, 0u, "zero height"),
    (16385u, 1u, "width bound"), (1u, 16385u, "height bound"),
    (6400u, 6400u, "total pixel bomb"), (uint.MaxValue, uint.MaxValue, "overflow dimensions")
})
    Reject(Header(width, height), why);
foreach ((uint width, uint height, string why) in new[]
{
    (16384u, 1u, "max width"), (1u, 16384u, "max height"),
    (8000u, 5000u, "40 million pixels"), (7680u, 4320u, "8K supported")
})
{
    ClipboardImageCodec.Validate(Header(width, height)); checks++;
    // Header-only validation intentionally avoids allocating huge native bitmaps.
}
foreach ((byte value, int index, string why) in new[]
{
    ((byte)0, 24, "invalid bit depth"), ((byte)5, 25, "invalid color"),
    ((byte)1, 26, "compression method"), ((byte)1, 27, "filter method"), ((byte)2, 28, "interlace method")
})
{
    corrupt = (byte[])png.Clone(); corrupt[index] = value; FixCrc(corrupt, 8); Reject(corrupt, why);
}
using (var jpeg = new MemoryStream())
{
    source.Save(jpeg, ImageFormat.Jpeg); Reject(jpeg.ToArray(), "JPEG cannot masquerade as PNG");
}
using (var bmp = new MemoryStream())
{
    source.Save(bmp, ImageFormat.Bmp); Reject(bmp.ToArray(), "BMP cannot masquerade as PNG");
}
using (var tooWide = new Bitmap(ClipboardImageCodec.MaxDimension + 1, 1))
{
    try { ClipboardImageCodec.Encode(tooWide); throw new Exception("Encoded image exceeding width bound"); }
    catch (InvalidDataException) { checks++; }
}

// Retain structurally valid PNG framing/CRC while corrupting the DEFLATE bytes.
int idat = FindChunk(png, "IDAT");
corrupt = (byte[])png.Clone();
int idatLength = (int)BinaryPrimitives.ReadUInt32BigEndian(corrupt.AsSpan(idat, 4));
Array.Clear(corrupt, idat + 8, idatLength); FixCrc(corrupt, idat);
Reject(corrupt, "invalid zlib pixels despite valid chunk CRC");

// Repeated decoding/disposal catches accidental dependence on closed stream handles.
for (int i = 0; i < 30; i++)
{
    using var bitmap = ClipboardImageCodec.Decode(png);
    Assert(bitmap.GetPixel(3, 2).ToArgb() == source.GetPixel(3, 2).ToArgb(), "repeated owned decode " + i);
}
Console.WriteLine($"ClipboardImageCodecSmoke: {checks} checks passed; synthetic images only, OS clipboard unchanged.");

void Assert(bool condition, string why) { if (!condition) throw new Exception(why); checks++; }
void Reject(byte[] bytes, string why)
{
    try { ClipboardImageCodec.Validate(bytes); throw new Exception("Accepted " + why); }
    catch (InvalidDataException) { checks++; }
    try { using var image = ClipboardImageCodec.Decode(bytes); throw new Exception("Decoded " + why); }
    catch (InvalidDataException) { checks++; }
}
byte[] Header(uint width, uint height)
{
    var result = (byte[])png.Clone();
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(16, 4), width);
    BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(20, 4), height);
    FixCrc(result, 8);
    if (width == 0 || height == 0 || width > ClipboardImageCodec.MaxDimension || height > ClipboardImageCodec.MaxDimension || (long)width * height > ClipboardImageCodec.MaxPixels)
        return result;
    // Stream a valid all-transparent synthetic image, even for 8K/40MP, with
    // only one row in memory and no native bitmap allocation.
    using var compressed = new MemoryStream();
    using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
    {
        var row = new byte[width * 4 + 1];
        for (int y = 0; y < height; y++) zlib.Write(row);
    }
    byte[] pixels = compressed.ToArray();
    var body = new byte[33 + pixels.Length + 24];
    result.AsSpan(0, 33).CopyTo(body);
    BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(33, 4), (uint)pixels.Length);
    "IDAT"u8.CopyTo(body.AsSpan(37, 4)); pixels.CopyTo(body, 41); FixCrc(body, 33);
    "IEND"u8.CopyTo(body.AsSpan(body.Length - 8, 4)); FixCrc(body, body.Length - 12);
    return body;
}
static int FindChunk(byte[] bytes, string name)
{
    for (int offset = 8; offset < bytes.Length; offset += 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4)))
        if (System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4) == name) return offset;
    throw new Exception("Missing " + name);
}
static void FixCrc(byte[] bytes, int offset)
{
    int length = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
    uint crc = uint.MaxValue;
    foreach (byte value in bytes.AsSpan(offset + 4, length + 4))
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320 : crc >> 1;
    }
    BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4), ~crc);
}
sealed class NoImagesClipboard : IClipboardAccess
{
    public uint SequenceNumber => 0;
    public ClipboardContent Read() => new();
    public void SetText(string value) => throw new Exception("Unexpected clipboard write");
    public void SetFiles(string[] paths) => throw new Exception("Unexpected clipboard write");
    public void Clear() => throw new Exception("Unexpected clipboard write");
}
