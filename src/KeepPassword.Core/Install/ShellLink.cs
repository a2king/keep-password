using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KeepPassword.Core.Install;

/// <summary>
/// 通过 COM 的 IShellLink 直接创建 / 比对快捷方式，不再启动 powershell.exe。
/// 原来每次创建快捷方式都会拉起一个带 -ExecutionPolicy Bypass 的 PowerShell 子进程，
/// 这正是安全软件最常拦截的行为之一，而且慢、依赖本机 PowerShell 可用。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ShellLink
{
    public static bool Matches(string shortcutPath, string target, string workingDirectory)
    {
        if (!File.Exists(shortcutPath))
        {
            return false;
        }

        var link = (IShellLinkW)new CShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcutPath, 0);
            var path = new string('\0', 520);
            link.GetPath(path, path.Length, IntPtr.Zero, 4);
            var folder = new string('\0', 520);
            link.GetWorkingDirectory(folder, folder.Length);
            return SamePath(Trim(path), target) && SamePath(Trim(folder), workingDirectory);
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void Write(string shortcutPath, string target, string workingDirectory)
    {
        var folder = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var link = (IShellLinkW)new CShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(workingDirectory);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(shortcutPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    private static string Trim(string value)
    {
        var end = value.IndexOf('\0');
        return end >= 0 ? value[..end] : value;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            left.TrimEnd('\\', '/'),
            right.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink
    {
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] string file, int maxPath, IntPtr data, int flags);
        void GetIDList(out IntPtr idList);
        void SetIDList(IntPtr idList);
        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] string name, int maxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] string args, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int maxPath, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string relativePath, int reserved);
        void Resolve(IntPtr window, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);
        [PreserveSig]
        int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, int mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
