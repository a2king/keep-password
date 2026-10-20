namespace KeepPassword.Core.Autofill;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Shift = 4,
    Win = 8
}

/// <summary>
/// 程序自动填充的全局快捷键，例如 Ctrl+Alt+K。按键仅支持 A–Z、0–9、F1–F12 和空格。
/// </summary>
public sealed record AutofillHotkey(HotkeyModifiers Modifiers, string Key)
{
    public static AutofillHotkey Default { get; } = new(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "K");

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out AutofillHotkey hotkey)
    {
        hotkey = Default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var modifier = raw.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "win" or "windows" or "meta" => HotkeyModifiers.Win,
                _ => HotkeyModifiers.None
            };
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key is not null || NormalizeKey(raw) is not { } normalized)
            {
                return false;
            }

            key = normalized;
        }

        if (key is null)
        {
            return false;
        }

        var candidate = new AutofillHotkey(modifiers, key);
        if (candidate.Validate() is not null)
        {
            return false;
        }

        hotkey = candidate;
        return true;
    }

    /// <summary>返回不可用的原因；可用时返回 null。</summary>
    public string? Validate()
    {
        if (NormalizeKey(Key) != Key)
        {
            return "只支持字母、数字、F1–F12 或空格作为按键。";
        }

        var hasCtrlOrAlt = (Modifiers & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) != 0;
        var isFunctionKey = Key.StartsWith('F') && Key.Length > 1;
        if (!hasCtrlOrAlt && !(isFunctionKey && Modifiers != HotkeyModifiers.None))
        {
            return "需要包含 Ctrl 或 Alt，避免和普通输入冲突。";
        }

        if (Modifiers == HotkeyModifiers.Alt && !isFunctionKey)
        {
            return "单独的 Alt+按键常被程序菜单占用，请再加 Ctrl 或 Shift。";
        }

        return null;
    }

    public uint VirtualKey => Key switch
    {
        "Space" => 0x20,
        _ when Key.Length == 1 => Key[0],
        _ => (uint)(0x70 + int.Parse(Key.AsSpan(1)) - 1)
    };

    public uint Win32Modifiers =>
        (Modifiers.HasFlag(HotkeyModifiers.Alt) ? 0x1u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Ctrl) ? 0x2u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Shift) ? 0x4u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Win) ? 0x8u : 0);

    /// <summary>Carbon RegisterEventHotKey 修饰键：cmdKey / shiftKey / optionKey / controlKey。</summary>
    public uint CarbonModifiers =>
        (Modifiers.HasFlag(HotkeyModifiers.Win) ? 256u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Shift) ? 512u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Alt) ? 2048u : 0)
        | (Modifiers.HasFlag(HotkeyModifiers.Ctrl) ? 4096u : 0);

    /// <summary>macOS 虚拟键码（ANSI 布局）。无法映射时返回 null。</summary>
    public ushort? CarbonKeyCode => Key switch
    {
        "A" => 0, "S" => 1, "D" => 2, "F" => 3, "H" => 4, "G" => 5, "Z" => 6, "X" => 7, "C" => 8, "V" => 9,
        "B" => 11, "Q" => 12, "W" => 13, "E" => 14, "R" => 15, "Y" => 16, "T" => 17,
        "1" => 18, "2" => 19, "3" => 20, "4" => 21, "6" => 22, "5" => 23, "9" => 25, "7" => 26, "8" => 28, "0" => 29,
        "O" => 31, "U" => 32, "I" => 34, "P" => 35, "L" => 37, "J" => 38, "K" => 40,
        "N" => 45, "M" => 46, "Space" => 49,
        "F1" => 122, "F2" => 120, "F3" => 99, "F4" => 118, "F5" => 96, "F6" => 97,
        "F7" => 98, "F8" => 100, "F9" => 101, "F10" => 109, "F11" => 103, "F12" => 111,
        _ => null
    };

    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var value = key.Trim().ToUpperInvariant();
        if (value is "SPACE")
        {
            return "Space";
        }

        if (value.Length == 1 && (char.IsAsciiLetterUpper(value[0]) || char.IsAsciiDigit(value[0])))
        {
            return value;
        }

        if (value.Length is 2 or 3 && value[0] == 'F' && int.TryParse(value.AsSpan(1), out var number) && number is >= 1 and <= 12)
        {
            return "F" + number;
        }

        return null;
    }
}
