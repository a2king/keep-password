using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Sync;

/// <summary>本机保存的设备同步状态（设备令牌经 DPAPI/设备密钥保护）。</summary>
public sealed class SyncSessionState
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("keep-password/sync-device/v1");

    public string Email { get; set; } = "";
    public string DeviceToken { get; set; } = "";
    public int DeviceId { get; set; }
    public long VaultVersion { get; set; }
    public string? BindingKeyFingerprint { get; set; }
    public string BaseUrl { get; set; } = SyncDefaults.BaseUrl;
    public int PollMinutes { get; set; } = (int)SyncDefaults.DefaultPollInterval.TotalMinutes;

    public static string PathFor(string vaultPath) => vaultPath + ".sync";

    public static SyncSessionState? TryLoad(string vaultPath)
    {
        var path = PathFor(vaultPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var stored = File.ReadAllBytes(path);
            var json = Unprotect(stored);
            if (json is null)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<SyncSessionState>(json, Options);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(json);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        {
            return null;
        }
    }

    public void Save(string vaultPath)
    {
        var path = PathFor(vaultPath);
        var json = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        try
        {
            var stored = Protect(json);
            try
            {
                VaultStore.WriteProtectedFile(path, stored);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    public static void Delete(string vaultPath)
    {
        try
        {
            var path = PathFor(vaultPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static byte[] Protect(byte[] json)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ProtectedData.Protect(json, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        // 非 Windows：与 .quick 类似，依赖文件权限；后续可换成设备密钥。
        return json.ToArray();
    }

    private static byte[]? Unprotect(byte[] stored)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ProtectedData.Unprotect(stored, Entropy, DataProtectionScope.CurrentUser);
            }
            catch (Exception ex) when (ex is CryptographicException or PlatformNotSupportedException)
            {
            }
        }

        return stored.ToArray();
    }
}
