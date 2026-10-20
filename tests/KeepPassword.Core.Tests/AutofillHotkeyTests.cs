using KeepPassword.Core.Autofill;

namespace KeepPassword.Core.Tests;

public class AutofillHotkeyTests
{
    [Fact]
    public void Default_IsCtrlAltK()
    {
        Assert.Equal("Ctrl+Alt+K", AutofillHotkey.Default.ToString());
        Assert.Null(AutofillHotkey.Default.Validate());
        Assert.Equal(0x4Bu, AutofillHotkey.Default.VirtualKey);
        Assert.Equal(0x3u, AutofillHotkey.Default.Win32Modifiers);
        Assert.Equal((ushort)40, AutofillHotkey.Default.CarbonKeyCode);
        Assert.Equal(4096u + 2048u, AutofillHotkey.Default.CarbonModifiers);
    }

    [Theory]
    [InlineData("ctrl+alt+k", "Ctrl+Alt+K")]
    [InlineData(" Control + Shift + p ", "Ctrl+Shift+P")]
    [InlineData("Alt+Shift+7", "Alt+Shift+7")]
    [InlineData("shift+f9", "Shift+F9")]
    [InlineData("Win+Ctrl+space", "Ctrl+Win+Space")]
    public void TryParse_AcceptsAndNormalizes(string text, string expected)
    {
        Assert.True(AutofillHotkey.TryParse(text, out var hotkey));
        Assert.Equal(expected, hotkey.ToString());
        Assert.True(AutofillHotkey.TryParse(hotkey.ToString(), out var roundTrip));
        Assert.Equal(hotkey, roundTrip);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("K")]
    [InlineData("Shift+K")]
    [InlineData("Alt+K")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+K+L")]
    [InlineData("Ctrl+Enter")]
    [InlineData("Ctrl+F13")]
    [InlineData("F5")]
    public void TryParse_RejectsUnsafeOrUnknown_AndFallsBackToDefault(string? text)
    {
        Assert.False(AutofillHotkey.TryParse(text, out var hotkey));
        Assert.Equal(AutofillHotkey.Default, hotkey);
    }

    [Fact]
    public void Validate_AllowsAltWithFunctionKey()
    {
        Assert.Null(new AutofillHotkey(HotkeyModifiers.Alt, "F8").Validate());
        Assert.NotNull(new AutofillHotkey(HotkeyModifiers.Alt, "K").Validate());
        Assert.NotNull(new AutofillHotkey(HotkeyModifiers.Ctrl, "k").Validate());
    }

    [Theory]
    [InlineData("Ctrl+Alt+A", 0x41u, 0x3u)]
    [InlineData("Ctrl+Shift+0", 0x30u, 0x6u)]
    [InlineData("Shift+F1", 0x70u, 0x4u)]
    [InlineData("Ctrl+F12", 0x7Bu, 0x2u)]
    [InlineData("Ctrl+Win+Space", 0x20u, 0xAu)]
    public void Win32Codes_MatchRegisterHotKey(string text, uint virtualKey, uint modifiers)
    {
        Assert.True(AutofillHotkey.TryParse(text, out var hotkey));
        Assert.Equal(virtualKey, hotkey.VirtualKey);
        Assert.Equal(modifiers, hotkey.Win32Modifiers);
    }
}
