using KeepPassword.Core.Install;
using Microsoft.Win32;

namespace KeepPassword.Core.Tests.Support;

/// <summary>
/// 把安装器要读写的注册表项、快捷方式、设置文件全部指到一次性位置（HKCU 下的随机子键 + 临时目录），
/// 测试结束后整体清掉，所以测试绝不会碰到真实的开机启动项或浏览器连接登记。
/// </summary>
internal sealed class IsolatedInstall : IDisposable
{
    private readonly string _hive = @"Software\KeepPasswordTests\" + Guid.NewGuid().ToString("n");

    public IsolatedInstall()
    {
        Root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);
        Where = new InstallLocations(
            _hive + @"\Uninstall",
            [_hive + @"\ChromeHost", _hive + @"\EdgeHost"],
            Path.Combine(Root, "data", "host.json"),
            Path.Combine(Root, "menu", "Keep Password.lnk"),
            Path.Combine(Root, "desk", "Keep Password.lnk"),
            Path.Combine(Root, "data", "settings.json"),
            new AutostartLocations(
                _hive + @"\Run",
                "KeepPassword",
                Path.Combine(Root, "LaunchAgents", "kp.plist"),
                Path.Combine(Root, "autostart", "kp.desktop")));
    }

    public string Root { get; }

    public InstallLocations Where { get; }

    public string Source => Path.Combine(Root, "source");

    public string Target => Path.Combine(Root, "target");

    /// <summary>生成一个最小的安装包内容：主程序、Native Host 和一个依赖库。</summary>
    public void WritePayload()
    {
        Directory.CreateDirectory(Source);
        File.WriteAllText(Path.Combine(Source, "KeepPassword.exe"), "app");
        File.WriteAllText(Path.Combine(Source, "KeepPassword.NativeHost.exe"), "host");
        File.WriteAllText(Path.Combine(Source, "a.dll"), "lib");
    }

    public InstallOptions Options(bool? autostart = null, bool shortcuts = true) => new()
    {
        CreateStartMenuShortcut = shortcuts,
        CreateDesktopShortcut = shortcuts,
        LaunchAfterInstall = false,
        LaunchAtStartup = autostart,
        Locations = Where,
        DiscoveredOrigins = []
    };

    public static bool Supported => OperatingSystem.IsWindows();

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_hive, throwOnMissingSubKey: false);
            // 父键为空时一并清掉，不在注册表里留下 KeepPasswordTests 空键。
            using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\KeepPasswordTests"))
            {
                if (parent is not null && parent.SubKeyCount == 0)
                {
                    parent.Close();
                    Registry.CurrentUser.DeleteSubKey(@"Software\KeepPasswordTests", throwOnMissingSubKey: false);
                }
            }
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
