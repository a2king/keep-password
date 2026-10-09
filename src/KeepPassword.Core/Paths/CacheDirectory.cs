using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeepPassword.Core.Paths;

/// <summary>
/// 缓存目录可以改。配置文件放在固定位置，不跟缓存文件一起搬走。
/// </summary>
public static class CacheDirectory
{
    public const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string SettingsFile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return SettingsFileFor(AppDataPaths.Current, home, roaming);
    }

    public static string SettingsFileFor(PlatformKind platform, string home, string? roamingAppData)
    {
        return platform switch
        {
            PlatformKind.Windows => Join('\\', Require(roamingAppData), AppDataPaths.FolderName, SettingsFileName),
            PlatformKind.MacOS => Join('/', Trim(home), "Library", "Preferences", AppDataPaths.FolderName, SettingsFileName),
            PlatformKind.Linux => Join('/', Trim(home), ".config", AppDataPaths.FolderName, SettingsFileName),
            _ => throw new ArgumentOutOfRangeException(nameof(platform))
        };
    }

    public static string Resolve(string settingsFile, string defaultDirectory)
    {
        if (!File.Exists(settingsFile))
        {
            return Normalize(defaultDirectory);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(settingsFile), Options);
            if (string.IsNullOrWhiteSpace(settings?.Directory))
            {
                return Normalize(defaultDirectory);
            }

            return Normalize(settings.Directory);
        }
        catch (JsonException)
        {
            return Normalize(defaultDirectory);
        }
    }

    public static string Switch(string settingsFile, string currentDirectory, string newDirectory)
    {
        if (string.IsNullOrWhiteSpace(newDirectory))
        {
            throw new ArgumentException("请填写缓存目录。", nameof(newDirectory));
        }

        var source = Normalize(currentDirectory);
        var destination = Normalize(newDirectory);
        if (Same(source, destination))
        {
            Write(settingsFile, destination);
            return destination;
        }

        if (IsInside(source, destination) || IsInside(destination, source))
        {
            throw new InvalidOperationException("新目录不能放在当前缓存目录里面，当前缓存目录也不能放在新目录里面。");
        }

        var settingsDirectory = Path.GetDirectoryName(Path.GetFullPath(settingsFile));
        if (!string.IsNullOrEmpty(settingsDirectory)
            && (Same(destination, Normalize(settingsDirectory)) || IsInside(destination, Path.GetFullPath(settingsFile))))
        {
            throw new InvalidOperationException("缓存目录不能使用配置文件所在的目录。");
        }

        Directory.CreateDirectory(destination);
        var entries = Directory.Exists(source)
            ? Directory.EnumerateFileSystemEntries(source).ToList()
            : [];
        foreach (var entry in entries)
        {
            var target = Path.Combine(destination, Path.GetFileName(entry));
            if (File.Exists(target) || Directory.Exists(target))
            {
                throw new InvalidOperationException("目标目录里已经有同名文件：" + Path.GetFileName(entry));
            }
        }

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var entry in entries)
            {
                var target = Path.Combine(destination, Path.GetFileName(entry));
                MoveEntry(entry, target);
                moved.Add((entry, target));
            }

            Write(settingsFile, destination);
        }
        catch
        {
            foreach (var (from, to) in Enumerable.Reverse(moved))
            {
                try
                {
                    MoveEntry(to, from);
                }
                catch (IOException)
                {
                }
            }

            throw;
        }

        if (Directory.Exists(source))
        {
            try
            {
                Directory.Delete(source, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException("缓存文件已转移到新目录，但旧目录未能完全清除：" + ex.Message, ex);
            }
        }

        return destination;
    }

    private static void MoveEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source).ToList())
            {
                MoveEntry(child, Path.Combine(destination, Path.GetFileName(child)));
            }

            Directory.Delete(source, recursive: false);
            return;
        }

        try
        {
            File.Move(source, destination);
        }
        catch (IOException)
        {
            File.Copy(source, destination, overwrite: false);
            File.Delete(source);
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(destination, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (PlatformNotSupportedException)
                {
                }
            }
        }
    }

    private static void Write(string settingsFile, string directory)
    {
        var folder = Path.GetDirectoryName(settingsFile);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(new SettingsDto { Directory = directory }, Options);
        var temp = settingsFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, settingsFile, overwrite: true);
    }

    private static bool IsInside(string parent, string child)
    {
        var relative = Path.GetRelativePath(parent, child);
        return relative != "."
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path.Trim());
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Require(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("需要配置目录。", nameof(path));
        }

        return Trim(path);
    }

    private static string Trim(string path) => path.Trim().TrimEnd('\\', '/');

    private static string Join(char separator, params string[] parts) => string.Join(separator, parts);

    private sealed class SettingsDto
    {
        public string? Directory { get; set; }
    }
}
