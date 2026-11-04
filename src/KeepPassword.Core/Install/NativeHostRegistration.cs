using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KeepPassword.Core.Browser;
using KeepPassword.Core.Paths;
using Microsoft.Win32;

namespace KeepPassword.Core.Install;

/// <summary>
/// 把已上架扩展的固定 ID 登记到本机 Native Messaging。
/// 桌面端安装或启动后，用户再安装商店版扩展即可连接，不必再跑注册脚本。
/// </summary>
public static partial class NativeHostRegistration
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string ManifestPath() =>
        Path.Combine(AppDataPaths.VaultDirectory(), InstallConstants.NativeHostName + ".json");

    public static IReadOnlyList<string> AllowedOrigins(IEnumerable<string?>? existing)
    {
        var origins = new List<string>();
        AddId(origins, BrowserExtensionLinks.EdgeCrxId);
        AddId(origins, BrowserExtensionLinks.SideloadExtensionId);
        if (existing is null)
        {
            return origins;
        }

        foreach (var item in existing)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            var match = ExtensionOrigin().Match(item.Trim());
            if (match.Success)
            {
                AddId(origins, match.Groups[1].Value);
            }
        }

        return origins;
    }

    /// <summary>重新写入 Edge / Chrome 的本机连接，并认领已经安装的扩展。</summary>
    public static string RepairInstalledHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "这项修复只在 Windows 上可用。";
        }

        var host = Path.Combine(AppContext.BaseDirectory, "KeepPassword.NativeHost.exe");
        if (!File.Exists(host))
        {
            return "找不到本机连接程序，请重新安装 Keep Password。";
        }

        try
        {
            Register(host);
            return "已重新注册 Edge 扩展连接。请完全退出并重新打开 Edge 后再试。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return "注册失败：" + ex.Message;
        }
    }

    /// <summary>当前程序目录里有 Native Host 时写入注册表。失败时不打断启动。</summary>
    public static bool TryRegisterInstalledHost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var host = Path.Combine(AppContext.BaseDirectory, "KeepPassword.NativeHost.exe");
            if (!File.Exists(host))
            {
                return false;
            }

            Register(host);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    public static string Register(string hostExe)
    {
        var hostPath = Path.GetFullPath(hostExe);
        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("找不到 Native Host。", hostPath);
        }

        var manifestPath = ManifestPath();
        var folder = Path.GetDirectoryName(manifestPath);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var origins = AllowedOrigins(ReadExistingOrigins(manifestPath).Concat(DiscoverInstalledOrigins()));
        var json = JsonSerializer.Serialize(new HostManifest(hostPath, origins), JsonOptions);
        File.WriteAllText(manifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        foreach (var keyPath in InstallConstants.NativeHostRegistryKeys)
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key?.SetValue("", manifestPath);
        }

        return manifestPath;
    }

    public static string ExtensionIdForPath(string path)
    {
        return ToExtensionId(SHA256.HashData(Encoding.Unicode.GetBytes(path)));
    }

    public static string ExtensionIdForPublicKey(string base64PublicKey)
    {
        var key = Convert.FromBase64String(base64PublicKey);
        return ToExtensionId(SHA256.HashData(key));
    }

    private static string ToExtensionId(byte[] hash)
    {
        var chars = new char[32];
        for (var i = 0; i < 16; i++)
        {
            chars[i * 2] = (char)('a' + (hash[i] >> 4));
            chars[i * 2 + 1] = (char)('a' + (hash[i] & 0x0F));
        }

        return new string(chars);
    }

    private static IEnumerable<string> DiscoverInstalledOrigins()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local))
            {
                return [];
            }

            var origins = new List<string>();
            foreach (var browser in new[] { Path.Combine("Microsoft", "Edge", "User Data"), Path.Combine("Google", "Chrome", "User Data") })
            {
                var root = Path.Combine(local, browser);
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var profile in Directory.EnumerateDirectories(root))
                {
                    CollectFromSecurePreferences(origins, Path.Combine(profile, "Secure Preferences"));
                    CollectUnpacked(origins, Path.Combine(profile, "UnpackedExtensions"));
                }
            }

            return origins;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void CollectUnpacked(List<string> origins, string unpackedRoot)
    {
        if (!Directory.Exists(unpackedRoot))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(unpackedRoot))
        {
            AddInstalledExtension(origins, directory, directory);
        }
    }

    private static void CollectFromSecurePreferences(List<string> origins, string preferencesPath)
    {
        if (!File.Exists(preferencesPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(preferencesPath));
            if (!document.RootElement.TryGetProperty("extensions", out var extensions)
                || !extensions.TryGetProperty("settings", out var settings)
                || settings.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var extension in settings.EnumerateObject())
            {
                if (!extension.Value.TryGetProperty("path", out var pathElement)
                    || pathElement.GetString() is not { Length: > 0 } path)
                {
                    continue;
                }

                AddInstalledExtension(origins, path, path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
    }

    private static void AddInstalledExtension(List<string> origins, string extensionDirectory, string pathIdentity)
    {
        try
        {
            var manifestPath = Path.Combine(extensionDirectory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var root = document.RootElement;
            if (!root.TryGetProperty("name", out var name) || name.GetString() != "Keep Password")
            {
                return;
            }

            if (!HasNativeMessaging(root))
            {
                return;
            }

            var id = root.TryGetProperty("key", out var key) && key.GetString() is { Length: > 0 } publicKey
                ? ExtensionIdForPublicKey(publicKey)
                : ExtensionIdForPath(pathIdentity);
            AddId(origins, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
        }
    }

    private static bool HasNativeMessaging(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("permissions", out var permissions) || permissions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var permission in permissions.EnumerateArray())
        {
            if (permission.GetString() == "nativeMessaging")
            {
                return true;
            }
        }

        return false;
    }

    private static void AddId(List<string> origins, string extensionId)
    {
        var origin = "chrome-extension://" + extensionId.Trim().ToLowerInvariant() + "/";
        if (!origins.Contains(origin, StringComparer.Ordinal))
        {
            origins.Add(origin);
        }
    }

    private static IEnumerable<string> ReadExistingOrigins(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
            if (!document.RootElement.TryGetProperty("allowed_origins", out var origins)
                || origins.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return origins.EnumerateArray()
                .Select(item => item.GetString())
                .Where(item => item is not null)
                .Cast<string>()
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex(@"^chrome-extension://([a-p]{32})/$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionOrigin();

    private sealed class HostManifest
    {
        public HostManifest(string path, IReadOnlyList<string> allowedOrigins)
        {
            Path = path;
            AllowedOrigins = allowedOrigins;
        }

        [JsonPropertyName("name")]
        public string Name { get; } = InstallConstants.NativeHostName;

        [JsonPropertyName("description")]
        public string Description { get; } = "Keep Password 本机消息宿主";

        [JsonPropertyName("path")]
        public string Path { get; }

        [JsonPropertyName("type")]
        public string Type { get; } = "stdio";

        [JsonPropertyName("allowed_origins")]
        public IReadOnlyList<string> AllowedOrigins { get; }
    }
}
