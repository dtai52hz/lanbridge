using System.Text.Json;

namespace LanBridge;

public enum ScreenEdge { Right, Left, Top, Bottom }

public sealed record Permissions
{
    public bool Input { get; set; } = true;
    public bool Clipboard { get; set; } = true;
    public bool Audio { get; set; }
    public bool Files { get; set; } = true;
    public bool Restart { get; set; }
    public bool Recording { get; set; }
    public bool BlockInput { get; set; }
    public bool Privacy { get; set; }
}

public sealed record Packet
{
    public string Type { get; set; } = "";
    public string? Text { get; set; }
    public string? Name { get; set; }
    public string? Id { get; set; }
    public byte[]? Data { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Dx { get; set; }
    public int Dy { get; set; }
    public int Key { get; set; }
    public int Scan { get; set; }
    public int Flags { get; set; }
    public bool Down { get; set; }
    public string? Button { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Rate { get; set; }
    public int Channels { get; set; }
    public int Bits { get; set; }
    public int Encoding { get; set; }
    public bool Allow { get; set; }
    public long Length { get; set; }
    public Permissions? Permissions { get; set; }
}

public static class WireJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
