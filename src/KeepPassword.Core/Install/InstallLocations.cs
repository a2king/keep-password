using KeepPassword.Core.Paths;

namespace KeepPassword.Core.Install;

/// <summary>开机自启动入口所在位置：Windows 注册表项、macOS plist、Linux desktop 文件。</summary>
internal sealed record AutostartLocations(string RunKey, string RunValueName, string MacLaunchAgent, string LinuxDesktopFile)
{
    public static AutostartLocations Default()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(home, ".config");
        }

        return new AutostartLocations(
            @"Software\Microsoft\Windows\CurrentVersion\Run",
            "KeepPassword",
            Path.Combine(home, "Library", "LaunchAgents", "com.keeppassword.app.plist"),
            Path.Combine(config, "autostart", "keep-password.desktop"));
    }
}

/// <summary>
/// 安装器会读写的全部系统位置。生产环境用 <see cref="Default"/>；
/// 测试传入隔离的注册表子键和临时目录，验证"重复安装不再写注册表"时不会碰真实系统。
/// </summary>
internal sealed record InstallLocations(
    string UninstallKey,
    IReadOnlyList<string> NativeHostKeys,
    string NativeHostManifest,
    string StartMenuShortcut,
    string DesktopShortcut,
    string SettingsFile,
    AutostartLocations Autostart)
{
    public static InstallLocations Default() => new(
        InstallConstants.UninstallRegistryKey,
        InstallConstants.NativeHostRegistryKeys,
        NativeHostRegistration.ManifestPath(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), InstallConstants.ProductName + ".lnk"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), InstallConstants.ProductName + ".lnk"),
        CacheDirectory.SettingsFile(),
        AutostartLocations.Default());
}
