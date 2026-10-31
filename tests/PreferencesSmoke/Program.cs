using System.Text.Json;
using LanBridge;

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
var defaults = new UserPreferences();
var testDirectory = Path.Combine(Path.GetTempPath(), "LanBridge-PreferencesSmoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
try
{
    string path = Path.Combine(testDirectory, "nested", "preferences.json");
    var store = new UserPreferencesStore(path);
    Check(!Directory.Exists(Path.GetDirectoryName(path)), "Constructing a preferences store created a directory.");
    Check(store.Load() == defaults && !File.Exists(path), "Missing preferences should use defaults without writing a file.");
    Check(defaults.Edge == ScreenEdge.Right && defaults.TypingMode == TypingMode.VietnameseTelex
        && defaults.MouseSpeedPercent == 100 && !defaults.MinimizeToTray, "Convenience defaults changed existing behavior.");

    foreach (var edge in Enum.GetValues<ScreenEdge>())
    foreach (var typing in Enum.GetValues<TypingMode>())
    foreach (int speed in new[] { 50, 75, 100, 150, 200 })
    foreach (bool tray in new[] { false, true })
    {
        var expected = new UserPreferences { Edge = edge, TypingMode = typing, MouseSpeedPercent = speed, MinimizeToTray = tray };
        Check(store.TrySave(expected), "Valid preferences could not be saved.");
        Check(store.Load() == expected, "A valid preference did not survive a save/load round trip.");
    }
    using (var document = JsonDocument.Parse(File.ReadAllBytes(path)))
    {
        var expectedFields = new HashSet<string> { "schemaVersion", "edge", "typingMode", "mouseSpeedPercent", "minimizeToTray" };
        Check(document.RootElement.EnumerateObject().All(property => expectedFields.Remove(property.Name)) && expectedFields.Count == 0,
            "Saved settings contain fields outside the convenience-only allowlist.");
        Check(document.RootElement.GetProperty("schemaVersion").GetInt32() == 1, "Settings schema version is missing.");
    }
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "A successful save left temporary files behind.");

    void Write(string json) => File.WriteAllText(path, json);
    foreach (string malformed in new[] { "", "{", "null", "[]", "true", "42", "\"settings\"", "{\"edge\":\"Top\",}", new string('[', 17) + new string(']', 17) })
    {
        Write(malformed);
        Check(store.Load() == defaults, "Malformed or non-object settings were not reset safely.");
    }
    Write("{\"schemaVersion\":2,\"edge\":\"Top\",\"mouseSpeedPercent\":150,\"minimizeToTray\":true}");
    Check(store.Load() == defaults, "Unknown future schema must not apply potentially incompatible settings.");
    foreach (string invalidVersion in new[] { "-1", "0", "null", "true", "\"1\"", "1.5", "2147483648" })
    {
        Write($"{{\"schemaVersion\":{invalidVersion},\"edge\":\"Top\"}}");
        Check(store.Load() == defaults, "An invalid schema version was accepted.");
    }
    Write("{}");
    Check(store.Load() == defaults, "An empty object must use defaults.");
    Write("{\"edge\":\"Top\",\"typingMode\":1,\"mouseSpeedPercent\":150,\"minimizeToTray\":true,\"pairingCode\":\"must-not-persist\"}");
    var partial = new UserPreferences { Edge = ScreenEdge.Top, TypingMode = TypingMode.VietnameseVni, MouseSpeedPercent = 150, MinimizeToTray = true };
    Check(store.Load() == partial, "Valid fields or forward-compatible unknown fields were mishandled.");
    Check(store.TrySave(store.Load()) && !File.ReadAllText(path).Contains("pairingCode", StringComparison.Ordinal),
        "Saving loaded settings retained an unknown secret field.");

    foreach (string invalidEdge in new[] { "\"sideways\"", "5", "-1", "null", "true", "{}" })
    {
        Write($"{{\"edge\":{invalidEdge},\"typingMode\":\"VietnameseVni\",\"mouseSpeedPercent\":150,\"minimizeToTray\":true}}");
        Check(store.Load() == partial with { Edge = ScreenEdge.Right }, "An invalid edge discarded unrelated valid preferences.");
    }
    foreach (string invalidTyping in new[] { "\"other\"", "3", "-1", "null", "false", "[]" })
    {
        Write($"{{\"edge\":\"Top\",\"typingMode\":{invalidTyping},\"mouseSpeedPercent\":150,\"minimizeToTray\":true}}");
        Check(store.Load() == partial with { TypingMode = TypingMode.VietnameseTelex }, "An invalid typing mode discarded unrelated valid preferences.");
    }
    foreach (string invalidSpeed in new[] { "49", "201", "0", "-100", "2147483648", "100.5", "1e100", "\"100\"", "null", "true", "{}" })
    {
        Write($"{{\"edge\":\"Top\",\"typingMode\":\"VietnameseVni\",\"mouseSpeedPercent\":{invalidSpeed},\"minimizeToTray\":true}}");
        Check(store.Load() == partial with { MouseSpeedPercent = 100 }, "An invalid speed discarded unrelated valid preferences.");
    }
    foreach (string invalidTray in new[] { "1", "\"true\"", "null", "{}" })
    {
        Write($"{{\"edge\":\"Top\",\"typingMode\":\"VietnameseVni\",\"mouseSpeedPercent\":150,\"minimizeToTray\":{invalidTray}}}");
        Check(store.Load() == partial with { MinimizeToTray = false }, "An invalid tray preference discarded unrelated valid preferences.");
    }
    Write("{\"edge\":\"bOtToM\",\"typingMode\":\"physical\"}");
    Check(store.Load() == defaults with { Edge = ScreenEdge.Bottom, TypingMode = TypingMode.Physical }, "Readable enum names should be case-insensitive.");

    File.WriteAllBytes(path, new byte[UserPreferencesStore.MaximumFileBytes + 1]);
    Check(store.Load() == defaults, "Oversized settings were accepted.");
    var invalid = new UserPreferences { Edge = (ScreenEdge)999, TypingMode = (TypingMode)(-1), MouseSpeedPercent = int.MaxValue, MinimizeToTray = true };
    Check(store.TrySave(invalid) && store.Load() == defaults with { MinimizeToTray = true }, "Save did not normalize invalid typed values.");

    // A held destination rejects replacement on Windows: the previous complete file must survive.
    Check(store.TrySave(partial), "Could not prepare destination-lock test.");
    using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        Check(!store.TrySave(defaults), "Replacing a non-delete-shared destination unexpectedly succeeded.");
    }
    Check(store.Load() == partial, "A failed replacement damaged previous preferences.");
    Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "A failed save left temporary files behind.");

    string blockedParent = Path.Combine(testDirectory, "file-instead-of-directory");
    File.WriteAllText(blockedParent, "keep me");
    var blocked = new UserPreferencesStore(Path.Combine(blockedParent, "preferences.json"));
    Check(!blocked.TrySave(partial) && blocked.Load() == defaults, "An unwritable parent should fail without throwing.");
    Check(File.ReadAllText(blockedParent) == "keep me", "A failed save modified an unrelated file.");

    // Repeated readers may see either complete version, never an in-place truncation or mixed document.
    var concurrentFirst = partial;
    var concurrentSecond = new UserPreferences { Edge = ScreenEdge.Bottom, TypingMode = TypingMode.Physical, MouseSpeedPercent = 75 };
    Check(store.TrySave(concurrentFirst), "Could not prepare concurrent publication test.");
    using var stopReading = new CancellationTokenSource();
    int invalidReads = 0;
    var reader = Task.Run(() =>
    {
        while (!stopReading.IsCancellationRequested)
        {
            var current = store.Load();
            if (current != concurrentFirst && current != concurrentSecond) Interlocked.Increment(ref invalidReads);
        }
    });
    int successfulSaves = 0;
    for (int index = 0; index < 30; index++)
        if (store.TrySave(index % 2 == 0 ? concurrentFirst : concurrentSecond)) successfulSaves++;
    stopReading.Cancel();
    await reader;
    Check(successfulSaves > 0, "No settings document could be published while a reader was active.");
    Check(invalidReads == 0, "A reader observed a partially published settings document.");
    Console.WriteLine($"Preferences smoke passed: {assertions} assertions; isolated temporary files only.");
}
finally
{
    Directory.Delete(testDirectory, recursive: true);
}
