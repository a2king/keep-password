using KeepPassword.Core.Install;

namespace KeepPassword.Core.Update;

public static class UpdatePaths
{
    public const string CacheFolderName = "cache";
    public const string SetupAssetName = "KeepPassword-Setup-win-x64.exe";
    public const string Sha256AssetName = SetupAssetName + ".sha256";
    public const string GitHubOwner = "a2king";
    public const string GitHubRepo = "keep-password";

    public static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";

    public static string ReleasesPageUrl =>
        $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest";

    /// <summary>
    /// 已通过安装程序登记的目录，或当前进程位于默认安装目录 / 带卸载程序的目录时返回安装根；纯便携解压返回 null。
    /// </summary>
    public static string? ResolveInstallRoot(string? processBaseDirectory = null)
    {
        var existing = InstallOperations.FindExistingInstall();
        if (existing is not null)
        {
            return existing.Directory;
        }

        var baseDir = processBaseDirectory ?? AppContext.BaseDirectory;
        string directory;
        try
        {
            directory = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception)
        {
            return null;
        }

        if (!File.Exists(Path.Combine(directory, InstallConstants.AppExecutableName())))
        {
            return null;
        }

        var defaultInstall = Path.GetFullPath(InstallConstants.DefaultInstallDirectory())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var hasUninstaller = File.Exists(Path.Combine(directory, InstallConstants.UninstallExecutableName()));
        var isDefault = string.Equals(directory, defaultInstall, StringComparison.OrdinalIgnoreCase);
        return hasUninstaller || isDefault ? directory : null;
    }

    public static string CacheDirectory(string installRoot) =>
        Path.Combine(Path.GetFullPath(installRoot), CacheFolderName);

    public static string SetupPath(string installRoot) =>
        Path.Combine(CacheDirectory(installRoot), SetupAssetName);

    public static bool IsUnderUpdateCache(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(filePath);
            var name = Path.GetFileName(full);
            if (!name.StartsWith("KeepPassword-Setup", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(full);
            if (parent is null)
            {
                return false;
            }

            return string.Equals(Path.GetFileName(parent), CacheFolderName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>删除 cache 中残留的安装包、哈希与临时文件。</summary>
    public static void CleanupCache(string? installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return;
        }

        string cache;
        try
        {
            cache = CacheDirectory(installRoot);
        }
        catch (Exception)
        {
            return;
        }

        if (!Directory.Exists(cache))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(cache))
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("KeepPassword-Setup", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
