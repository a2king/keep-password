using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform.Mac;

/// <summary>
/// 使用 Carbon RegisterEventHotKey 注册全局快捷键（需在主线程创建）。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsGlobalHotkey : IDisposable
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint EventClassKeyboard = 0x6B657962; // 'keyb'
    private const uint EventHotKeyPressed = 6;
    private const uint EventTargetCurrentProcess = 2;

    private static readonly EventTypeSpec[] EventTypes =
    [
        new() { EventClass = EventClassKeyboard, EventKind = EventHotKeyPressed }
    ];

    private readonly EventHandlerProc _handler;
    private nint _handlerRef;
    private EventHotKeyRef _hotKeyRef;
    private bool _registered;
    private bool _disposed;

    public MacOsGlobalHotkey()
    {
        _handler = OnHotKey;
        var status = InstallEventHandler(
            GetApplicationEventTarget(),
            _handler,
            1,
            EventTypes,
            0,
            out _handlerRef);
        if (status != 0)
        {
            throw new InvalidOperationException("无法安装 macOS 全局快捷键事件处理：" + status);
        }
    }

    public event Action? Pressed;

    public bool Register(AutofillHotkey hotkey)
    {
        Unregister();
        if (hotkey.CarbonKeyCode is not { } keyCode)
        {
            return false;
        }

        var id = new EventHotKeyId { Signature = 0x4B505744, Id = 1 }; // 'KPWD'
        var status = RegisterEventHotKey(
            keyCode,
            hotkey.CarbonModifiers,
            id,
            GetApplicationEventTarget(),
            0,
            out _hotKeyRef);
        _registered = status == 0;
        return _registered;
    }

    public void Unregister()
    {
        if (_registered)
        {
            UnregisterEventHotKey(_hotKeyRef);
            _hotKeyRef = default;
            _registered = false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Unregister();
        if (_handlerRef != 0)
        {
            RemoveEventHandler(_handlerRef);
            _handlerRef = 0;
        }
    }

    private nint OnHotKey(nint nextHandler, nint theEvent, nint userData)
    {
        Pressed?.Invoke();
        return 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint EventHandlerProc(nint nextHandler, nint theEvent, nint userData);

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyId
    {
        public uint Signature;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyRef
    {
        public nint Handle;
    }

    [DllImport(Carbon)]
    private static extern nint GetApplicationEventTarget();

    [DllImport(Carbon)]
    private static extern int InstallEventHandler(
        nint target,
        EventHandlerProc handler,
        long numTypes,
        EventTypeSpec[] typeList,
        nint userData,
        out nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RemoveEventHandler(nint handlerRef);

    [DllImport(Carbon)]
    private static extern int RegisterEventHotKey(
        uint keyCode,
        uint modifiers,
        EventHotKeyId id,
        nint target,
        uint options,
        out EventHotKeyRef outRef);

    [DllImport(Carbon)]
    private static extern int UnregisterEventHotKey(EventHotKeyRef hotKey);
}
