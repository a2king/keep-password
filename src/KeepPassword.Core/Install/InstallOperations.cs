using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.Versioning;
using System.Text;
using KeepPassword.Core.Paths;
using KeepPassword.Core.Update;
using Microsoft.Win32;

namespace KeepPassword.Core.Install;

public static class InstallOperations
{
    public const double ExtractStart = 5;
    public const double ExtractEnd = 55;
    private const double CleanupEnd = 58;
    private const double CopyEnd = 92;

    public static void ExtractZip(
        Stream zipStream,
        string destinationRoot,
        IProgress<InstallProgress>? progress = null)
    {
        Directory.CreateDirectory(destinationRoot);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !string.IsNullOrEmpty(entry.Name)).ToList();
        var total = Math.Max(entries.Sum(entry => Math.Max(entry.Length, 1)), 1);
        long done = 0;
        foreach (var entry in entries)
        {
            var target = SafeEntryPath(destinationRoot, entry.FullName);
            var folder = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            entry.ExtractToFile(target, overwrite: true);
            done += Math.Max(entry.Length, 1);
            progress?.Report(new InstallProgress(
                Scale(ExtractStart, ExtractEnd, done, total),
                "正在解压安装文件…",
                $"解压  {entry.FullName.Replace('\\', '/')}  ({FormatSize(entry.Length)})"));
        }
    }

    private static double Scale(double start, double end, long done, long total) =>
        start + (end - start) * done / total;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:0.0} MB",
        >= 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes} B"
    };

    public static string SafeEntryPath(string destinationRoot, string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            throw new InvalidDataException("压缩包包含空路径。");
        }

        var normalized = entryName.Replace('\\', '/');
        if (normalized.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(entryName) || normalized.StartsWith('/'))
        {
            throw new InvalidDataException("压缩包路径无效。");
        }

        var root = Path.GetFullPath(destinationRoot);
        var target = Path.GetFullPath(Path.Combine(root, entryName.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException("压缩包路径无效。");
        }

        return target;
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
        progress?.Report(new InstallProgress(ExtractEnd, "正在清理旧版本…", "检查旧版本残留文件"));

        if (Directory.Exists(targetRoot))
        {
            foreach (var existing in EnumerateRelativeFiles(targetRoot).ToList())
            {
                if (desired.Contains(existing) || IsUpdateCacheRelative(existing))
                {
                    continue;
                }

                TryDeleteFile(Path.Combine(targetRoot, existing));
                progress?.Report(new InstallProgress(ExtractEnd, "正在清理旧版本…", "删除  " + existing.Replace('\\', '/')));
            }

            foreach (var directory in Directory.EnumerateDirectories(targetRoot, "*", SearchOption.AllDirectories)
                         .OrderByDescending(item => item.Length)
                         .ToList())
            {
                if (IsUpdateCachePath(targetRoot, directory))
                {
                    continue;
                }

                if (IsEmpty(directory))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }

        var files = desired.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToList();
        var sizes = files.ToDictionary(item => item, item => Math.Max(new FileInfo(Path.Combine(sourceRoot, item)).Length, 1), StringComparer.OrdinalIgnoreCase);
        var total = Math.Max(sizes.Values.Sum(), 1);
        long done = 0;
        progress?.Report(new InstallProgress(CleanupEnd, "正在复制程序文件…", "安装到  " + targetRoot));
        foreach (var relative in files)
        {
            var from = Path.Combine(sourceRoot, relative);
            var to = Path.Combine(targetRoot, relative);
            var folder = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
            }

            File.Copy(from, to, overwrite: true);
            done += sizes[relative];
            progress?.Report(new InstallProgress(Scale(CleanupEnd, CopyEnd, done, total), "正在复制程序文件…", "复制  " + relative.Replace('\\', '/')));
        }

        if (OperatingSystem.IsWindows() && options.RegisterWithSystem)
        {
            progress?.Report(new InstallProgress(CopyEnd, "正在写入卸载信息…", "写入卸载信息（当前用户注册表）"));
            WriteWindowsUninstallInfo(targetRoot);
            if (options.CreateStartMenuShortcut)
            {
                progress?.Report(new InstallProgress(95, "正在创建快捷方式…", "创建开始菜单快捷方式"));
                CreateWindowsShortcut(StartMenuShortcutPath(), targetRoot);
            }
            else
            {
                TryDeleteFile(StartMenuShortcutPath());
            }

            if (options.CreateDesktopShortcut)
            {
                progress?.Report(new InstallProgress(98, "正在创建快捷方式…", "创建桌面快捷方式"));
                CreateWindowsShortcut(DesktopShortcutPath(), targetRoot);
            }
            else
            {
                TryDeleteFile(DesktopShortcutPath());
            }
        }

        progress?.Report(new InstallProgress(100, "安装完成", $"安装完成，共 {files.Count} 个文件"));
    }

    public static ExistingInstall? FindExistingInstall()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var key = Registry.CurrentUser.OpenSubKey(InstallConstants.UninstallRegistryKey);
        return ResolveExistingInstall(
            key?.GetValue("InstallLocation") as string,
            key?.GetValue("DisplayVersion") as string,
            File.Exists(StartMenuShortcutPath()),
            File.Exists(DesktopShortcutPath()));
    }

    /// <summary>
    /// 查找仍在运行、且可执行文件位于安装目录内的 Keep Password 相关进程（不含当前进程与安装程序自身）。
    /// </summary>
    public static IReadOnlyList<Process> FindRunningProductProcesses(string installRoot)
    {
        string root;
        try
        {
            root = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        }
        catch (Exception)
        {
            return [];
        }

        var selfId = Environment.ProcessId;
        var found = new List<Process>();
        foreach (var processName in new[] { InstallConstants.AppFileName, "KeepPassword.NativeHost" })
        {
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName(processName);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (process.Id == selfId || process.HasExited)
                    {
                        process.Dispose();
                        continue;
                    }

                    string? path = null;
                    try
                    {
                        path = process.MainModule?.FileName;
                    }
                    catch (Exception)
                    {
                        // 无权限读模块时，同名进程仍视为可能占用安装目录
                    }

                    if (path is not null)
                    {
                        var full = Path.GetFullPath(path);
                        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            process.Dispose();
                            continue;
                        }
                    }

                    found.Add(process);
                }
                catch (Exception)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        return found;
    }

    public static void CloseRunningProductProcesses(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                try
                {
                    process.CloseMainWindow();
                }
                catch (Exception)
                {
                }

                if (!process.WaitForExit(2500) && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }
    }

    public static bool IsFileInUseException(Exception? ex)
    {
        const int sharingViolation = unchecked((int)0x80070020);
        const int lockViolation = unchecked((int)0x80070021);
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is not IOException io)
            {
                continue;
            }

            if (io.HResult is sharingViolation or lockViolation)
            {
                return true;
            }

            var message = io.Message;
            if (message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase)
                || message.Contains("另一个程序正在使用此文件", StringComparison.Ordinal)
                || message.Contains("正由另一进程使用", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static ExistingInstall? ResolveExistingInstall(string? location, string? version, bool hasStartMenuShortcut, bool hasDesktopShortcut)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        string directory;
        try
        {
            directory = Path.GetFullPath(location.Trim());
        }
        catch (Exception)
        {
            return null;
        }

        if (!File.Exists(Path.Combine(directory, InstallConstants.AppExecutableName())))
        {
            return null;
        }

        return new ExistingInstall(
            directory,
            string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            hasStartMenuShortcut,
            hasDesktopShortcut);
    }

    public static void Uninstall(string installRoot, bool deleteCache, bool unregisterFromSystem = true)
    {
        if (OperatingSystem.IsWindows() && unregisterFromSystem)
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

    /// <summary>当前进程退出后再删除文件（用于删除正在运行的安装包自身）。</summary>
    public static void ScheduleDeleteFile(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(filePath);
        }
        catch (Exception)
        {
            return;
        }

        if (!OperatingSystem.IsWindows())
        {
            TryDeleteFile(full);
            return;
        }

        var bat = Path.Combine(Path.GetTempPath(), "keep-password-del-" + Guid.NewGuid().ToString("n") + ".cmd");
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("set TARGET=" + full);
        script.AppendLine(":retry");
        script.AppendLine("ping 127.0.0.1 -n 2 >nul");
        script.AppendLine("del /f /q \"%TARGET%\" >nul 2>&1");
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
        foreach (var key in InstallConstants.NativeHostRegistryKeys)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
            }
            catch (Exception)
            {
            }
        }

        TryDeleteFile(Path.Combine(AppDataPaths.VaultDirectory(), InstallConstants.NativeHostName + ".json"));
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

    private static bool IsUpdateCacheRelative(string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var prefix = UpdatePaths.CacheFolderName + Path.DirectorySeparatorChar;
        return string.Equals(normalized, UpdatePaths.CacheFolderName, StringComparison.OrdinalIgnoreCase)
               || normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUpdateCachePath(string installRoot, string directory)
    {
        try
        {
            var cache = Path.GetFullPath(UpdatePaths.CacheDirectory(installRoot));
            var full = Path.GetFullPath(directory);
            if (string.Equals(full, cache, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var prefix = cache.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

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
