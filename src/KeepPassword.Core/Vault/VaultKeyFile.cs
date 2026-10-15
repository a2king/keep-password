using System.Security.Cryptography;
using System.Text.Json;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

/// <summary>A portable, unencrypted copy of the independent data key, saved only at the user's chosen path.</summary>
public static class VaultKeyFile
{
    internal static string Encode(byte[] key) => "KP2-" + Convert.ToBase64String(key);

    public static string Read(string path)
    {
        if (new FileInfo(path).Length > 4096)
        {
            throw new InvalidDataException("请选择 Keep Password 加密密钥文件（.kpkey）。");
        }
        var text = File.ReadAllText(path);
        var key = Parse(text);
        try { return Encode(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    internal static byte[] Parse(string text)
    {
        try
        {
            var value = text.Trim();
            if (value.Length > 4096) throw new FormatException();
            if (value.StartsWith('{'))
            {
                using var json = JsonDocument.Parse(value);
                var root = json.RootElement;
                if (root.GetProperty("format").GetString() != "keep-password-key"
                    || root.GetProperty("version").GetInt32() != 1) throw new FormatException();
                value = root.GetProperty("key").GetString() ?? "";
            }
            if (!value.StartsWith("KP2-", StringComparison.Ordinal)) throw new FormatException();
            var key = Convert.FromBase64String(value[4..]);
            if (key.Length == Argon2Id.HashLength) return key;
            CryptographicOperations.ZeroMemory(key);
            throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new CredentialRejectedException("加密密钥格式不正确，请选择密钥文件或粘贴完整的 KP2- 密钥。");
        }
    }

    internal static byte[] Serialize(byte[] key) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "keep-password-key",
        version = 1,
        key = Encode(key),
        notice = "请永久保存并与保险库备份分开保管。此文件可解密对应的保险库和备份；更换主密码不会改变此密钥。"
    }, new JsonSerializerOptions { WriteIndented = true });
}
