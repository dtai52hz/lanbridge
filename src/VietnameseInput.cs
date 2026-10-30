using System.Globalization;
using System.Text;

namespace LanBridge;

public enum TypingMode
{
    VietnameseTelex,
    VietnameseVni,
    Physical,
}

/// <summary>
/// Small, deterministic Vietnamese composer. It owns only the word typed since the last
/// caret change; callers must Reset before forwarding navigation, shortcuts or mouse clicks.
/// Edits replace a suffix of that word and never inspect text in the remote application.
/// </summary>
public sealed class VietnameseComposer
{
    private const int MaximumWordLength = 128;
    // Each row is an untoned vowel followed by sắc, huyền, hỏi, ngã and nặng.
    private static readonly string[] Vowels =
    [
        "aáàảãạ", "ăắằẳẵặ", "âấầẩẫậ", "eéèẻẽẹ", "êếềểễệ", "iíìỉĩị",
        "oóòỏõọ", "ôốồổỗộ", "ơớờởỡợ", "uúùủũụ", "ưứừửữự", "yýỳỷỹỵ",
    ];

    private string _word = "";
    private int _tone;
    private bool _literal;
    private char _lastCommand;
    private TypingMode _mode = TypingMode.VietnameseTelex;

    public TypingMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            Reset();
        }
    }

    public string CurrentWord => _word;

    public Packet Process(char character)
    {
        if (_mode == TypingMode.Physical)
        {
            Reset();
            return Edit(0, character.ToString());
        }

        // Forgetting an overlong token is a composition boundary, not a remote deletion.
        if (_word.Length >= MaximumWordLength) Reset();
        var before = _word;
        var key = char.ToLowerInvariant(character);
        bool transformed = !_literal && (_mode == TypingMode.VietnameseTelex
            ? ApplyTelex(key, character)
            : ApplyVni(key, character));
        if (!transformed)
        {
            if (!IsWordCharacter(character))
            {
                Reset();
                return Edit(0, character.ToString());
            }
            _word = (_word + character).Normalize(NormalizationForm.FormC);
            int enteredTone = ToneOf(character);
            if (enteredTone == 0 && CharUnicodeInfo.GetUnicodeCategory(character) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
                enteredTone = ToneOf(_word[^1]);
            if (enteredTone != 0) _tone = enteredTone;
            _lastCommand = '\0';
        }
        ReflowTone();
        return Difference(before, _word);
    }

    public Packet Backspace()
    {
        if (_word.Length == 0) return Edit(1, "");
        var before = _word;
        var starts = StringInfo.ParseCombiningCharacters(_word);
        _word = _word[..starts[^1]];
        _lastCommand = '\0';
        _literal = false;
        ReflowTone();
        return Difference(before, _word);
    }

    public void Reset()
    {
        _word = "";
        _tone = 0;
        _literal = false;
        _lastCommand = '\0';
    }

    private bool ApplyTelex(char key, char original)
    {
        int tone = "sfrxj".IndexOf(key) + 1;
        if (tone > 0) return ApplyTone(tone, key, original);
        if (key == 'z' && _tone != 0) return ClearTone(key);
        return key switch
        {
            'a' => ChangeShape('a', 'â', key, original),
            'e' => ChangeShape('e', 'ê', key, original),
            'o' => ChangeShape('o', 'ô', key, original),
            'd' => ChangeD(key, original),
            'w' => ChangeHornOrBreve(key, original),
            _ => false,
        };
    }

    private bool ApplyVni(char key, char original)
    {
        if (key is >= '1' and <= '5') return ApplyTone(key - '0', key, original);
        if (key == '0' && _tone != 0) return ClearTone(key);
        if (key == '9') return ChangeD(key, original);
        if (key == '7') return ChangeHornOrBreve(key, original, onlyHorn: true);
        if (key == '8') return ChangeShape('a', 'ă', key, original);
        if (key != '6') return false;
        int index = LastIndexOfBase('a', 'â', 'e', 'ê', 'o', 'ô');
        if (index < 0) return false;
        char target = BaseOf(_word[index]) switch { 'a' or 'â' => 'â', 'e' or 'ê' => 'ê', _ => 'ô' };
        char source = target switch { 'â' => 'a', 'ê' => 'e', _ => 'o' };
        return ChangeShapeAt(index, source, target, key, original);
    }

    private bool ApplyTone(int tone, char key, char original)
    {
        if (ToneTarget(_word) < 0) return false;
        if (_tone == tone && _lastCommand == key)
        {
            _tone = 0;
            StripTones();
            _word += original;
            _literal = true;
        }
        else _tone = tone;
        _lastCommand = key;
        return true;
    }

    private bool ClearTone(char key)
    {
        _tone = 0;
        StripTones();
        _lastCommand = key;
        return true;
    }

    private bool ChangeD(char key, char original)
    {
        // A word-initial d can also be changed after its vowel has already been entered.
        int index = -1;
        for (int i = _word.Length - 1; i >= 0; i--)
            if (char.ToLowerInvariant(_word[i]) is 'd' or 'đ') { index = i; break; }
        if (index < 0) return false;
        return ChangeShapeAt(index, 'd', 'đ', key, original);
    }

    private bool ChangeShape(char source, char target, char key, char original)
    {
        int index = LastIndexOfBase(source, target);
        return index >= 0 && ChangeShapeAt(index, source, target, key, original);
    }

    private bool ChangeShapeAt(int index, char source, char target, char key, char original)
    {
        char current = BaseOf(_word[index]);
        if (current == target)
        {
            if (_lastCommand != key) return false;
            SetBase(index, source);
            _word += original;
            _literal = true;
        }
        else if (current == source) SetBase(index, target);
        else return false;
        _lastCommand = key;
        return true;
    }

    private bool ChangeHornOrBreve(char key, char original, bool onlyHorn = false)
    {
        int index = onlyHorn ? LastIndexOfBase('o', 'ơ', 'u', 'ư') : LastIndexOfBase('a', 'ă', 'o', 'ơ', 'u', 'ư');
        if (index < 0) return false;
        char current = BaseOf(_word[index]);
        // uo + w/7 changes the vowel pair together, including late entry after a final consonant.
        if (current is 'o' or 'ơ' && index > 0 && BaseOf(_word[index - 1]) is 'u' or 'ư')
        {
            bool alreadyHorned = current == 'ơ' && BaseOf(_word[index - 1]) == 'ư';
            if (alreadyHorned && _lastCommand == key)
            {
                SetBase(index - 1, 'u');
                SetBase(index, 'o');
                _word += original;
                _literal = true;
            }
            else
            {
                SetBase(index - 1, 'ư');
                SetBase(index, 'ơ');
            }
            _lastCommand = key;
            return true;
        }
        char source = current switch { 'a' or 'ă' => 'a', 'o' or 'ơ' => 'o', _ => 'u' };
        char target = source switch { 'a' => 'ă', 'o' => 'ơ', _ => 'ư' };
        return ChangeShapeAt(index, source, target, key, original);
    }

    private int LastIndexOfBase(params char[] choices)
    {
        for (int i = _word.Length - 1; i >= 0; i--)
            if (Array.IndexOf(choices, BaseOf(_word[i])) >= 0) return i;
        return -1;
    }

    private void SetBase(int index, char replacement)
    {
        var chars = _word.ToCharArray();
        chars[index] = WithCase(replacement, chars[index]);
        _word = new string(chars);
    }

    private void StripTones()
    {
        var chars = _word.ToCharArray();
        for (int i = 0; i < chars.Length; i++) chars[i] = WithCase(BaseOf(chars[i]), chars[i]);
        _word = new string(chars);
    }

    private void ReflowTone()
    {
        StripTones();
        int target = ToneTarget(_word);
        if (target < 0) { _tone = 0; return; }
        if (_tone == 0) return;
        char current = _word[target];
        foreach (var row in Vowels)
        {
            if (row[0] != char.ToLowerInvariant(current)) continue;
            var chars = _word.ToCharArray();
            chars[target] = WithCase(row[_tone], current);
            _word = new string(chars);
            return;
        }
    }

    private static int ToneTarget(string word)
    {
        var vowels = new List<int>();
        for (int i = 0; i < word.Length; i++)
        {
            char vowel = BaseOf(word[i]);
            if (!IsVowel(vowel)) continue;
            bool anotherVowel = i + 1 < word.Length && IsVowel(BaseOf(word[i + 1]));
            if (i > 0 && anotherVowel &&
                ((vowel == 'u' && char.ToLowerInvariant(word[i - 1]) == 'q') ||
                 (vowel == 'i' && char.ToLowerInvariant(word[i - 1]) == 'g'))) continue;
            vowels.Add(i);
        }
        if (vowels.Count == 0) return -1;
        // A shaped vowel carries the tone; ươ places it on ơ rather than ư.
        for (int i = vowels.Count - 1; i >= 0; i--)
            if (BaseOf(word[vowels[i]]) is 'ă' or 'â' or 'ê' or 'ô' or 'ơ') return vowels[i];
        for (int i = vowels.Count - 1; i >= 0; i--)
            if (BaseOf(word[vowels[i]]) == 'ư') return vowels[i];
        if (vowels.Count >= 3) return vowels[vowels.Count - 2];
        if (vowels.Count == 2)
            return vowels[^1] < word.Length - 1 ? vowels[^1] : vowels[0];
        return vowels[0];
    }

    private static bool IsWordCharacter(char character) => char.IsLetter(character) ||
        CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;

    private static bool IsVowel(char character) => Vowels.Any(row => row[0] == character);

    private static char BaseOf(char character)
    {
        char lower = char.ToLowerInvariant(character);
        foreach (var row in Vowels) if (row.IndexOf(lower) >= 0) return row[0];
        return lower;
    }

    private static int ToneOf(char character)
    {
        char lower = char.ToLowerInvariant(character);
        foreach (var row in Vowels)
        {
            int tone = row.IndexOf(lower);
            if (tone >= 0) return tone;
        }
        return 0;
    }

    private static char WithCase(char replacement, char original) =>
        char.IsUpper(original) ? char.ToUpperInvariant(replacement) : replacement;

    private static Packet Difference(string before, string after)
    {
        int common = 0;
        while (common < before.Length && common < after.Length && before[common] == after[common]) common++;
        // Never split a supplementary character at the unchanged suffix boundary.
        if (common > 0 && common < before.Length && char.IsLowSurrogate(before[common])) common--;
        return Edit(before.Length - common, after[common..]);
    }

    private static Packet Edit(int backspaces, string text) => new() { Type = "text", Dx = backspaces, Text = text };
}
