using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KeepPassword.App.Platform.Windows;

/// <summary>
/// Avalonia 11.3 在 Windows 上用 MTA 调文件框，容易和 UI 线程死锁。
/// 这里在独立 STA 线程里调用系统对话框，避开这个问题。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32StorageDialogs
{
    public static Task<string?> PickOpenFileAsync(nint owner, string title, string filter)
    {
        return RunSta(() =>
        {
            var buffer = new StringBuilder(260);
            var ofn = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = owner,
                lpstrFilter = filter,
                lpstrFile = buffer,
                nMaxFile = buffer.Capacity,
                lpstrTitle = title,
                Flags = OfnExplorer | OfnFileMustExist | OfnPathMustExist | OfnNoChangeDir
            };
            return GetOpenFileName(ref ofn) ? ofn.lpstrFile?.ToString() : null;
        });
    }

    public static Task<string?> PickFolderAsync(nint owner, string title)
    {
        return RunSta(() =>
        {
            var bi = new BrowseInfo
            {
                hwndOwner = owner,
                lpszTitle = title,
                ulFlags = BifReturnOnlyFsDirs | BifNewDialogStyle
            };
            var pidl = SHBrowseForFolder(ref bi);
            if (pidl == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var path = new StringBuilder(260);
                return SHGetPathFromIDList(pidl, path) ? path.ToString() : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        });
    }

    private static Task<string?> RunSta(Func<string?> work)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(work());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "KeepPassword.FileDialog"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    private const int OfnExplorer = 0x00080000;
    private const int OfnFileMustExist = 0x00001000;
    private const int OfnPathMustExist = 0x00000800;
    private const int OfnNoChangeDir = 0x00000008;
    private const uint BifReturnOnlyFsDirs = 0x00000001;
    private const uint BifNewDialogStyle = 0x00000040;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileName(ref OpenFileName ofn);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolder(ref BrowseInfo lpbi);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDList(IntPtr pidl, StringBuilder pszPath);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public string? lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public StringBuilder? lpstrFile;
        public int nMaxFile;
        public string? lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
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
        public string? lpszTitle;
        public uint ulFlags;
        public IntPtr lpfn;
        public IntPtr lParam;
        public int iImage;
    }
}
