namespace OpenMicBoard.Core;

public readonly record struct ParsedHotkey(
    uint Modifiers,
    uint VirtualKey,
    string CanonicalChord);

/// <summary>
/// Parses human-readable global-hotkey chords without calling Windows APIs.
/// Virtual-key values match the Win32 VK_* constants used by RegisterHotKey.
/// </summary>
public static class HotkeyChordParser
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    public static IReadOnlyList<string> SingleKeySuggestions { get; } =
    [
        "Numpad 0", "Numpad 1", "Numpad 2", "Numpad 3", "Numpad 4",
        "Numpad 5", "Numpad 6", "Numpad 7", "Numpad 8", "Numpad 9",
        "Numpad Add", "Numpad Subtract", "Numpad Multiply", "Numpad Divide",
        "Numpad Decimal", "F13", "F14", "F15", "F16", "F17", "F18",
        "F19", "F20", "F21", "F22", "F23", "F24", "Media Play/Pause",
        "Media Next", "Media Previous", "Media Stop", "Volume Mute",
        "Volume Up", "Volume Down", "Scroll Lock", "Pause"
    ];

    public static bool TryParse(
        string? chord,
        out ParsedHotkey parsed,
        out string? error)
    {
        parsed = default;
        error = null;
        if (string.IsNullOrWhiteSpace(chord))
        {
            error = "Enter a key such as Numpad 1 or a chord such as Ctrl+Alt+F1.";
            return false;
        }

        var modifiers = 0u;
        var virtualKey = 0u;
        string? canonicalKey = null;
        var tokens = chord.Split(
            '+',
            StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in tokens)
        {
            switch (Normalize(token))
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (virtualKey != 0 ||
                        !TryParseKey(token, out virtualKey, out canonicalKey))
                    {
                        error = $"'{token}' is not a supported key.";
                        return false;
                    }

                    break;
            }
        }

        if (virtualKey == 0 || canonicalKey is null)
        {
            error = "The shortcut needs a non-modifier key.";
            return false;
        }

        var canonicalParts = new List<string>(5);
        if ((modifiers & ModControl) != 0) canonicalParts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) canonicalParts.Add("Alt");
        if ((modifiers & ModShift) != 0) canonicalParts.Add("Shift");
        if ((modifiers & ModWin) != 0) canonicalParts.Add("Win");
        canonicalParts.Add(canonicalKey);
        parsed = new ParsedHotkey(
            modifiers,
            virtualKey,
            string.Join("+", canonicalParts));
        return true;
    }

    private static bool TryParseKey(
        string token,
        out uint virtualKey,
        out string? canonicalKey)
    {
        virtualKey = 0;
        canonicalKey = null;
        var normalized = Normalize(token);
        if (normalized.Length == 1 &&
            ((normalized[0] is >= 'A' and <= 'Z') ||
             (normalized[0] is >= '0' and <= '9')))
        {
            virtualKey = normalized[0];
            canonicalKey = normalized;
            return true;
        }

        if (normalized.StartsWith('F') &&
            int.TryParse(normalized[1..], out var function) &&
            function is >= 1 and <= 24)
        {
            virtualKey = (uint)(0x70 + function - 1);
            canonicalKey = $"F{function}";
            return true;
        }

        if (TryParseNumpad(normalized, out virtualKey, out canonicalKey))
        {
            return true;
        }

        var parsedKey = normalized switch
        {
            "BACKSPACE" => (0x08, "Backspace"),
            "TAB" => (0x09, "Tab"),
            "ENTER" or "RETURN" => (0x0D, "Enter"),
            "PAUSE" => (0x13, "Pause"),
            "CAPSLOCK" => (0x14, "Caps Lock"),
            "ESC" or "ESCAPE" => (0x1B, "Escape"),
            "SPACE" or "SPACEBAR" => (0x20, "Space"),
            "PAGEUP" or "PGUP" => (0x21, "Page Up"),
            "PAGEDOWN" or "PGDN" => (0x22, "Page Down"),
            "END" => (0x23, "End"),
            "HOME" => (0x24, "Home"),
            "LEFT" => (0x25, "Left"),
            "UP" => (0x26, "Up"),
            "RIGHT" => (0x27, "Right"),
            "DOWN" => (0x28, "Down"),
            "PRINTSCREEN" or "PRTSC" => (0x2C, "Print Screen"),
            "INSERT" or "INS" => (0x2D, "Insert"),
            "DELETE" or "DEL" => (0x2E, "Delete"),
            "APPS" or "CONTEXTMENU" => (0x5D, "Context Menu"),
            "SLEEP" => (0x5F, "Sleep"),
            "NUMLOCK" => (0x90, "Num Lock"),
            "SCROLLLOCK" => (0x91, "Scroll Lock"),
            "BROWSERBACK" => (0xA6, "Browser Back"),
            "BROWSERFORWARD" => (0xA7, "Browser Forward"),
            "BROWSERREFRESH" => (0xA8, "Browser Refresh"),
            "BROWSERSTOP" => (0xA9, "Browser Stop"),
            "BROWSERSEARCH" => (0xAA, "Browser Search"),
            "BROWSERFAVORITES" => (0xAB, "Browser Favorites"),
            "BROWSERHOME" => (0xAC, "Browser Home"),
            "VOLUMEMUTE" => (0xAD, "Volume Mute"),
            "VOLUMEDOWN" => (0xAE, "Volume Down"),
            "VOLUMEUP" => (0xAF, "Volume Up"),
            "MEDIANEXT" or "MEDIANEXTTRACK" => (0xB0, "Media Next"),
            "MEDIAPREVIOUS" or "MEDIAPREVIOUSTRACK" => (0xB1, "Media Previous"),
            "MEDIASTOP" => (0xB2, "Media Stop"),
            "MEDIAPLAYPAUSE" => (0xB3, "Media Play/Pause"),
            "LAUNCHMAIL" => (0xB4, "Launch Mail"),
            "LAUNCHMEDIA" => (0xB5, "Launch Media"),
            "COMMA" => (0xBC, "Comma"),
            "MINUS" => (0xBD, "Minus"),
            "PERIOD" or "DOT" => (0xBE, "Period"),
            "SLASH" => (0xBF, "Slash"),
            "BACKTICK" or "GRAVE" => (0xC0, "Backtick"),
            "SEMICOLON" => (0xBA, "Semicolon"),
            "EQUALS" => (0xBB, "Equals"),
            "LEFTBRACKET" => (0xDB, "Left Bracket"),
            "BACKSLASH" => (0xDC, "Backslash"),
            "RIGHTBRACKET" => (0xDD, "Right Bracket"),
            "QUOTE" or "APOSTROPHE" => (0xDE, "Quote"),
            _ => (0, null)
        };
        virtualKey = (uint)parsedKey.Item1;
        canonicalKey = parsedKey.Item2;
        return virtualKey != 0;
    }

    private static bool TryParseNumpad(
        string normalized,
        out uint virtualKey,
        out string? canonicalKey)
    {
        virtualKey = 0;
        canonicalKey = null;
        if ((normalized.StartsWith("NUMPAD") || normalized.StartsWith("NUM")) &&
            int.TryParse(
                normalized[(normalized.StartsWith("NUMPAD") ? 6 : 3)..],
                out var digit) &&
            digit is >= 0 and <= 9)
        {
            virtualKey = (uint)(0x60 + digit);
            canonicalKey = $"Numpad {digit}";
            return true;
        }

        var parsedKey = normalized switch
        {
            "NUMPADMULTIPLY" or "NUMMULTIPLY" => (0x6A, "Numpad Multiply"),
            "NUMPADADD" or "NUMADD" or "NUMPADPLUS" => (0x6B, "Numpad Add"),
            "NUMPADSEPARATOR" => (0x6C, "Numpad Separator"),
            "NUMPADSUBTRACT" or "NUMSUBTRACT" or "NUMPADMINUS" =>
                (0x6D, "Numpad Subtract"),
            "NUMPADDECIMAL" or "NUMDECIMAL" or "NUMPADDOT" =>
                (0x6E, "Numpad Decimal"),
            "NUMPADDIVIDE" or "NUMDIVIDE" => (0x6F, "Numpad Divide"),
            _ => (0, null)
        };
        virtualKey = (uint)parsedKey.Item1;
        canonicalKey = parsedKey.Item2;
        return virtualKey != 0;
    }

    private static string Normalize(string token) =>
        token.Trim()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("/", string.Empty, StringComparison.Ordinal)
            .ToUpperInvariant();
}
