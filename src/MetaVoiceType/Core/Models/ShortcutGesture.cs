using SharpHook.Data;

namespace MetaVoiceType.Core.Models;

public sealed record KeyboardStroke(KeyCode Key, bool IsKeyDown);

public sealed record ShortcutGesture(bool Control, bool Shift, bool Alt, bool Windows, KeyCode Key)
{
    public IReadOnlyList<KeyCode> Modifiers
    {
        get
        {
            var keys = new List<KeyCode>(4);
            if (Control) keys.Add(KeyCode.VcLeftControl);
            if (Shift) keys.Add(KeyCode.VcLeftShift);
            if (Alt) keys.Add(KeyCode.VcLeftAlt);
            if (Windows) keys.Add(KeyCode.VcLeftMeta);
            return keys;
        }
    }

    public override string ToString() => string.Join('+', Modifiers.Select(ShortcutGestureParser.Display).Append(ShortcutGestureParser.Display(Key)));

    public IReadOnlyList<KeyboardStroke> PlaybackSequence()
    {
        var strokes = new List<KeyboardStroke>(Modifiers.Count * 2 + 2);
        strokes.AddRange(Modifiers.Select(x => new KeyboardStroke(x, true)));
        strokes.Add(new(Key, true));
        strokes.Add(new(Key, false));
        strokes.AddRange(Modifiers.Reverse().Select(x => new KeyboardStroke(x, false)));
        return strokes;
    }
}

public static class ShortcutGestureParser
{
    private static readonly Dictionary<string, KeyCode> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = KeyCode.VcSpace,
        ["Enter"] = KeyCode.VcEnter,
        ["Tab"] = KeyCode.VcTab,
        ["Escape"] = KeyCode.VcEscape,
        ["Esc"] = KeyCode.VcEscape,
        ["Up"] = KeyCode.VcUp,
        ["Down"] = KeyCode.VcDown,
        ["Left"] = KeyCode.VcLeft,
        ["Right"] = KeyCode.VcRight,
        ["Home"] = KeyCode.VcHome,
        ["End"] = KeyCode.VcEnd,
        ["PageUp"] = KeyCode.VcPageUp,
        ["PageDown"] = KeyCode.VcPageDown,
        ["Insert"] = KeyCode.VcInsert,
        ["Delete"] = KeyCode.VcDelete,
        ["Backspace"] = KeyCode.VcBackspace,
        ["Scroll"] = KeyCode.VcScrollLock,
        ["ScrollLock"] = KeyCode.VcScrollLock,
        ["Pause"] = KeyCode.VcPause,
        ["PrintScreen"] = KeyCode.VcPrintScreen
    };

    public static ShortcutGesture Parse(string value) => ParseCore(value, requireModifier: true);

    public static ShortcutGesture ParseAction(string value) => ParseCore(value, requireModifier: false);

    private static ShortcutGesture ParseCore(string value, bool requireModifier)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new FormatException("Press a key or shortcut.");
        bool control = false, shift = false, alt = false, windows = false;
        KeyCode? key = null;
        foreach (string raw in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL": case "CONTROL": control = true; continue;
                case "SHIFT": shift = true; continue;
                case "ALT": alt = true; continue;
                case "WIN": case "WINDOWS": case "META": windows = true; continue;
            }
            if (key is not null) throw new FormatException("A shortcut can contain only one non-modifier key.");
            key = ParseKey(raw);
        }
        if (key is null) throw new FormatException("Modifier-only shortcuts are not valid.");
        if (requireModifier && !control && !shift && !alt && !windows) throw new FormatException("Use at least one modifier for a global shortcut.");
        return new(control, shift, alt, windows, key.Value);
    }

    private static KeyCode ParseKey(string value)
    {
        if (Aliases.TryGetValue(value, out KeyCode alias)) return alias;
        string enumName = "Vc" + value.ToUpperInvariant();
        if (Enum.TryParse(enumName, true, out KeyCode key) && !IsModifier(key)) return key;
        throw new FormatException($"'{value}' is not a supported shortcut key.");
    }

    public static bool IsModifier(KeyCode key) => key is KeyCode.VcLeftControl or KeyCode.VcRightControl or KeyCode.VcLeftShift or KeyCode.VcRightShift
        or KeyCode.VcLeftAlt or KeyCode.VcRightAlt or KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    public static string Display(KeyCode key)
    {
        if (key == KeyCode.VcLeftControl) return "Ctrl";
        if (key == KeyCode.VcLeftShift) return "Shift";
        if (key == KeyCode.VcLeftAlt) return "Alt";
        if (key == KeyCode.VcLeftMeta) return "Win";
        string value = key.ToString();
        return value.StartsWith("Vc", StringComparison.Ordinal) ? value[2..] : value;
    }
}
