using System.Security;
using System.Text.Json;

namespace LanBridge;

/// <summary>Local convenience options only. Connection credentials and permission grants are never persisted.</summary>
public sealed record UserPreferences
{
    public ScreenEdge Edge { get; init; } = ScreenEdge.Right;
    public TypingMode TypingMode { get; init; } = TypingMode.VietnameseTelex;
    public int MouseSpeedPercent { get; init; } = 100;
    public bool MinimizeToTray { get; init; }
    public SwitchShortcut? SwitchShortcut { get; init; }
}

/// <summary>
/// Bounded, per-user preferences. Reading malformed settings returns safe defaults; saving publishes
/// a complete file with a same-directory rename and never truncates the previous settings in place.
/// </summary>
public sealed class UserPreferencesStore
{
    public const int MinimumMouseSpeedPercent = 50;
    public const int MaximumMouseSpeedPercent = 200;
    public const int MaximumFileBytes = 64 * 1024;
    private const int CurrentSchemaVersion = 1;
    private readonly string _path;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge", "preferences.json");

    /// <summary>Constructing a store does not create directories or read/write the file.</summary>
    public UserPreferencesStore(string? path = null) => _path = Path.GetFullPath(path ?? DefaultPath);

    public UserPreferences Load()
    {
        try
        {
            using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > MaximumFileBytes) return new();
            var bytes = new byte[checked((int)file.Length)];
            file.ReadExactly(bytes);
            // If a concurrent writer grew the file, do not parse a misleading prefix.
            if (file.ReadByte() != -1) return new();
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new();
            if (root.TryGetProperty("schemaVersion", out var version)
                && (!TryInteger(version, out int schemaVersion) || schemaVersion != CurrentSchemaVersion)) return new();

            return new UserPreferences
            {
                Edge = ReadEnum(root, "edge", ScreenEdge.Right),
                TypingMode = ReadEnum(root, "typingMode", TypingMode.VietnameseTelex),
                MouseSpeedPercent = root.TryGetProperty("mouseSpeedPercent", out var speed)
                    && TryInteger(speed, out int percent) && ValidSpeed(percent) ? percent : 100,
                MinimizeToTray = root.TryGetProperty("minimizeToTray", out var tray) && tray.ValueKind == JsonValueKind.True,
                SwitchShortcut = ReadShortcut(root),
            };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or JsonException)
        {
            return new();
        }
    }

    public bool TrySave(UserPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        string? temporaryPath = null;
        try
        {
            string directory = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = CurrentSchemaVersion,
                edge = (Enum.IsDefined(preferences.Edge) ? preferences.Edge : ScreenEdge.Right).ToString(),
                typingMode = (Enum.IsDefined(preferences.TypingMode) ? preferences.TypingMode : TypingMode.VietnameseTelex).ToString(),
                mouseSpeedPercent = ValidSpeed(preferences.MouseSpeedPercent) ? preferences.MouseSpeedPercent : 100,
                minimizeToTray = preferences.MinimizeToTray,
                switchShortcut = preferences.SwitchShortcut is { IsValid: true } shortcut
                    ? new { key = shortcut.Key, modifiers = (int)shortcut.Modifiers } : null,
            }, JsonOptions);
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { }
            }
        }
    }

    private static bool ValidSpeed(int percent) => percent >= MinimumMouseSpeedPercent && percent <= MaximumMouseSpeedPercent;

    private static SwitchShortcut? ReadShortcut(JsonElement root)
    {
        if (!root.TryGetProperty("switchShortcut", out var value) || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("key", out var key) || !TryInteger(key, out int keyCode)
            || !value.TryGetProperty("modifiers", out var modifiers) || !TryInteger(modifiers, out int modifierFlags)) return null;
        return SwitchShortcut.TryCreate(keyCode, (ShortcutModifiers)modifierFlags, out var shortcut) ? shortcut : null;
    }

    private static bool TryInteger(JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }

    private static T ReadEnum<T>(JsonElement root, string name, T fallback) where T : struct, Enum
    {
        if (!root.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind == JsonValueKind.String && Enum.TryParse<T>(value.GetString(), ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)) return parsed;
        if (TryInteger(value, out int number))
        {
            var parsedNumber = (T)Enum.ToObject(typeof(T), number);
            if (Enum.IsDefined(parsedNumber)) return parsedNumber;
        }
        return fallback;
    }
}
