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
            LaunchAfterInstall = false,
            RegisterWithSystem = false
        });

        Assert.Equal("new-exe", File.ReadAllText(Path.Combine(target, "KeepPassword.exe")));
        Assert.Equal("new-a", File.ReadAllText(Path.Combine(target, "lib", "a.dll")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(target, "使用说明.txt")));
        Assert.False(File.Exists(Path.Combine(target, "old.dll")));
        Assert.False(File.Exists(Path.Combine(target, "lib", "gone.dll")));
    }

    [Fact]
    public void Install_ReportsSingleMonotonicProgressWithFileLog()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        var zipPath = Path.Combine(root, "payload.zip");
        Directory.CreateDirectory(root);
        using (var zip = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "KeepPassword.exe", "lib/a.dll", "lib/b.dll" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(new string('x', 2048));
            }
        }

        var reports = new List<InstallProgress>();
        var progress = new InlineProgress(reports.Add);
        var extract = Path.Combine(root, "extract");
        using (var stream = File.OpenRead(zipPath))
        {
            InstallOperations.ExtractZip(stream, extract, progress);
        }

        InstallOperations.InstallFromDirectory(extract, Path.Combine(root, "target"), new InstallOptions { RegisterWithSystem = false }, progress);

        var percents = reports.Select(item => item.Percent).ToList();
        Assert.Equal(percents.OrderBy(value => value), percents);
        Assert.Equal(100, percents[^1]);
        Assert.True(percents[0] <= InstallOperations.ExtractEnd);
        Assert.Equal(3, reports.Count(item => item.Log?.StartsWith("解压", StringComparison.Ordinal) == true));
        Assert.Equal(3, reports.Count(item => item.Log?.StartsWith("复制", StringComparison.Ordinal) == true));
        Assert.Contains(reports, item => item.Log == "解压  lib/a.dll  (2.0 KB)");
    }

    private sealed class InlineProgress(Action<InstallProgress> report) : IProgress<InstallProgress>
    {
        public void Report(InstallProgress value) => report(value);
    }

    [Fact]
    public void ResolveExistingInstall_RequiresInstalledExecutable()
    {
        var install = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"), "Keep Password");
        Directory.CreateDirectory(install);

        Assert.Null(InstallOperations.ResolveExistingInstall(null, "0.2.2", true, true));
        Assert.Null(InstallOperations.ResolveExistingInstall("   ", "0.2.2", true, true));
        Assert.Null(InstallOperations.ResolveExistingInstall(install, "0.2.2", true, true));
        Assert.Null(InstallOperations.ResolveExistingInstall(Path.Combine(install, "missing"), "0.2.2", true, true));

        File.WriteAllText(Path.Combine(install, InstallConstants.AppExecutableName()), "x");
        var existing = InstallOperations.ResolveExistingInstall("  " + install + "  ", " 0.2.2 ", true, false);
        Assert.NotNull(existing);
        Assert.Equal(Path.GetFullPath(install), existing.Directory);
        Assert.Equal("0.2.2", existing.Version);
        Assert.True(existing.HasStartMenuShortcut);
        Assert.False(existing.HasDesktopShortcut);
        Assert.Null(InstallOperations.ResolveExistingInstall(install, "", false, false)!.Version);
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

        InstallOperations.Uninstall(install, deleteCache: false, unregisterFromSystem: false);
        if (OperatingSystem.IsWindows())
        {
            var removed = false;
            for (var i = 0; i < 25 && !removed; i++)
            {
                Thread.Sleep(200);
                removed = !Directory.Exists(install);
            }

            Assert.True(removed);
        }
        else
        {
            Assert.False(Directory.Exists(install));
        }

        Assert.True(File.Exists(Path.Combine(cache, "vault.kpvault")));
    }
}
