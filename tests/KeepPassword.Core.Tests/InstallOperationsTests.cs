using KeepPassword.Core.Install;

namespace KeepPassword.Core.Tests;

public class InstallOperationsTests
{
    [Fact]
    public void InstallFromDirectory_OverwritesAndRemovesStaleFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        var source = Path.Combine(root, "source");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(Path.Combine(source, "lib"));
        File.WriteAllText(Path.Combine(source, "KeepPassword.exe"), "new-exe");
        File.WriteAllText(Path.Combine(source, "使用说明.txt"), "readme");
        File.WriteAllText(Path.Combine(source, "lib", "a.dll"), "new-a");
        Directory.CreateDirectory(Path.Combine(target, "lib"));
        File.WriteAllText(Path.Combine(target, "KeepPassword.exe"), "old-exe");
        File.WriteAllText(Path.Combine(target, "old.dll"), "stale");
        File.WriteAllText(Path.Combine(target, "lib", "a.dll"), "old-a");
        File.WriteAllText(Path.Combine(target, "lib", "gone.dll"), "stale-lib");

        InstallOperations.InstallFromDirectory(source, target, new InstallOptions
        {
            CreateStartMenuShortcut = false,
            CreateDesktopShortcut = false,
            LaunchAfterInstall = false
        });

        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(target, "KeepPassword.exe")));
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(target, "lib", "a.dll")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(target, "使用说明.txt")));
        Assert.False(File.Exists(Path.Combine(target, "old.dll")));
        Assert.False(File.Exists(Path.Combine(target, "lib", "gone.dll")));
    }

    [Fact]
    public void Uninstall_CanKeepOrDeleteCache()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        var install = Path.Combine(root, "install");
        var cache = Path.Combine(root, "cache");
        Directory.CreateDirectory(install);
        Directory.CreateDirectory(cache);
        File.WriteAllText(Path.Combine(install, "KeepPassword.exe"), "x");
        File.WriteAllText(Path.Combine(cache, "vault.kpvault"), "vault");

        InstallOperations.Uninstall(install, deleteCache: false);
        Assert.False(Directory.Exists(install));
        Assert.True(File.Exists(Path.Combine(cache, "vault.kpvault")));
    }
}
