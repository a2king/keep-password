using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace KeepPassword.Core.Vault;

public static class FileProtection
{
    public static void RestrictToCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            RestrictWindows(path);
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            else if (Directory.Exists(path))
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RestrictWindows(string path)
    {
        var user = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确定当前用户。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
