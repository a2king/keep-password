using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public sealed partial class VaultStore
{
    public bool UsesIndependentKey(string path) => ReadEnvelope(path).Version == FormatVersion;

    private static byte[] PayloadAad(string account, int version) => version == 1
        ? VaultCipher.AssociatedData(account)
        : Encoding.UTF8.GetBytes("keep-password/payload/v2|" + account);

    private static byte[] PasswordWrapAad(string account) =>
        Encoding.UTF8.GetBytes("keep-password/password-wrap/v2|" + account);

    internal static CipherDto WrapDataKey(string password, KdfMaterial kdf, byte[] dataKey, string account)
    {
        var wrappingKey = Argon2Id.Derive(password, kdf.Profile, kdf.Salt);
        try
        {
            return CipherDto.From(VaultCipher.Encrypt(wrappingKey, dataKey, PasswordWrapAad(account)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    internal static byte[] UnwrapDataKey(string password, KdfMaterial kdf, CipherDto? wrap, string account)
    {
        var wrappingKey = Argon2Id.Derive(password, kdf.Profile, kdf.Salt);
        if (wrap is null) // Legacy v1 vault, upgraded explicitly when changing its master password.
        {
            return wrappingKey;
        }
        try
        {
            return Unwrap(wrappingKey, wrap, PasswordWrapAad(account));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
        }
    }

    internal static void UpdateKeyHeader(string path, KdfMaterial kdf, CipherDto passwordWrap, bool keyFileSaved)
    {
        var envelope = ReadEnvelope(path);
        if (envelope.Version != FormatVersion)
        {
            throw new InvalidDataException("请先升级保险库密钥格式。");
        }
        envelope.Kdf = KdfDto.From(kdf);
        envelope.PasswordWrap = passwordWrap;
        envelope.KeyFileSaved = keyFileSaved;
        // The payload, nonce and authentication tag are deliberately preserved byte-for-byte.
        WriteEnvelope(path, envelope);
    }

    private static void WriteEnvelope(string path, EnvelopeDto envelope) =>
        WriteProtectedFile(path, JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeOptions));

    internal static void WriteProtectedFile(string path, byte[] bytes, bool overwrite = true)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                FileProtection.RestrictToCurrentUser(temp);
                stream.Write(bytes);
                stream.Flush(true);
            }
            File.Move(temp, path, overwrite);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void ValidatePasswordWrap(CipherDto? wrap)
    {
        if (wrap is null || wrap.Algorithm != "aes-256-gcm")
        {
            throw new InvalidDataException("保险库密钥保护信息已损坏。");
        }
        RequireDecodedLength(wrap.Nonce, VaultCipher.NonceLength);
        RequireDecodedLength(wrap.Tag, VaultCipher.TagLength);
        RequireDecodedLength(wrap.Ciphertext, Argon2Id.HashLength);
    }

    internal static IReadOnlyList<VaultEntry> ReadEntriesWithKey(string path, string encryptionKey)
    {
        var envelope = ReadEnvelope(path, allowBackup: true);
        RequireIndependentKey(envelope);
        var dataKey = VaultKeyFile.Parse(encryptionKey);
        try
        {
            return DecryptPayload(envelope.Account, dataKey, envelope.Cipher!, envelope.Version).Entries;
        }
        catch (CryptographicException)
        {
            throw new CredentialRejectedException("加密密钥不匹配，或备份文件已损坏。");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    internal static void ExportEncryptedBackup(string vaultPath, string destination)
    {
        var source = ReadEnvelope(vaultPath);
        RequireIndependentKey(source);
        // A portable backup contains no password-wrapped data key or quick-unlock material.
        // Knowing a past master password alone cannot unlock an exported v2 backup.
        WriteEnvelope(destination, new EnvelopeDto
        {
            Version = FormatVersion,
            Format = "keep-password-backup",
            Account = source.Account,
            Cipher = source.Cipher
        });
    }

    private static void ValidateBackupEnvelope(EnvelopeDto envelope)
    {
        if (envelope.Version != FormatVersion || string.IsNullOrWhiteSpace(envelope.Account)
            || envelope.Account.Length > 256 || envelope.PasswordWrap is not null
            || envelope.Kdf is not null || envelope.ShortKey is not null || envelope.Recovery is not null
            || envelope.Cipher is not { Algorithm: "aes-256-gcm" } cipher)
            throw new InvalidDataException("加密备份文件格式不正确。");
        RequireDecodedLength(cipher.Nonce, VaultCipher.NonceLength);
        RequireDecodedLength(cipher.Tag, VaultCipher.TagLength);
        if (Convert.FromBase64String(cipher.Ciphertext).Length is 0 or > 16 * 1024 * 1024)
            throw new InvalidDataException("加密备份文件内容不正确。");
    }

    public VaultSession RecoverWithKey(string path, string encryptionKey, string newMasterPassword)
    {
        if (string.IsNullOrEmpty(newMasterPassword))
        {
            throw new ArgumentException("请填写新的主密码。", nameof(newMasterPassword));
        }
        var envelope = ReadEnvelope(path);
        RequireIndependentKey(envelope);
        var dataKey = VaultKeyFile.Parse(encryptionKey);
        var handedOff = false;
        try
        {
            var (entries, labels) = DecryptPayload(envelope.Account, dataKey, envelope.Cipher!, envelope.Version);
            var kdf = new KdfMaterial(Profile, RandomNumberGenerator.GetBytes(Argon2Id.SaltLength));
            var passwordWrap = WrapDataKey(newMasterPassword, kdf, dataKey, envelope.Account);
            UpdateKeyHeader(path, kdf, passwordWrap, envelope.KeyFileSaved);
            DeleteQuickUnlock(path);
            var session = new VaultSession(path, envelope.Account, kdf, envelope.ShortKey!.ToMaterial(), dataKey,
                entries, labels: labels, passwordWrap: passwordWrap, keyFileSaved: envelope.KeyFileSaved);
            handedOff = true;
            return session;
        }
        catch (CryptographicException)
        {
            throw new CredentialRejectedException("加密密钥不匹配，或保险库文件已损坏。");
        }
        finally
        {
            if (!handedOff)
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
    }

    private static void RequireIndependentKey(EnvelopeDto envelope)
    {
        if (envelope.Version != FormatVersion)
        {
            throw new InvalidOperationException("这是旧版保险库。请先打开原保险库，在设置中更换主密码并保存加密密钥，再重新导出备份。");
        }
    }
}
