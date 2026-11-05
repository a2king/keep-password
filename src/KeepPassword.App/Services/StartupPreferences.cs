using KeepPassword.Core.Install;
using KeepPassword.Core.Paths;

namespace KeepPassword.App.Services;

/// <summary>
/// 开机启动与最小化启动：读写 settings，并同步到各平台自启动位置。
/// 真正读写注册表 / plist / desktop 文件的逻辑在 Core 的 AutostartRegistration（先读后写，没变化不写）。
/// </summary>
public static class StartupPreferences
{
    public const string MinimizedArg = AutostartRegistration.MinimizedArg;

    public static bool LaunchAtStartup { get; private set; }

    public static bool StartMinimized { get; private set; } = true;

    public static bool RequestedMinimizedStartup { get; private set; }

    public static void Initialize()
    {
        var settings = CacheDirectory.SettingsFile();
        // 用户明确选过就按选择；从没选过时沿用系统里已有的登记（旧版靠默认值登记的用户保持开启），全新安装默认关闭。
        var stored = CacheDirectory.TryGetLaunchAtStartup(settings);
        LaunchAtStartup = AutostartRegistration.ResolveEnabled(stored);
        StartMinimized = CacheDirectory.GetStartMinimized(settings);
        RequestedMinimizedStartup = Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, MinimizedArg, StringComparison.OrdinalIgnoreCase));

        // 已开启时每次启动都核对一次；一致就不写任何东西（这也是修复"每次启动都弹注册表修改"的关键）。
        // 没开启时什么都不做，不再为了"确认已关闭"去碰注册表。
        if (LaunchAtStartup)
        {
            TryApplyOs();
        }
    }

    public static string? SetLaunchAtStartup(bool enabled)
    {
        CacheDirectory.SetLaunchAtStartup(CacheDirectory.SettingsFile(), enabled);
        LaunchAtStartup = enabled;
        return TryApplyOs();
    }

    public static string? SetStartMinimized(bool enabled)
    {
        CacheDirectory.SetStartMinimized(CacheDirectory.SettingsFile(), enabled);
        StartMinimized = enabled;
        return LaunchAtStartup ? TryApplyOs() : null;
    }

    private static string? TryApplyOs()
    {
        try
        {
            ApplyOsRegistration(LaunchAtStartup, StartMinimized);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                       or System.Security.SecurityException)
        {
            return "无法更新开机启动项：" + ex.Message;
        }
    }

    internal static void ApplyOsRegistration(bool launchAtStartup, bool startMinimized)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            throw new InvalidOperationException("找不到当前程序路径。");
        }

        AutostartRegistration.Apply(exe, launchAtStartup, startMinimized);
    }
}
