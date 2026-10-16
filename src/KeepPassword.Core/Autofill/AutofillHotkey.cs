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
