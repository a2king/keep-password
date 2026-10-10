namespace KeepPassword.Core.Install;

public static class InstallConstants
{
    public const string ProductName = "Keep Password";
    public const string Publisher = "Keep Password";
    public const string AppFileName = "KeepPassword";
    public const string UninstallFileName = "Uninstall";
    public const string ReadmeFileName = "使用说明.txt";
    public const string LibFolderName = "lib";
    public const string NativeHostFolderName = "native-host";
    public const string ExtensionFolderName = "extension";
    public const string UninstallRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KeepPassword";
    public const string NativeHostName = "com.keeppassword.host";

    public static IReadOnlyList<string> NativeHostRegistryKeys { get; } =
    [
        @"Software\Google\Chrome\NativeMessagingHosts\" + NativeHostName,
        @"Software\Microsoft\Edge\NativeMessagingHosts\" + NativeHostName
    ];
    public const string Version = "0.3.2";

    public static string DefaultInstallDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "Programs", "KeepPassword");
    }

    public static string AppExecutableName() =>
        OperatingSystem.IsWindows() ? AppFileName + ".exe" : AppFileName;

    public static string UninstallExecutableName() =>
        OperatingSystem.IsWindows() ? UninstallFileName + ".exe" : UninstallFileName;
}
