using KeepPassword.Core.Install;
using KeepPassword.Core.Tests.Support;

namespace KeepPassword.Core.Tests;

public class ShortcutAndDeleteTests
{
    [Fact]
    public void Shortcut_RoundTripsThroughComWithChinesePathsAndDetectsChanges()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunShortcutRoundTrip();
    }

    // ShellLink 只在 Windows 上可用；单独成方法并标注平台，Linux 上编译不再报 CA1416。
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunShortcutRoundTrip()
    {
        using var env = new IsolatedInstall();
        var app = Path.Combine(env.Root, "程序目录", "KeepPassword.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(app)!);
        File.WriteAllText(app, "x");
        var link = Path.Combine(env.Root, "快捷方式", "Keep Password.lnk");

        Assert.False(ShellLink.Matches(link, app, Path.GetDirectoryName(app)!));
        ShellLink.Write(link, app, Path.GetDirectoryName(app)!);
        Assert.True(File.Exists(link));
        Assert.True(ShellLink.Matches(link, app, Path.GetDirectoryName(app)!));

        // 目标换了位置，必须能识别出"不一致"，这样升级到新目录时会重建快捷方式。
        var moved = Path.Combine(env.Root, "另一个目录", "KeepPassword.exe");
        Assert.False(ShellLink.Matches(link, moved, Path.GetDirectoryName(moved)!));
    }

    [Fact]
    public void InstallAndUninstall_WorkWhenThePathContainsChineseCharacters()
    {
        if (!OperatingSystem.IsWindows()) return;
        RunChinesePathInstall();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void RunChinesePathInstall()
    {
        using var env = new IsolatedInstall();
        env.WritePayload();
        var target = Path.Combine(env.Root, "用户 张三", "Keep Password");

        var summary = InstallOperations.InstallFromDirectory(env.Source, target, env.Options(autostart: true));
        Assert.Equal(2, summary.ShortcutsWritten);
        Assert.True(ShellLink.Matches(env.Where.StartMenuShortcut, Path.Combine(target, "KeepPassword.exe"), target));
        Assert.True(AutostartRegistration.IsRegistered(env.Where.Autostart, AutostartPlatform.Windows));
    }

    [Fact]
    public void ScheduledDelete_RemovesDirectoriesWhosePathHasChineseCharacters()
    {
        if (!IsolatedInstall.Supported) return;
        using var env = new IsolatedInstall();
        var folder = Path.Combine(env.Root, "用户 张三", "安装目录");
        Directory.CreateDirectory(Path.Combine(folder, "lib"));
        File.WriteAllText(Path.Combine(folder, "lib", "a.dll"), "x");

        // 旧脚本用 ASCII 写入路径，中文会变成问号：目录删不掉，脚本还会一直挂在重试循环里。
        InstallOperations.ScheduleDeleteDirectory(folder);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (Directory.Exists(folder) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public void ScheduledDeleteFile_RemovesFilesWhosePathHasChineseCharacters()
    {
        if (!IsolatedInstall.Supported) return;
        using var env = new IsolatedInstall();
        var file = Path.Combine(env.Root, "更新缓存", "安装包.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "x");

        InstallOperations.ScheduleDeleteFile(file);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (File.Exists(file) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);
        }

        Assert.False(File.Exists(file));
    }
}
