using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepPassword.App.Platform.Windows;

/// <summary>
/// 用仅消息窗口接收 WM_HOTKEY。必须在 UI 线程创建和使用，消息由 Avalonia 的消息循环分发。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsGlobalHotkey : IDisposable
{
    private const int HotkeyId = 0x4B50;
    private const uint WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private static readonly nint HwndMessage = -3;

    private readonly WndProc _proc;
    private readonly string _className = "KeepPassword.Hotkey." + Environment.ProcessId;
    private nint _hwnd;
    private bool _registered;

    public WindowsGlobalHotkey()
    {
        _proc = OnMessage;
        var windowClass = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className
        };
        if (RegisterClassEx(ref windowClass) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        _hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, HwndMessage, 0, windowClass.hInstance, 0);
        if (_hwnd == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public event Action? Pressed;

    /// <summary>注册失败（通常是被其它程序占用）时返回 false，并保持未注册状态。</summary>
    public bool Register(uint modifiers, uint virtualKey)
    {
        Unregister();
        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers | ModNoRepeat, virtualKey);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
        }
    }

    public void Dispose()
    {
        Unregister();
        if (_hwnd != 0)
        {
            DestroyWindow(_hwnd);
            _hwnd = 0;
            UnregisterClass(_className, GetModuleHandle(null));
        }
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmHotkey && wParam == HotkeyId)
        {
            Pressed?.Invoke();
            return 0;
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProc(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
