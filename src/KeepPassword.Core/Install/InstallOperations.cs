using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using KeepPassword.Core.Paths;
using Microsoft.Win32;

namespace KeepPassword.Core.Install;

public static class InstallOperations
{
    public static void InstallFromDirectory(string sourceRoot, string targetRoot)
    {
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException("找不到安装包内容：" + sourceRoot);
        }

        Directory.CreateDirectory(targetRoot);
        var desired = EnumerateRelativeFiles(sourceRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(targetRoot))
        {
            foreach (var existing in EnumerateRelativeFiles(targetRoot).ToList())
            {
                if (desired.Contains(existing))
                {
                    continue;
                }

                var path = Path.Combine(targetRoot, existing);
                TryDeleteFile(path);
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

        foreach (var relative in desired.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            var from = Path.Combine(sourceRoot, relative);
            var to = Path.Combine(targetRoot, relative);
            var folder = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.Copy(from, to, overwrite: true);
        }

        if (OperatingSystem.IsWindows())
        {
            WriteWindowsUninstallInfo(targetRoot);
            CreateWindowsShortcut(targetRoot);
        }
    }

    public static void Uninstall(string installRoot, bool deleteCache)
    {
        if (OperatingSystem.IsWindows())
        {
            RemoveWindowsShortcut();
            RemoveWindowsUninstallInfo();
        }

        if (Directory.Exists(installRoot))
        {
            foreach (var file in Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories))
            {
                TryDeleteFile(file);
            }

            TryDeleteDirectory(installRoot);
        }

        if (!deleteCache)
        {
            return;
        }

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

    public static IEnumerable<string> EnumerateRelativeFiles(string root)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(root, file);
        }
    }

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
        key.SetValue("EstimatedSize", checked((int)size), RegistryValueKind.DWord);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindowsUninstallInfo()
    {
        Registry.CurrentUser.DeleteSubKeyTree(InstallConstants.UninstallRegistryKey, throwOnMissingSubKey: false);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindowsShortcut(string installRoot)
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        Directory.CreateDirectory(programs);
        var shortcut = Path.Combine(programs, InstallConstants.ProductName + ".lnk");
        var target = Path.Combine(installRoot, InstallConstants.AppExecutableName());
        WriteShortcutViaPowerShell(shortcut, target, installRoot);
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindowsShortcut()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        TryDeleteFile(Path.Combine(programs, InstallConstants.ProductName + ".lnk"));
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
