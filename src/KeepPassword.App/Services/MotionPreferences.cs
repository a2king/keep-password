using System.Runtime.InteropServices;
using KeepPassword.Core.Paths;

namespace KeepPassword.App.Services;

public static class MotionPreferences
{
    public const string FullMode = "Full";
    public const string SystemMode = "System";
    public const string ReducedMode = "Reduced";

    public static string CurrentMode { get; private set; } = FullMode;

    public static bool IsEnabled => ResolveEnabled(CurrentMode,
        CurrentMode != SystemMode || SystemAllowsAnimation());

    public static void Initialize() => CurrentMode = CacheDirectory.GetMotionMode(CacheDirectory.SettingsFile());

    public static void SetMode(string mode)
    {
        var normalized = mode is SystemMode or ReducedMode ? mode : FullMode;
        CacheDirectory.SetMotionMode(CacheDirectory.SettingsFile(), normalized);
        CurrentMode = normalized;
    }

    public static bool ResolveEnabled(string mode, bool systemEnabled) => mode switch
    {
        ReducedMode => false,
        SystemMode => systemEnabled,
        _ => true
    };

    private static bool SystemAllowsAnimation() => !OperatingSystem.IsWindows()
        || !SystemParametersInfo(0x1042, 0, out var enabled, 0) || enabled;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
