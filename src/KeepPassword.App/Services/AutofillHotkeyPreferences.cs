using KeepPassword.Core.Autofill;
using KeepPassword.Core.Paths;

namespace KeepPassword.App.Services;

public static class AutofillHotkeyPreferences
{
    private static Func<AutofillHotkey, bool>? _register;
    private static Action? _unregister;

    public static AutofillHotkey Current { get; private set; } = AutofillHotkey.Default;

    /// <summary>当前快捷键是否已成功注册到系统；被其它程序占用时为 false。</summary>
    public static bool IsActive { get; private set; }

    public static bool IsSupported => _register is not null;

    public static void Initialize()
    {
        Current = AutofillHotkey.TryParse(CacheDirectory.GetAutofillHotkey(CacheDirectory.SettingsFile()), out var saved)
            ? saved
            : AutofillHotkey.Default;
    }

    public static void Attach(Func<AutofillHotkey, bool> register, Action unregister)
    {
        _register = register;
        _unregister = unregister;
        IsActive = register(Current);
    }

    /// <summary>录制新快捷键时暂停全局注册，否则当前组合会被系统拦截，设置页收不到按键。</summary>
    public static void Suspend() => _unregister?.Invoke();

    public static void Resume()
    {
        if (_register is not null)
        {
            IsActive = _register(Current);
        }
    }

    /// <summary>注册新快捷键；失败时恢复原快捷键并返回原因。</summary>
    public static string? TryApply(AutofillHotkey hotkey)
    {
        if (hotkey.Validate() is { } invalid)
        {
            return invalid;
        }

        if (_register is null)
        {
            return "当前系统不支持程序自动填充。";
        }

        if (!_register(hotkey))
        {
            IsActive = _register(Current);
            return $"{hotkey} 已被其它程序占用，请换一个组合。";
        }

        CacheDirectory.SetAutofillHotkey(CacheDirectory.SettingsFile(), hotkey.ToString());
        Current = hotkey;
        IsActive = true;
        return null;
    }
}
