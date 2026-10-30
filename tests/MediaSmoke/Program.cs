using System.Text;
using LanBridge;

// Synthetic images only: this test never reads a display, starts audio, or installs hooks.
var path = args.Length == 1 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), $"LanBridge-media-{Guid.NewGuid():N}.avi");
var colors = new[] { Color.Red, Color.Lime, Color.Blue, Color.Yellow, Color.Cyan, Color.Magenta, Color.White, Color.Black };
using (var writer = new MjpegAviWriter(path, 64, 48, 8))
{
    try { writer.AddFrame([1, 2, 3, 4]); throw new Exception("Invalid JPEG was accepted."); }
    catch (InvalidDataException) { }
    foreach (var color in colors)
    {
        using var image = new Bitmap(64, 48);
        using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
        var jpeg = DesktopCapture.EncodeJpeg(image);
        // Exercise both even-sized chunks and a padded odd-sized chunk.
        if (color == Color.Red && jpeg.Length % 2 == 0)
            jpeg = [.. jpeg[..^2], 0xFF, 0xFE, 0, 3, 0, .. jpeg[^2..]]; // Valid one-byte JPEG comment.
        writer.AddFrame(jpeg);
    }
}
var bytes = File.ReadAllBytes(path);
uint U32(int position) => BitConverter.ToUInt32(bytes, position);
string Fcc(int position) => Encoding.ASCII.GetString(bytes, position, 4);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Check(Fcc(0) == "RIFF" && Fcc(8) == "AVI ", "Missing RIFF AVI signature.");
Check(U32(4) == bytes.Length - 8, "RIFF length was not finalized.");
var chunks = new List<(string Id, int Start, int Length)>();
void Walk(int start, int end)
{
    while (start < end)
    {
        Check(start + 8 <= end, "Truncated chunk header.");
        var id = Fcc(start);
        var size = checked((int)U32(start + 4));
        Check(size >= 0 && start + 8L + size <= end, "Chunk extends beyond parent.");
        chunks.Add((id, start, size));
        if (id == "LIST") Walk(start + 12, start + 8 + size);
        start = checked(start + 8 + size + (size % 2));
    }
    Check(start == end, "RIFF padding is invalid.");
}
Walk(12, bytes.Length);
var main = chunks.Single(c => c.Id == "avih");
Check(main.Length == 56 && U32(main.Start + 8) == 125000, "Incorrect AVI timing header.");
Check(U32(main.Start + 8 + 16) == 8, "Main frame count was not finalized.");
Check(U32(main.Start + 8 + 32) == 64 && U32(main.Start + 8 + 36) == 48, "Incorrect dimensions.");
var stream = chunks.Single(c => c.Id == "strh");
Check(stream.Length == 56 && Fcc(stream.Start + 8) == "vids" && Fcc(stream.Start + 12) == "MJPG", "Incorrect video stream.");
Check(U32(stream.Start + 8 + 32) == 8, "Video stream frame count was not finalized.");
var format = chunks.Single(c => c.Id == "strf");
Check(format.Length == 40 && Fcc(format.Start + 8 + 16) == "MJPG", "Incorrect bitmap format.");
var frames = chunks.Where(c => c.Id == "00dc").ToArray();
Check(frames.Length == 8, "Unexpected frame count.");
var index = chunks.Single(c => c.Id == "idx1");
Check(index.Length == 8 * 16, "Missing or incomplete index.");
var movi = chunks.Single(c => c.Id == "LIST" && Fcc(c.Start + 8) == "movi");
for (var i = 0; i < frames.Length; i++)
{
    var entry = index.Start + 8 + i * 16;
    Check(Fcc(entry) == "00dc" && U32(entry + 4) == 0x10, "Index entry is not a key frame.");
    Check(movi.Start + 8 + U32(entry + 8) == frames[i].Start, "Index offset does not point to its frame.");
    Check(U32(entry + 12) == frames[i].Length, "Index length differs from frame length.");
    using var memory = new MemoryStream(bytes, frames[i].Start + 8, frames[i].Length);
    using var decoded = new Bitmap(memory);
    Check(decoded.Width == 64 && decoded.Height == 48, "JPEG does not decode at stream dimensions.");
    var actual = decoded.GetPixel(32, 24);
    Check(Math.Abs(actual.R - colors[i].R) < 8 && Math.Abs(actual.G - colors[i].G) < 8 && Math.Abs(actual.B - colors[i].B) < 8,
        "JPEG color order is incorrect.");
}
Console.WriteLine("PASS: RIFF sizes, timing, frame counts, format, padding, idx1 offsets and eight decoded JPEG frames.");
Console.WriteLine(path);
