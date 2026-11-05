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
    /// <summary>当前版本号，取自 Directory.Build.props 编进程序集的版本，不再在代码里另写一份。</summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var assembly = typeof(InstallConstants).Assembly;
        var text = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var plus = text.IndexOf('+');
        return plus >= 0 ? text[..plus] : text;
    }

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
