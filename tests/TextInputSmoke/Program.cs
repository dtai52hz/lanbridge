using System.Text.Json;
using LanBridge;

// Pure composition and protocol tests. Never install hooks, inject input, focus a window,
// inspect the clipboard, or require a second computer.
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

string Apply(string displayed, Packet packet)
{
    Check(packet.Type == "text", "Composer must use the Unicode text-edit wire message.");
    Check(packet.Dx >= 0 && packet.Dx <= displayed.Length,
        $"Edit attempted to delete {packet.Dx} characters from a {displayed.Length}-character document.");
    return displayed[..(displayed.Length - packet.Dx)] + packet.Text;
}

string Compose(string keys, string expected, TypingMode mode)
{
    var composer = new VietnameseComposer { Mode = mode };
    string displayed = "";
    foreach (char key in keys)
    {
        Packet original = composer.Process(key);
        // Exercise the actual JSON contract: accents must survive serialization and decoding.
        string json = JsonSerializer.Serialize(original, WireJson.Options);
        Packet decoded = JsonSerializer.Deserialize<Packet>(json, WireJson.Options)!;
        displayed = Apply(displayed, decoded);
    }
    Check(displayed == expected, $"{mode}: '{keys}' became '{displayed}', expected '{expected}'.");
    return displayed;
}

// Standard Telex letter substitutions, tone marks, consonant exclusions, and compound vowels.
foreach (var (keys, expected) in new (string, string)[]
{
    ("tieengs", "tiếng"), ("Vieetj", "Việt"), ("tooi", "tôi"),
    ("ddieeuf", "điều"), ("khieenr", "khiển"), ("chuootj", "chuột"),
    ("bafn", "bàn"), ("phims", "phím"), ("dduowngf", "đường"),
    ("nguowif", "người"), ("quoocs", "quốc"), ("giowis", "giới"),
    ("thuyr", "thủy"), ("hoaf", "hòa"), ("tieensg", "tiếng"),
    ("ass", "as"), ("aaa", "aa"), ("ddd", "dd"),
    ("a\u0301", "á"), ("e\u0302\u0301", "ế"),
    ("TIEENGS VIEETJ", "TIẾNG VIỆT"),
    ("tieengs Vieetj, tooi ddieeuf khieenr chuootj.", "tiếng Việt, tôi điều khiển chuột."),
}) Compose(keys, expected, TypingMode.VietnameseTelex);

// VNI 1..5 tones, 6 circumflex, 7 horn, 8 breve, and 9 crossed d.
foreach (var (keys, expected) in new (string, string)[]
{
    ("tie6ng1", "tiếng"), ("Vie6t5", "Việt"), ("to6i", "tôi"),
    ("d9ie6u2", "điều"), ("khie6n3", "khiển"), ("chuo6t5", "chuột"),
    ("ba2n", "bàn"), ("phi1m", "phím"), ("d9u7o7ng2", "đường"),
    ("ngu7o7i2", "người"), ("quo6c1", "quốc"), ("gio7i1", "giới"),
    ("thuy3", "thủy"), ("hoa2", "hòa"), ("d9a8ng", "đăng"),
    ("a11", "a1"), ("a66", "a6"),
    ("TIE6NG1 VIE6T5", "TIẾNG VIỆT"),
    ("tie6ng1 Vie6t5, to6i d9ie6u2 khie6n3 chuo6t5.", "tiếng Việt, tôi điều khiển chuột."),
}) Compose(keys, expected, TypingMode.VietnameseVni);

// Incremental edits must correct the already displayed word, not type an entire duplicate word.
{
    var composer = new VietnameseComposer();
    string displayed = "";
    foreach (char key in "tieeng") displayed = Apply(displayed, composer.Process(key));
    Check(displayed == "tiêng", "Pre-tone Telex shape is incorrect.");
    Packet tone = composer.Process('s');
    Check(tone.Dx == 3 && tone.Text == "ếng", "Tone edit must replace only the changed suffix.");
    displayed = Apply(displayed, tone);
    Check(displayed == "tiếng", "Tone correction changed another part of the word.");
    displayed = Apply(displayed, composer.Backspace());
    Check(displayed == "tiến" && composer.CurrentWord == "tiến", "Backspace must remove the displayed g, preserving the vowel's accents.");
    displayed = Apply(displayed, composer.Process('g'));
    Check(displayed == "tiếng", "Typing after Backspace did not resume the displayed word.");
}

// Removing the final consonant reflows the tone to its correct open-syllable position.
{
    var composer = new VietnameseComposer();
    string displayed = "";
    foreach (char key in "hoans") displayed = Apply(displayed, composer.Process(key));
    Check(displayed == "hoán", "Closed-syllable tone position is incorrect.");
    displayed = Apply(displayed, composer.Backspace());
    Check(displayed == "hóa", "Backspace did not reflow the tone after removing a consonant.");
}

// Mouse movement/selection changes and direct shortcuts reset composition without deleting text.
{
    var composer = new VietnameseComposer();
    string displayed = "";
    foreach (char key in "tieengs") displayed = Apply(displayed, composer.Process(key));
    composer.Reset();
    Check(composer.CurrentWord.Length == 0, "Reset must discard the word's composition context.");
    displayed += " | "; // A click moved B's caret into a new text field/document position.
    foreach (char key in "ddawng") displayed = Apply(displayed, composer.Process(key));
    Check(displayed == "tiếng | đăng", "Composition after a reset erased unrelated text.");
    composer.Reset();
    Packet backspace = composer.Backspace();
    Check(backspace.Dx == 1 && string.IsNullOrEmpty(backspace.Text), "Backspace at a reset boundary must delete a remote character.");
    displayed = Apply(displayed, backspace);
    Check(displayed == "tiếng | đăn", "Boundary Backspace did not affect the remote document once.");
}

// Mode changes must not carry an old word into a new input scheme.
{
    var composer = new VietnameseComposer();
    composer.Process('a'); composer.Process('s');
    composer.Mode = TypingMode.VietnameseVni;
    Check(composer.CurrentWord.Length == 0, "Changing typing modes must reset composition.");
    string displayed = "existing: ";
    foreach (char key in "a1") displayed = Apply(displayed, composer.Process(key));
    Check(displayed == "existing: á", "A mode change modified preceding content.");
}

foreach (string punctuation in new[] { " ", ",", ".", "!", "?", ":", ";", "-", "(", ")" })
{
    var composer = new VietnameseComposer();
    string displayed = "";
    foreach (char key in "as" + punctuation) displayed = Apply(displayed, composer.Process(key));
    Check(displayed == "á" + punctuation && composer.CurrentWord.Length == 0,
        $"'{punctuation}' did not commit the previous composition word.");
    displayed = Apply(displayed, composer.Process('s'));
    Check(displayed == "á" + punctuation + "s", "A modifier crossed a word boundary.");
}

// Long unbroken input must keep bounded context without erasing already typed characters.
{
    var composer = new VietnameseComposer();
    string displayed = "";
    foreach (char key in new string('b', 513))
    {
        displayed = Apply(displayed, composer.Process(key));
        Check(composer.CurrentWord.Length <= 128, "Composition context exceeded its word bound.");
    }
    Check(displayed == new string('b', 513), "Trimming composition context changed earlier text.");
}

Compose("tieengs 1234", "tieengs 1234", TypingMode.Physical);

Console.WriteLine($"Text input smoke passed: {assertions} assertions; Telex/VNI composition and JSON edits, no desktop input.");
