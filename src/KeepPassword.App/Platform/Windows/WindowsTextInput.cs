using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepPassword.App.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsTextInput
{
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const uint Unicode = 0x0004;
    private const ushort VkControl = 0x11;
    private const ushort VkA = 0x41;
    private const ushort VkDelete = 0x2E;

    public static bool TypeReplacing(string text)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        Thread.Sleep(40);
        var inputs = new List<INPUT>();
        KeyCombo(inputs, VkControl, VkA);
        Key(inputs, VkDelete, keyUp: false);
        Key(inputs, VkDelete, keyUp: true);
        foreach (var character in text)
        {
            UnicodeChar(inputs, character);
        }

        if (inputs.Count == 0)
        {
            return false;
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == inputs.Count;
    }

    public static bool TypeTab()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var inputs = new List<INPUT>();
        Key(inputs, 0x09, keyUp: false);
        Key(inputs, 0x09, keyUp: true);
        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == inputs.Count;
    }

    private static void KeyCombo(List<INPUT> inputs, ushort modifier, ushort key)
    {
        Key(inputs, modifier, keyUp: false);
        Key(inputs, key, keyUp: false);
        Key(inputs, key, keyUp: true);
        Key(inputs, modifier, keyUp: true);
    }

    private static void Key(List<INPUT> inputs, ushort virtualKey, bool keyUp)
    {
        inputs.Add(new INPUT
        {
            type = InputKeyboard,
            union = new INPUTUNION
            {
                ki = new KEYBDINPUT
                {
                    wVk = virtualKey,
                    dwFlags = keyUp ? KeyUp : 0
                }
            }
        });
    }

    private static void UnicodeChar(List<INPUT> inputs, char character)
    {
        inputs.Add(Keyboard((ushort)character, Unicode));
        inputs.Add(Keyboard((ushort)character, Unicode | KeyUp));
    }

    private static INPUT Keyboard(ushort scan, uint flags) => new()
    {
        type = InputKeyboard,
        union = new INPUTUNION
        {
            ki = new KEYBDINPUT
            {
                wScan = scan,
                dwFlags = flags
            }
        }
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public INPUTUNION union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }
}
