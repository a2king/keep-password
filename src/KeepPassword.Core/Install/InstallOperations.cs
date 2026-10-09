using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using KeepPassword.Core.Paths;
using Microsoft.Win32;

namespace KeepPassword.Core.Install;

public static class InstallOperations
{
    public static void ExtractZip(
        Stream zipStream,
        string destinationRoot,
        IProgress<InstallProgress>? progress = null)
    {
        Directory.CreateDirectory(destinationRoot);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var total = Math.Max(entries.Count, 1);
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var target = Path.Combine(destinationRoot, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            entry.ExtractToFile(target, overwrite: true);
            progress?.Report(new InstallProgress((i + 1) * 50.0 / total, "正在解压 " + entry.Name));
        }
    }

    public static void InstallFromDirectory(
        string sourceRoot,
        string targetRoot,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null)
    {
        options ??= new InstallOptions();
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("找不到安装包内容：" + sourceRoot);
        }

        Directory.CreateDirectory(targetRoot);
        var desired = EnumerateRelativeFiles(sourceRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        progress?.Report(new InstallProgress(52, "正在清理旧版本…"));

        if (Directory.Exists(targetRoot))
        {
            foreach (var existing in EnumerateRelativeFiles(targetRoot).ToList())
            {
                if (desired.Contains(existing))
                {
                    continue;
                }

                TryDeleteFile(Path.Combine(targetRoot, existing));
            }

            foreach (var directory in Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(item => item.Length)
                         .ToList())
            {
                if (IsEmpty(directory))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }

        var files = desired.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        var total = Math.Max(files.Count, 1);
        for (var i = 0; i < files.Count; i++)
        {
            var relative = files[i];
            var from = Path.Combine(sourceRoot, relative);
            var to = Path.Combine(targetRoot, relative);
            var folder = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.Copy(from, to, overwrite: true);
            progress?.Report(new InstallProgress(55 + (i + 1) * 35.0 / total, "正在复制 " + Path.GetFileName(relative)));
        }

        if (OperatingSystem.IsWindows())
        {
            progress?.Report(new InstallProgress(93, "正在写入卸载信息…"));
            WriteWindowsUninstallInfo(targetRoot);
            if (options.CreateStartMenuShortcut)
            {
                progress?.Report(new InstallProgress(96, "正在创建开始菜单快捷方式…"));
                CreateWindowsShortcut(StartMenuShortcutPath(), targetRoot);
            }
            else
            {
                TryDeleteFile(StartMenuShortcutPath());
            }

            if (options.CreateDesktopShortcut)
            {
                progress?.Report(new InstallProgress(98, "正在创建桌面快捷方式…"));
                CreateWindowsShortcut(DesktopShortcutPath(), targetRoot);
            }
            else
            {
                TryDeleteFile(DesktopShortcutPath());
            }
        }

        progress?.Report(new InstallProgress(100, "安装完成"));
    }

    public static void Uninstall(string installRoot, bool deleteCache)
    {
        if (OperatingSystem.IsWindows())
        {
            TryDeleteFile(StartMenuShortcutPath());
            TryDeleteFile(DesktopShortcutPath());
            RemoveWindowsUninstallInfo();
        }

        if (deleteCache)
        {
            var settings = CacheDirectory.SettingsFile();
            var cache = CacheDirectory.Resolve(settings, AppDataPaths.VaultDirectory());
            if (Directory.Exists(cache))
            {
                TryDeleteDirectory(cache);
            }

            if (File.Exists(settings))
            {
                TryDeleteFile(settings);
                var settingsDir = Path.GetDirectoryName(settings);
                if (!string.IsNullOrEmpty(settingsDir) && IsEmpty(settingsDir))
                {
                    TryDeleteDirectory(settingsDir);
                }
            }
        }

        // 安装目录里可能正运行着 Uninstall.exe；Windows 用延后脚本删除，避免必须再打一份单文件运行时。
        if (OperatingSystem.IsWindows())
        {
            ScheduleDeleteDirectory(installRoot);
            return;
        }

        if (Directory.Exists(installRoot))
        {
            foreach (var file in Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories))
            {
                TryDeleteFile(file);
            }

            TryDeleteDirectory(installRoot);
        }
    }

    public static void ScheduleDeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            TryDeleteDirectory(directory);
            return;
        }

        var bat = Path.Combine(Path.GetTempPath(), "keep-password-remove-" + Guid.NewGuid().ToString("n") + ".cmd");
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("set TARGET=" + directory.TrimEnd('\\'));
        script.AppendLine(":retry");
        script.AppendLine("ping 127.0.0.1 -n 2 >nul");
        script.AppendLine("rmdir /s /q \"%TARGET%\"");
        script.AppendLine("if exist \"%TARGET%\" goto retry");
        script.AppendLine("del \"%~f0\"");
        File.WriteAllText(bat, script.ToString(), Encoding.ASCII);
        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    public static IEnumerable<string> EnumerateRelativeFiles(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, file);
        }
    }

    private static string StartMenuShortcutPath()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        return Path.Combine(programs, InstallConstants.ProductName + ".lnk");
    }

    private static string DesktopShortcutPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            InstallConstants.ProductName + ".lnk");

    [SupportedOSPlatform("windows")]
    private static void WriteWindowsUninstallInfo(string installRoot)
    {
        var uninstall = Path.Combine(installRoot, InstallConstants.UninstallExecutableName());
        var displayIcon = Path.Combine(installRoot, InstallConstants.AppExecutableName());
        using var key = Registry.CurrentUser.CreateSubKey(InstallConstants.UninstallRegistryKey);
        key.SetValue("DisplayName", InstallConstants.ProductName);
        key.SetValue("DisplayVersion", InstallConstants.Version);
        key.SetValue("Publisher", InstallConstants.Publisher);
        key.SetValue("InstallLocation", installRoot);
        key.SetValue("DisplayIcon", displayIcon);
        key.SetValue("UninstallString", "\"" + uninstall + "\"");
        key.SetValue("QuietUninstallString", "\"" + uninstall + "\" --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        var size = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum() / 1024;
        key.SetValue("EstimatedSize", checked((int)Math.Min(size, int.MaxValue)), RegistryValueKind.DWord);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindowsUninstallInfo()
    {
        Registry.CurrentUser.DeleteSubKeyTree(InstallConstants.UninstallRegistryKey, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsShortcut(string shortcutPath, string installRoot)
    {
        var target = Path.Combine(installRoot, InstallConstants.AppExecutableName());
        WriteShortcutViaPowerShell(shortcutPath, target, installRoot);
    }

    private static void WriteShortcutViaPowerShell(string shortcutPath, string targetPath, string workingDirectory)
    {
        var script = new StringBuilder();
        script.Append("$ws = New-Object -ComObject WScript.Shell; ");
        script.Append("$s = $ws.CreateShortcut('").Append(EscapePs(shortcutPath)).Append("'); ");
        script.Append("$s.TargetPath = '").Append(EscapePs(targetPath)).Append("'; ");
        script.Append("$s.WorkingDirectory = '").Append(EscapePs(workingDirectory)).Append("'; ");
        script.Append("$s.IconLocation = '").Append(EscapePs(targetPath)).Append(",0'; ");
        script.Append("$s.Save();");
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + script + "\"",
                UseShellExecute = false,
                CreateNoWindow = true
            })?.WaitForExit(15000);
        }
        catch (Exception)
        {
        }
    }

    private static string EscapePs(string value) => value.Replace("'", "''");

    private static bool IsEmpty(string directory) =>
        !Directory.EnumerateFileSystemEntries(directory).Any();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception)
        {
        }
    }
}
