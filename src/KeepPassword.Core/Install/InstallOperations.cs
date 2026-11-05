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

    public static InstallSummary InstallFromDirectory(
        string sourceRoot,
        string targetRoot,
        InstallOptions? options = null,
        IProgress<InstallProgress>? progress = null)
    {
        options ??= new InstallOptions();
        var where = options.Locations ?? InstallLocations.Default();
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

        var summary = new InstallSummary(files.Count, 0, 0, 0, 0, false);
        if (OperatingSystem.IsWindows() && options.RegisterWithSystem)
        {
            summary = RegisterWithWindows(targetRoot, options, where, summary, progress);
        }

        progress?.Report(new InstallProgress(100, "安装完成", $"安装完成，共 {files.Count} 个文件"));
        return summary;
    }

    [SupportedOSPlatform("windows")]
    private static InstallSummary RegisterWithWindows(
        string targetRoot,
        InstallOptions options,
        InstallLocations where,
        InstallSummary summary,
        IProgress<InstallProgress>? progress)
    {
        var registry = new RegistryWriter();
        progress?.Report(new InstallProgress(CopyEnd, "正在写入卸载信息…", "核对卸载信息（没变化就不重复写入）"));
        WriteWindowsUninstallInfo(targetRoot, where, registry);
        var hostExe = Path.Combine(targetRoot, "KeepPassword.NativeHost.exe");
        if (File.Exists(hostExe))
        {
            progress?.Report(new InstallProgress(94, "正在连接浏览器扩展…", "核对 Edge 扩展的本机连接"));
            var discovered = options.DiscoveredOrigins ?? NativeHostRegistration.DiscoverOrigins();
            NativeHostRegistration.Register(hostExe, where, discovered, registry);
        }

        var appExe = Path.Combine(targetRoot, InstallConstants.AppExecutableName());
        var written = 0;
        var same = 0;
        progress?.Report(new InstallProgress(96, "正在创建快捷方式…", "核对快捷方式"));
        SyncShortcut(where.StartMenuShortcut, options.CreateStartMenuShortcut, appExe, targetRoot, ref written, ref same);
        SyncShortcut(where.DesktopShortcut, options.CreateDesktopShortcut, appExe, targetRoot, ref written, ref same);

        var autostartChanged = false;
        if (options.LaunchAtStartup is { } enabled)
        {
            progress?.Report(new InstallProgress(99, "正在保存开机启动设置…", enabled ? "开机自动启动：开" : "开机自动启动：关"));
            CacheDirectory.SetLaunchAtStartup(where.SettingsFile, enabled);
            var minimized = CacheDirectory.GetStartMinimized(where.SettingsFile);
            autostartChanged = AutostartRegistration.Apply(appExe, enabled, minimized, where.Autostart, AutostartPlatform.Windows);
        }

        return summary with
        {
            RegistryWrites = registry.Writes,
            RegistryUnchanged = registry.Unchanged,
            ShortcutsWritten = written,
            ShortcutsUnchanged = same,
            AutostartChanged = autostartChanged
        };
    }

    /// <summary>需要则创建（已一致就不重写），不需要则删除。</summary>
    [SupportedOSPlatform("windows")]
    private static void SyncShortcut(string path, bool wanted, string target, string workingDirectory, ref int written, ref int same)
    {
        if (!wanted)
        {
            TryDeleteFile(path);
            return;
        }

        if (ShellLink.Matches(path, target, workingDirectory))
        {
            same++;
            return;
        }

        try
        {
            ShellLink.Write(path, target, workingDirectory);
            written++;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // 快捷方式不是安装成败的关键，创建不出来也不应该让整个安装失败。
        }
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
            File.Exists(InstallLocations.Default().StartMenuShortcut),
            File.Exists(InstallLocations.Default().DesktopShortcut));
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

    /// <summary>保险库与设置所在目录（用户可能改过位置），卸载页要把它明确告诉用户。</summary>
    public static string DataDirectory() =>
        CacheDirectory.Resolve(CacheDirectory.SettingsFile(), AppDataPaths.VaultDirectory());

    /// <summary>退出安装目录里仍在运行的主程序和浏览器连接程序，返回处理了多少个。</summary>
    public static int StopRunningProduct(string installRoot)
    {
        var running = FindRunningProductProcesses(installRoot);
        var count = running.Count;
        CloseRunningProductProcesses(running);
        return count;
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

    public static void Uninstall(string installRoot, bool deleteCache, bool unregisterFromSystem = true) =>
        Uninstall(installRoot, deleteCache, unregisterFromSystem, InstallLocations.Default());

    internal static void Uninstall(string installRoot, bool deleteCache, bool unregisterFromSystem, InstallLocations where)
    {
        if (OperatingSystem.IsWindows() && unregisterFromSystem)
        {
            TryDeleteFile(where.StartMenuShortcut);
            TryDeleteFile(where.DesktopShortcut);
            RemoveWindowsUninstallInfo(where);
        }

        if (deleteCache)
        {
            var settings = where.SettingsFile;
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

        StartDeferredDelete("rmdir /s /q \"%KP_TARGET%\" >nul 2>&1", directory.TrimEnd('\\', '/'));
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

        StartDeferredDelete("del /f /q \"%KP_TARGET%\" >nul 2>&1", full);
    }

    /// <summary>
    /// 脚本本身只含 ASCII，要删的路径通过环境变量 KP_TARGET 传给 cmd（环境块是 Unicode）。
    /// 旧做法把路径直接写进用 ASCII 编码的 .cmd，路径里有中文（例如中文用户名）就会变成问号，
    /// rmdir 找不到目录，目录删不掉，脚本还会在重试循环里一直挂着。现在最多重试约 2 分钟。
    /// </summary>
    private static void StartDeferredDelete(string deleteCommand, string target)
    {
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine("set /a TRIES=0");
        script.AppendLine(":retry");
        script.AppendLine("ping 127.0.0.1 -n 2 >nul");
        script.AppendLine(deleteCommand);
        script.AppendLine("if not exist \"%KP_TARGET%\" goto done");
        script.AppendLine("set /a TRIES+=1");
        script.AppendLine("if %TRIES% lss 120 goto retry");
        script.AppendLine(":done");
        script.AppendLine("del \"%~f0\" >nul 2>&1");
        var bat = Path.Combine(Path.GetTempPath(), "keep-password-clean-" + Guid.NewGuid().ToString("n") + ".cmd");
        File.WriteAllText(bat, script.ToString(), Encoding.ASCII);
        var start = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/d /c \"\"" + bat + "\"\"",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.Environment["KP_TARGET"] = target;
        Process.Start(start);
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
    private static void WriteWindowsUninstallInfo(string installRoot, InstallLocations where, RegistryWriter registry)
    {
        var uninstall = Path.Combine(installRoot, InstallConstants.UninstallExecutableName());
        var displayIcon = Path.Combine(installRoot, InstallConstants.AppExecutableName());
        var key = where.UninstallKey;
        registry.SetString(key, "DisplayName", InstallConstants.ProductName);
        registry.SetString(key, "DisplayVersion", InstallConstants.Version);
        registry.SetString(key, "Publisher", InstallConstants.Publisher);
        registry.SetString(key, "InstallLocation", installRoot);
        registry.SetString(key, "DisplayIcon", displayIcon);
        registry.SetString(key, "UninstallString", "\"" + uninstall + "\"");
        registry.SetString(key, "QuietUninstallString", "\"" + uninstall + "\" --quiet");
        registry.SetDword(key, "NoModify", 1);
        registry.SetDword(key, "NoRepair", 1);
        var size = Directory.EnumerateFiles(installRoot, "*", SearchOption.AllDirectories)
            .Select(path => new FileInfo(path).Length)
            .Sum() / 1024;
        registry.SetDword(key, "EstimatedSize", checked((int)Math.Min(size, int.MaxValue)));
    }

    [SupportedOSPlatform("windows")]
    private static void RemoveWindowsUninstallInfo(InstallLocations where)
    {
        var registry = new RegistryWriter();
        registry.DeleteTree(where.UninstallKey);
        foreach (var key in where.NativeHostKeys)
        {
            try
            {
                registry.DeleteTree(key);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
            }
        }

        // 开机启动项以前卸载时没人清理，卸载后每次开机都会去找一个已经不存在的程序。
        try
        {
            AutostartRegistration.Apply("", false, false, where.Autostart, AutostartPlatform.Windows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        TryDeleteFile(where.NativeHostManifest);
    }

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
