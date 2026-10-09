using System.Runtime.Versioning;
using KeepPassword.Core.Autofill;

namespace KeepPassword.App.Platform;

public static class PlatformAutofill
{
    public static IPasswordFieldDetector CreateDetector()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsDetector();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new Mac.MacOsPasswordFieldDetector();
        }

        return new Linux.LinuxPasswordFieldDetector();
    }

    public static ICredentialFiller CreateFiller()
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsFiller();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new Mac.MacOsCredentialFiller();
        }

        return new Linux.LinuxCredentialFiller();
    }

    [SupportedOSPlatform("windows")]
    private static IPasswordFieldDetector CreateWindowsDetector() => new Windows.WindowsPasswordFieldDetector();

    [SupportedOSPlatform("windows")]
    private static ICredentialFiller CreateWindowsFiller() => new Windows.WindowsCredentialFiller();
}
