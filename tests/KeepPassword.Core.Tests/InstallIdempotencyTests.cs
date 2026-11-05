using KeepPassword.Core.Install;
using KeepPassword.Core.Paths;
using KeepPassword.Core.Tests.Support;

namespace KeepPassword.Core.Tests;

public class InstallIdempotencyTests
{
    [Fact]
    public void ReinstallingTheSameVersion_WritesNothingToRegistryOrShortcuts()
    {
        if (!IsolatedInstall.Supported) return;
        using var env = new IsolatedInstall();
        env.WritePayload();

        var first = InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: false));
        Assert.True(first.RegistryWrites > 0);
        Assert.Equal(2, first.ShortcutsWritten);

        // 第二次安装同一版本：注册表一次都不应该写，快捷方式也不重建。
        // 这正是"每次重装都弹注册表修改"的修复点：安全软件拦的是写入动作。
        var second = InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: false));
        Assert.Equal(0, second.RegistryWrites);
        Assert.True(second.RegistryUnchanged > 0);
        Assert.Equal(0, second.ShortcutsWritten);
        Assert.Equal(2, second.ShortcutsUnchanged);
        Assert.False(second.AutostartChanged);
    }

    [Fact]
    public void FreshInstall_DoesNotEnableAutostartUnlessAsked()
    {
        if (!IsolatedInstall.Supported) return;
        using var env = new IsolatedInstall();
        env.WritePayload();

        // 不传 LaunchAtStartup：完全不碰开机启动。
        var plain = InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: null));
        Assert.False(plain.AutostartChanged);
        Assert.False(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
        Assert.Null(CacheDirectory.TryGetLaunchAtStartup(env.Where.SettingsFile));

        // 向导里勾选后才会登记，并且把选择保存下来。
        var on = InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: true));
        Assert.True(on.AutostartChanged);
        Assert.True(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
        Assert.True(CacheDirectory.GetLaunchAtStartup(env.Where.SettingsFile));

        // 再装一次且选择不变：不会再写。取消勾选则撤销登记。
        Assert.False(InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: true)).AutostartChanged);
        var off = InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: false));
        Assert.True(off.AutostartChanged);
        Assert.False(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
    }

    [Fact]
    public void Uninstall_RemovesAutostartRegistryAndShortcuts()
    {
        if (!IsolatedInstall.Supported) return;
        using var env = new IsolatedInstall();
        env.WritePayload();
        InstallOperations.InstallFromDirectory(env.Source, env.Target, env.Options(autostart: true));
        Assert.True(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
        Assert.True(File.Exists(env.Where.StartMenuShortcut));
        Assert.True(File.Exists(env.Where.NativeHostManifest));

        // 以前卸载后开机启动项会残留，每次开机都去找一个已经不存在的程序。
        InstallOperations.Uninstall(env.Target, deleteCache: false, unregisterFromSystem: true, env.Where);

        Assert.False(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
        Assert.False(File.Exists(env.Where.StartMenuShortcut));
        Assert.False(File.Exists(env.Where.DesktopShortcut));
        Assert.False(File.Exists(env.Where.NativeHostManifest));
    }
}
