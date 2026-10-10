using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace KeepPassword.App.Platform.Windows;

[SupportedOSPlatform("windows")]
internal static class WindowsProcessInfo
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;

    public static void Query(int processId, out string processName, out bool elevated)
    {
        processName = "";
        elevated = true;
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return;
        }

        try
        {
            var name = new StringBuilder(1024);
            var size = name.Capacity;
            if (QueryFullProcessImageName(process, 0, name, ref size))
            {
                processName = Path.GetFileName(name.ToString());
            }

            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return;
            }

            try
            {
                var elevation = 0;
                var length = sizeof(int);
                if (GetTokenInformation(token, TokenElevation, ref elevation, length, out _))
                {
                    elevated = elevation != 0;
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, int flags, StringBuilder name, ref int size);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int informationClass, ref int information, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);
}
