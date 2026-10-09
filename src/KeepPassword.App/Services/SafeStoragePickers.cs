using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using KeepPassword.App.Platform.Windows;

namespace KeepPassword.App.Services;

/// <summary>
/// 统一选文件/选目录。Windows 走系统 STA 对话框，避免 Avalonia 文件框卡死；
/// 打开对话框期间会暂停程序补全扫描。
/// </summary>
public static class SafeStoragePickers
{
    public static async Task<string?> PickOpenFileAsync(
        TopLevel owner,
        string title,
        IReadOnlyList<FilePickerFileType>? filters,
        Action<bool>? pauseWatcher = null)
    {
        pauseWatcher?.Invoke(true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return await PickOpenFileWindowsAsync(owner, title, filters);
            }

            var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
                FileTypeFilter = filters
            });
            return files.Count == 0 ? null : files[0].TryGetLocalPath();
        }
        finally
        {
            pauseWatcher?.Invoke(false);
        }
    }

    public static async Task<string?> PickFolderAsync(
        TopLevel owner,
        string title,
        Action<bool>? pauseWatcher = null)
    {
        pauseWatcher?.Invoke(true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return await PickFolderWindowsAsync(owner, title);
            }

            var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false
            });
            return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
        }
        finally
        {
            pauseWatcher?.Invoke(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task<string?> PickOpenFileWindowsAsync(
        TopLevel owner,
        string title,
        IReadOnlyList<FilePickerFileType>? filters)
    {
        var handle = TopLevelHandle(owner);
        return Win32StorageDialogs.PickOpenFileAsync(handle, title, ToWin32Filter(filters));
    }

    [SupportedOSPlatform("windows")]
    private static Task<string?> PickFolderWindowsAsync(TopLevel owner, string title)
    {
        var handle = TopLevelHandle(owner);
        return Win32StorageDialogs.PickFolderAsync(handle, title);
    }

    private static nint TopLevelHandle(TopLevel owner)
    {
        try
        {
            return owner.TryGetPlatformHandle()?.Handle ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string ToWin32Filter(IReadOnlyList<FilePickerFileType>? filters)
    {
        if (filters is null || filters.Count == 0)
        {
            return "所有文件\0*.*\0\0";
        }

        var parts = new List<string>();
        foreach (var filter in filters)
        {
            var patterns = filter.Patterns is { Count: > 0 }
                ? string.Join(";", filter.Patterns)
                : "*.*";
            parts.Add(filter.Name);
            parts.Add(patterns);
        }

        parts.Add("所有文件");
        parts.Add("*.*");
        return string.Join('\0', parts) + "\0\0";
    }
}
