namespace KeepPassword.Core.Paths;

public enum PlatformKind
{
    Windows,
    MacOS,
    Linux
}

/// <summary>
/// 各系统的数据目录。保险库文件本身与系统无关，只是存放位置不同。
/// </summary>
public static class AppDataPaths
{
    public const string FolderName = "KeepPassword";
    public const string VaultFileName = "vault.kpvault";

    public static PlatformKind Current
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return PlatformKind.Windows;
            }

            if (OperatingSystem.IsMacOS())
            {
                return PlatformKind.MacOS;
            }

            return PlatformKind.Linux;
        }
    }

    public static string VaultDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return For(Current, home, localAppData);
    }

    public static string VaultFile()
    {
        var separator = Current == PlatformKind.Windows ? '\\' : '/';
        return VaultDirectory() + separator + VaultFileName;
    }

    public static string For(PlatformKind platform, string home, string? localAppData)
    {
        return platform switch
        {
            PlatformKind.Windows => Join('\\', RequireLocalAppData(localAppData), FolderName),
            PlatformKind.MacOS => Join('/', Trim(home), "Library", "Application Support", FolderName),
            PlatformKind.Linux => Join('/', Trim(home), ".local", "share", FolderName),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
    }

    public static string VaultFileFor(PlatformKind platform, string home, string? localAppData)
    {
        var separator = platform == PlatformKind.Windows ? '\\' : '/';
        return For(platform, home, localAppData) + separator + VaultFileName;
    }

    private static string RequireLocalAppData(string? localAppData)
    {
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new ArgumentException("需要本地应用数据目录。", nameof(localAppData));
        }

        return Trim(localAppData);
    }

    private static string Trim(string path) => path.Trim().TrimEnd('\\', '/');

    private static string Join(char separator, params string[] parts) => string.Join(separator, parts);
}
