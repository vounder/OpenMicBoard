using OpenMicBoard.Core;

namespace OpenMicBoard.Core.Tests;

public sealed class HotkeyParserTests
{
    [Theory]
    [InlineData("Numpad 0", 0x60u, "Numpad 0")]
    [InlineData("NumPad9", 0x69u, "Numpad 9")]
    [InlineData("numpad add", 0x6Bu, "Numpad Add")]
    [InlineData("Numpad-Subtract", 0x6Du, "Numpad Subtract")]
    [InlineData("Numpad Divide", 0x6Fu, "Numpad Divide")]
    [InlineData("Media Play/Pause", 0xB3u, "Media Play/Pause")]
    [InlineData("A", 0x41u, "A")]
    [InlineData("Space", 0x20u, "Space")]
    public void Single_keys_do_not_require_modifiers(
        string chord,
        uint expectedKey,
        string expectedCanonical)
    {
        Assert.True(HotkeyChordParser.TryParse(chord, out var parsed, out var error));
        Assert.Null(error);
        Assert.Equal(0u, parsed.Modifiers);
        Assert.Equal(expectedKey, parsed.VirtualKey);
        Assert.Equal(expectedCanonical, parsed.CanonicalChord);
    }

    [Fact]
    public void Modifier_chords_are_normalized_in_a_stable_order()
    {
        Assert.True(
            HotkeyChordParser.TryParse(
                "shift + control + numpad_3",
                out var parsed,
                out var error));

        Assert.Null(error);
        Assert.Equal(0x0002u | 0x0004u, parsed.Modifiers);
        Assert.Equal(0x63u, parsed.VirtualKey);
        Assert.Equal("Ctrl+Shift+Numpad 3", parsed.CanonicalChord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Numpad Unicorn")]
    public void Invalid_shortcuts_return_a_clear_error(string chord)
    {
        Assert.False(HotkeyChordParser.TryParse(chord, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Quick_list_contains_the_full_numpad_and_media_controls()
    {
        Assert.All(
            Enumerable.Range(0, 10),
            digit => Assert.Contains($"Numpad {digit}", HotkeyChordParser.SingleKeySuggestions));
        Assert.Contains("Numpad Add", HotkeyChordParser.SingleKeySuggestions);
        Assert.Contains("Numpad Divide", HotkeyChordParser.SingleKeySuggestions);
        Assert.Contains("Media Play/Pause", HotkeyChordParser.SingleKeySuggestions);
    }
}
