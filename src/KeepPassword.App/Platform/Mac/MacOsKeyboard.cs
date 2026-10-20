using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// Accessibility 写入失败时的键盘模拟降级（CGEvent 逐字输入）。
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacOsKeyboard
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const ushort KeyTab = 48;

    public static bool TryType(string username, string password, bool includeUsername)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return false;
        }

        try
        {
            if (includeUsername && !string.IsNullOrEmpty(username))
            {
                if (!TypeText(username) || !TapKey(KeyTab))
                {
                    return false;
                }
            }

            return TypeText(password ?? "");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TypeText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (!TypeUnicode(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TypeUnicode(int codePoint)
    {
        var chars = char.ConvertFromUtf32(codePoint);
        var source = CGEventSourceCreate(0);
        if (source == 0)
        {
            return false;
        }

        try
        {
            var down = CGEventCreateKeyboardEvent(source, 0, true);
            var up = CGEventCreateKeyboardEvent(source, 0, false);
            if (down == 0 || up == 0)
            {
                if (down != 0)
                {
                    CFRelease(down);
                }

                if (up != 0)
                {
                    CFRelease(up);
                }

                return false;
            }

            try
            {
                CGEventKeyboardSetUnicodeString(down, (ulong)chars.Length, chars);
                CGEventKeyboardSetUnicodeString(up, (ulong)chars.Length, chars);
                CGEventPost(0, down);
                CGEventPost(0, up);
                return true;
            }
            finally
            {
                CFRelease(down);
                CFRelease(up);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    private static bool TapKey(ushort keyCode)
    {
        var source = CGEventSourceCreate(0);
        if (source == 0)
        {
            return false;
        }

        try
        {
            var down = CGEventCreateKeyboardEvent(source, keyCode, true);
            var up = CGEventCreateKeyboardEvent(source, keyCode, false);
            if (down == 0 || up == 0)
            {
                if (down != 0)
                {
                    CFRelease(down);
                }

                if (up != 0)
                {
                    CFRelease(up);
                }

                return false;
            }

            try
            {
                CGEventPost(0, down);
                CGEventPost(0, up);
                return true;
            }
            finally
            {
                CFRelease(down);
                CFRelease(up);
            }
        }
        finally
        {
            CFRelease(source);
        }
    }

    [DllImport(CoreGraphics)]
    private static extern nint CGEventSourceCreate(int stateId);

    [DllImport(CoreGraphics)]
    private static extern nint CGEventCreateKeyboardEvent(nint source, ushort virtualKey, [MarshalAs(UnmanagedType.U1)] bool keyDown);

    [DllImport(CoreGraphics)]
    private static extern void CGEventKeyboardSetUnicodeString(nint eventRef, ulong length, [MarshalAs(UnmanagedType.LPWStr)] string text);

    [DllImport(CoreGraphics)]
    private static extern void CGEventPost(uint tap, nint eventRef);

    [DllImport(CoreFoundation)]
    private static extern void CFRelease(nint cf);
}
