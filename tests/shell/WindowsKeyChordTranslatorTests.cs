using System.Runtime.Versioning;
using Thaddeus.Shell.Platform;
using Thaddeus.Shell.Platform.Windows;
using Xunit;

namespace Thaddeus.Shell.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsKeyChordTranslatorTests
{
    [Fact]
    public void Single_letter_translates_to_uppercase_vk()
    {
        var (mods, vk) = WindowsKeyChordTranslator.Translate(new KeyChord("a", KeyModifiers.None));
        Assert.Equal((uint)'A', vk);
        Assert.Equal(0x4000u, mods); // MOD_NOREPEAT only
    }

    [Theory]
    [InlineData("Space", 0x20u)]
    [InlineData("Enter", 0x0Du)]
    [InlineData("Escape", 0x1Bu)]
    [InlineData("Esc", 0x1Bu)]
    [InlineData("Tab", 0x09u)]
    [InlineData("Backspace", 0x08u)]
    [InlineData("Delete", 0x2Eu)]
    [InlineData("Home", 0x24u)]
    [InlineData("End", 0x23u)]
    [InlineData("PageUp", 0x21u)]
    [InlineData("PageDown", 0x22u)]
    [InlineData("Left", 0x25u)]
    [InlineData("Up", 0x26u)]
    [InlineData("Right", 0x27u)]
    [InlineData("Down", 0x28u)]
    public void Named_keys_resolve_to_correct_vk(string name, uint expected)
    {
        Assert.Equal(expected, WindowsKeyChordTranslator.ResolveVirtualKey(name));
    }

    [Theory]
    [InlineData("F1", 0x70u)]
    [InlineData("F8", 0x77u)]
    [InlineData("F12", 0x7Bu)]
    [InlineData("F24", 0x87u)]
    public void Function_keys_resolve_in_range(string name, uint expected)
    {
        Assert.Equal(expected, WindowsKeyChordTranslator.ResolveVirtualKey(name));
    }

    [Fact]
    public void Modifiers_combine_to_bitset_with_norepeat()
    {
        var (mods, _) = WindowsKeyChordTranslator.Translate(
            new KeyChord("Space", KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Super));
        // MOD_NOREPEAT(0x4000) | MOD_WIN(8) | MOD_ALT(1) | MOD_SHIFT(4) | MOD_CONTROL(2) = 0x400F
        Assert.Equal(0x400Fu, mods);
    }

    [Fact]
    public void Digits_translate_to_ascii_codepoint()
    {
        Assert.Equal((uint)'5', WindowsKeyChordTranslator.ResolveVirtualKey("5"));
    }

    [Fact]
    public void Unknown_key_throws()
    {
        Assert.Throws<ArgumentException>(() => WindowsKeyChordTranslator.ResolveVirtualKey("CapsLockJr"));
    }

    [Fact]
    public void Function_key_out_of_range_throws()
    {
        Assert.Throws<ArgumentException>(() => WindowsKeyChordTranslator.ResolveVirtualKey("F25"));
    }

    [Fact]
    public void Empty_chord_key_throws()
    {
        Assert.Throws<ArgumentException>(
            () => WindowsKeyChordTranslator.Translate(new KeyChord(" ", KeyModifiers.Control)));
    }
}
