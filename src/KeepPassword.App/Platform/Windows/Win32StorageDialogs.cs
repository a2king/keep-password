using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KeepPassword.App.Platform.Windows;

/// <summary>
/// Avalonia 11.3 的 StorageProvider 在 Windows 上容易卡死。
/// 在 UI 线程（本身已是 STA）直接调用系统对话框，避免跨线程 HWND 崩溃。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32StorageDialogs
{
    public static string? PickOpenFile(nint owner, string title, string filter)
    {
        const int maxChars = 1024;
        var fileBuffer = Marshal.AllocHGlobal(maxChars * 2);
        var filterBuffer = AllocDoubleNullString(filter);
        try
        {
            ZeroMemory(fileBuffer, maxChars * 2);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterBuffer,
                lpstrFile = fileBuffer,
                nMaxFile = maxChars,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir | OfnEnableSizing
            };

            return GetOpenFileNameW(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
        }
    }

    public static string? PickSaveFile(nint owner, string title, string filter, string defaultName)
    {
        const int maxChars = 1024;
        var fileBuffer = Marshal.AllocHGlobal(maxChars * 2);
        var filterBuffer = AllocDoubleNullString(filter);
        try
        {
            ZeroMemory(fileBuffer, maxChars * 2);
            if (!string.IsNullOrWhiteSpace(defaultName))
            {
                var bytes = Encoding.Unicode.GetBytes(defaultName + "\0");
                Marshal.Copy(bytes, 0, fileBuffer, Math.Min(bytes.Length, maxChars * 2));
            }

            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filterBuffer,
                lpstrFile = fileBuffer,
                nMaxFile = maxChars,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnPathMustExist | OfnOverwritePrompt | OfnNoChangeDir | OfnEnableSizing
            };
            return GetSaveFileNameW(ref ofn) ? Marshal.PtrToStringUni(fileBuffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(fileBuffer);
            Marshal.FreeHGlobal(filterBuffer);
        }
    }

    public static string? PickFolder(nint owner, string title)
    {
        var displayName = Marshal.AllocHGlobal(260 * 2);
        try
        {
            ZeroMemory(displayName, 260 * 2);
            var bi = new BrowseInfo
            {
                hwndOwner = owner,
                pszDisplayName = displayName,
                lpszTitle = title,
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle
            };
            var pidl = SHBrowseForFolderW(ref bi);
            if (pidl == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var path = new StringBuilder(260);
                return SHGetPathFromIDListW(pidl, path) ? path.ToString() : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(displayName);
        }
    }

    private static IntPtr AllocDoubleNullString(string value)
    {
        // 过滤器中间含 \0，不能按普通 LPWSTR 封送。
        var text = value;
        if (!text.EndsWith("\0\0", StringComparison.Ordinal))
        {
            text = text.TrimEnd('\0') + "\0\0";
        }

        var bytes = Encoding.Unicode.GetBytes(text);
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static void ZeroMemory(IntPtr ptr, int bytes)
    {
        for (var i = 0; i < bytes; i++)
        {
            Marshal.WriteByte(ptr, i, 0);
        }
    }

    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;
    private const int OfnEnableSizing = 0x00800000;
    private const int OfnOverwritePrompt = 0x00000002;
    private const uint BifReturnOnlyFsDirs = 0x00000001;
    private const uint BifNewDialogStyle = 0x00000040;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr pidl, StringBuilder pszPath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public IntPtr lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public IntPtr lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public IntPtr lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo
    {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }
}
