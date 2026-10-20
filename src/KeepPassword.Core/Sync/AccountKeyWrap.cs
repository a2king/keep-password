using System.Security.Cryptography;
using System.Text;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Sync;

/// <summary>用官网账号密码在本地派生密钥，包裹保险库 .kpkey（服务器不可解密）。</summary>
public static class AccountKeyWrap
{
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("keep-password/account-wrap/v1");

    public static byte[] Wrap(string accountPassword, byte[] dataKey, KdfProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(dataKey);
        if (string.IsNullOrEmpty(accountPassword))
        {
            throw new ArgumentException("请填写账号密码。", nameof(accountPassword));
        }

        profile ??= KdfProfile.Interactive;
        var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
        var wrappingKey = Argon2Id.Derive(accountPassword, profile, salt);
        try
        {
            var (nonce, ciphertext, tag) = VaultCipher.Encrypt(wrappingKey, dataKey, Aad);
            var blob = new byte[1 + salt.Length + nonce.Length + tag.Length + ciphertext.Length];
            blob[0] = 1;
            var offset = 1;
            salt.CopyTo(blob, offset);
            offset += salt.Length;
            nonce.CopyTo(blob, offset);
            offset += nonce.Length;
            tag.CopyTo(blob, offset);
            offset += tag.Length;
            ciphertext.CopyTo(blob, offset);
            return blob;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    public static byte[] Unwrap(string accountPassword, byte[] blob, KdfProfile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(blob);
        if (string.IsNullOrEmpty(accountPassword))
        {
            throw new ArgumentException("请填写账号密码。", nameof(accountPassword));
        }

        if (blob.Length < 1 + Argon2Id.SaltLength + VaultCipher.NonceLength + VaultCipher.TagLength + Argon2Id.HashLength
            || blob[0] != 1)
        {
            throw new CredentialRejectedException("云端密钥包格式不正确。");
        }

        profile ??= KdfProfile.Interactive;
        var offset = 1;
        var salt = blob.AsSpan(offset, Argon2Id.SaltLength).ToArray();
        offset += Argon2Id.SaltLength;
        var nonce = blob.AsSpan(offset, VaultCipher.NonceLength).ToArray();
        offset += VaultCipher.NonceLength;
        var tag = blob.AsSpan(offset, VaultCipher.TagLength).ToArray();
        offset += VaultCipher.TagLength;
        var ciphertext = blob.AsSpan(offset).ToArray();

        var wrappingKey = Argon2Id.Derive(accountPassword, profile, salt);
        try
        {
            return VaultCipher.Decrypt(wrappingKey, nonce, ciphertext, tag, Aad);
        }
        catch (CryptographicException)
        {
            throw new CredentialRejectedException("账号密码无法解开云端密钥包。若刚重置过官网密码，请在本机用新密码重新上传密钥包，或手动导入 .kpkey。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }
}
