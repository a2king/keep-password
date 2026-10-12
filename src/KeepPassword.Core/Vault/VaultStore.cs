using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public sealed class VaultStore
{
    public const int FormatVersion = 1;

    private static readonly JsonSerializerOptions EnvelopeOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public VaultStore(KdfProfile? profile = null)
    {
        Profile = profile ?? KdfProfile.Interactive;
        Profile.Validate();
    }

    public KdfProfile Profile { get; }

    public bool Exists(string path) => File.Exists(path);

    public void Create(string path, string account, string masterPassword, string shortKey, RecoverySetup? recovery = null)
    {
        using var session = CreateSession(path, account, masterPassword, shortKey, recovery);
    }

    public VaultSession CreateSession(string path, string account, string masterPassword, string shortKey, RecoverySetup? recovery = null)
    {
        RequireSecrets(account, masterPassword, shortKey);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("保险库已存在。");
        }

        var trimmed = account.Trim();
        var kdf = new KdfMaterial(Profile, RandomNumberGenerator.GetBytes(Argon2Id.SaltLength));
        var masterKey = Argon2Id.Derive(masterPassword, kdf.Profile, kdf.Salt);
        var entries = new List<VaultEntry>();
        RecoverySecrets? secrets = null;
        byte[]? wrapKey = null;
        var handedOff = false;
        try
        {
            secrets = recovery is null ? null : RecoverySecrets.Create(recovery, Profile);
            var shortKeyMaterial = HashShortKey(shortKey, Profile);
            var (wrap, key) = CreateShortWrap(shortKey, Profile, masterKey);
            wrapKey = key;
            WriteFile(path, trimmed, kdf, shortKeyMaterial, masterKey, entries, secrets);
            try
            {
                WriteQuickUnlock(path, kdf.Salt, wrap);
            }
            catch
            {
                TryDeleteFile(path);
                DeleteQuickUnlock(path);
                throw;
            }

            handedOff = true;
            return new VaultSession(path, trimmed, kdf, shortKeyMaterial, masterKey, entries, secrets, null, wrap, wrapKey);
        }
        finally
        {
            if (!handedOff)
            {
                secrets?.Zero();
                CryptographicOperations.ZeroMemory(masterKey);
                if (wrapKey is not null)
                {
                    CryptographicOperations.ZeroMemory(wrapKey);
                }
            }
        }
    }

    public bool HasRecovery(string path) => ReadEnvelope(path).Recovery is not null;

    public IReadOnlyList<string> RecoveryQuestions(string path)
    {
        var recovery = ReadEnvelope(path).Recovery ?? throw new InvalidOperationException("此保险库没有启用安全问题恢复。");
        return recovery.Questions;
    }

    public string ReadAccount(string path) => ReadEnvelope(path).Account;

    public bool HasShortUnlock(string path)
    {
        try
        {
            var salt = ReadEnvelope(path).Kdf!.ToMaterial().Salt;
            var quick = TryReadQuickUnlock(path, salt);
            quick?.Zero();
            return quick is not null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public VaultSession Unlock(string path, string masterPassword)
    {
        if (string.IsNullOrEmpty(masterPassword))
        {
            throw new ArgumentException("请填写主密码。", nameof(masterPassword));
        }

        var envelope = ReadEnvelope(path);
        var shortKeyDto = envelope.ShortKey ?? throw new InvalidDataException("保险库文件无法读取。");
        var kdfDto = envelope.Kdf ?? throw new InvalidDataException("保险库文件无法读取。");
        var cipher = envelope.Cipher ?? throw new InvalidDataException("保险库文件无法读取。");
        var kdf = kdfDto.ToMaterial();
        var masterKey = Argon2Id.Derive(masterPassword, kdf.Profile, kdf.Salt);
        List<VaultEntry> entries;
        VaultLabels labels;
        try
        {
            (entries, labels) = DecryptPayload(envelope.Account, masterKey, cipher);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new UnlockFailedException();
        }

        RecoverySecrets? recovery;
        try
        {
            recovery = UnwrapRecovery(envelope.Recovery, masterKey);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new InvalidDataException("保险库恢复数据已损坏。");
        }

        var quick = TryReadQuickUnlock(path, kdf.Salt);
        var device = false;
        ShortWrapMaterial? wrap = null;
        if (quick is null)
        {
            device = TryWriteDeviceQuickUnlock(path, kdf.Salt, masterKey);
        }
        else
        {
            device = quick.IsDevice;
            wrap = quick.Wrap;
            quick.Zero();
        }

        return new VaultSession(path, envelope.Account, kdf, shortKeyDto.ToMaterial(), masterKey, entries, recovery, labels, wrap, null, device);
    }

    public VaultSession UnlockWithShortKey(string path, string shortKey)
    {
        if (string.IsNullOrEmpty(shortKey))
        {
            throw new ArgumentException("请填写短密钥。", nameof(shortKey));
        }

        var envelope = ReadEnvelope(path);
        var shortKeyDto = envelope.ShortKey ?? throw new InvalidDataException("保险库文件无法读取。");
        var kdfDto = envelope.Kdf ?? throw new InvalidDataException("保险库文件无法读取。");
        var cipher = envelope.Cipher ?? throw new InvalidDataException("保险库文件无法读取。");
        var kdf = kdfDto.ToMaterial();
        var quick = TryReadQuickUnlock(path, kdf.Salt)
            ?? throw new InvalidOperationException("请先使用主密码解锁一次。");
        if (quick.IsDevice)
        {
            return UnlockWithDeviceKey(path, envelope, kdf, shortKeyDto.ToMaterial(), shortKey, cipher, quick);
        }

        var wrap = quick.Wrap ?? throw new InvalidDataException("保险库文件无法读取。");
        var wrapKey = Argon2Id.Derive(shortKey, wrap.Profile, wrap.Salt);
        byte[]? masterKey = null;
        var handedOff = false;
        try
        {
            try
            {
                masterKey = Unwrap(wrapKey, CipherFrom(wrap), ShortWrapAad());
                if (masterKey.Length != Argon2Id.HashLength)
                {
                    throw new CryptographicException();
                }
            }
            catch (CryptographicException)
            {
                throw new UnlockFailedException("短密钥不正确。");
            }

            List<VaultEntry> entries;
            VaultLabels labels;
            try
            {
                (entries, labels) = DecryptPayload(envelope.Account, masterKey, cipher);
            }
            catch (CryptographicException)
            {
                throw new UnlockFailedException("短密钥不正确。");
            }

            RecoverySecrets? recovery;
            try
            {
                recovery = UnwrapRecovery(envelope.Recovery, masterKey);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("保险库恢复数据已损坏。");
            }

            handedOff = true;
            return new VaultSession(path, envelope.Account, kdf, shortKeyDto.ToMaterial(), masterKey, entries, recovery, labels, wrap, wrapKey);
        }
        finally
        {
            if (!handedOff)
            {
                CryptographicOperations.ZeroMemory(wrapKey);
                if (masterKey is not null)
                {
                    CryptographicOperations.ZeroMemory(masterKey);
                }
            }
        }
    }

    private VaultSession UnlockWithDeviceKey(
        string path,
        EnvelopeDto envelope,
        KdfMaterial kdf,
        ShortKeyMaterial shortKey,
        string enteredShortKey,
        CipherDto cipher,
        QuickUnlockState quick)
    {
        var masterKey = quick.DeviceMasterKey ?? throw new InvalidDataException("保险库文件无法读取。");
        var handedOff = false;
        try
        {
            if (!Argon2Id.Verify(enteredShortKey, shortKey.Profile, shortKey.Salt, shortKey.Hash))
            {
                throw new UnlockFailedException("短密钥不正确。");
            }

            List<VaultEntry> entries;
            VaultLabels labels;
            try
            {
                (entries, labels) = DecryptPayload(envelope.Account, masterKey, cipher);
            }
            catch (CryptographicException)
            {
                throw new UnlockFailedException("短密钥不正确。");
            }

            RecoverySecrets? recovery;
            try
            {
                recovery = UnwrapRecovery(envelope.Recovery, masterKey);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException("保险库恢复数据已损坏。");
            }

            handedOff = true;
            return new VaultSession(path, envelope.Account, kdf, shortKey, masterKey, entries, recovery, labels, null, null, true);
        }
        finally
        {
            if (!handedOff)
            {
                CryptographicOperations.ZeroMemory(masterKey);
            }
        }
    }

    internal static void WriteFile(
        string path,
        string account,
        KdfMaterial kdf,
        ShortKeyMaterial shortKey,
        byte[] masterKey,
        IReadOnlyList<VaultEntry> entries,
        RecoverySecrets? recovery = null,
        VaultLabels? labels = null)
    {
        var payload = new PayloadDto
        {
            Spaces = labels?.Spaces.ToList() ?? [],
            Tags = labels?.Tags.ToList() ?? [],
            Entries = entries.Select(EntryDto.From).ToList()
        };

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(payload, PayloadOptions);
        try
        {
            var (nonce, ciphertext, tag) = VaultCipher.Encrypt(masterKey, plaintext, VaultCipher.AssociatedData(account));
            var envelope = new EnvelopeDto
            {
                Version = FormatVersion,
                Format = "keep-password-vault",
                Account = account,
                Kdf = KdfDto.From(kdf),
                ShortKey = ShortKeyDto.From(shortKey),
                Recovery = recovery is null ? null : RecoveryDto.From(recovery, masterKey),
                Cipher = new CipherDto
                {
                    Algorithm = "aes-256-gcm",
                    Nonce = Convert.ToBase64String(nonce),
                    Tag = Convert.ToBase64String(tag),
                    Ciphertext = Convert.ToBase64String(ciphertext)
                }
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(envelope, EnvelopeOptions);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(true);
            }

            FileProtection.RestrictToCurrentUser(temp);
            File.Move(temp, path, overwrite: true);
            FileProtection.RestrictToCurrentUser(path);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal static ShortKeyMaterial HashShortKey(string shortKey, KdfProfile profile)
    {
        var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
        var hash = Argon2Id.Hash(shortKey, profile, salt);
        return new ShortKeyMaterial(profile, salt, hash);
    }

    internal static (ShortWrapMaterial Material, byte[] Key) CreateShortWrap(string shortKey, KdfProfile profile, byte[] masterKey)
    {
        var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
        var key = Argon2Id.Derive(shortKey, profile, salt);
        var (nonce, ciphertext, tag) = VaultCipher.Encrypt(key, masterKey, ShortWrapAad());
        return (new ShortWrapMaterial(profile, salt, nonce, ciphertext, tag), key);
    }

    internal static ShortWrapMaterial RewrapShortUnlock(ShortWrapMaterial current, byte[] wrapKey, byte[] masterKey)
    {
        var (nonce, ciphertext, tag) = VaultCipher.Encrypt(wrapKey, masterKey, ShortWrapAad());
        return new ShortWrapMaterial(current.Profile, current.Salt, nonce, ciphertext, tag);
    }

    internal static void WriteQuickUnlock(string vaultPath, byte[] vaultSalt, ShortWrapMaterial wrap)
    {
        var dto = new QuickUnlockDto
        {
            Version = 1,
            Mode = "short",
            VaultSalt = Convert.ToBase64String(vaultSalt),
            Algorithm = "argon2id",
            Salt = Convert.ToBase64String(wrap.Salt),
            MemoryKb = wrap.Profile.MemoryKb,
            Iterations = wrap.Profile.Iterations,
            Parallelism = wrap.Profile.Parallelism,
            WrappedMasterKey = CipherFrom(wrap)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, EnvelopeOptions);
        var stored = ProtectQuick(json);
        CryptographicOperations.ZeroMemory(json);
        try
        {
            WriteQuickFile(QuickUnlockPath(vaultPath), stored);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    internal static bool TryWriteDeviceQuickUnlock(string vaultPath, byte[] vaultSalt, byte[] masterKey)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var dto = new QuickUnlockDto
        {
            Version = 1,
            Mode = "device",
            VaultSalt = Convert.ToBase64String(vaultSalt),
            DeviceMasterKey = Convert.ToBase64String(masterKey)
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(dto, EnvelopeOptions);
        byte[] stored;
        try
        {
            stored = ProtectedData.Protect(json, QuickEntropy, DataProtectionScope.CurrentUser);
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }

        try
        {
            WriteQuickFile(QuickUnlockPath(vaultPath), stored);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DeleteQuickUnlock(vaultPath);
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(stored);
        }
    }

    internal static void DeleteQuickUnlock(string vaultPath)
    {
        try
        {
            var path = QuickUnlockPath(vaultPath);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static QuickUnlockState? TryReadQuickUnlock(string vaultPath, byte[] vaultSalt)
    {
        var path = QuickUnlockPath(vaultPath);
        if (!File.Exists(path))
        {
            return null;
        }

        byte[] stored;
        try
        {
            stored = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var json = OpenQuick(stored);
        if (json is null)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<QuickUnlockDto>(json, EnvelopeOptions);
            if (dto is null || dto.Version != 1)
            {
                return null;
            }

            byte[] recordedSalt;
            try
            {
                recordedSalt = Convert.FromBase64String(dto.VaultSalt);
            }
            catch (FormatException)
            {
                return null;
            }

            if (recordedSalt.Length != vaultSalt.Length
                || !CryptographicOperations.FixedTimeEquals(recordedSalt, vaultSalt))
            {
                return null;
            }

            if (string.Equals(dto.Mode, "device", StringComparison.Ordinal))
            {
                return ReadDeviceQuick(dto);
            }

            if (dto.Mode.Length > 0 && !string.Equals(dto.Mode, "short", StringComparison.Ordinal))
            {
                return null;
            }

            return ReadShortQuick(dto);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    private static QuickUnlockState? ReadDeviceQuick(QuickUnlockDto dto)
    {
        byte[] masterKey;
        try
        {
            masterKey = Convert.FromBase64String(dto.DeviceMasterKey);
        }
        catch (FormatException)
        {
            return null;
        }

        if (masterKey.Length != Argon2Id.HashLength)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            return null;
        }

        return new QuickUnlockState { DeviceMasterKey = masterKey };
    }

    private static QuickUnlockState? ReadShortQuick(QuickUnlockDto dto)
    {
        if (dto.WrappedMasterKey is null
            || !string.Equals(dto.Algorithm, "argon2id", StringComparison.Ordinal)
            || !string.Equals(dto.WrappedMasterKey.Algorithm, "aes-256-gcm", StringComparison.Ordinal))
        {
            return null;
        }

        byte[] salt;
        byte[] nonce;
        byte[] tag;
        byte[] ciphertext;
        try
        {
            salt = Convert.FromBase64String(dto.Salt);
            nonce = Convert.FromBase64String(dto.WrappedMasterKey.Nonce);
            tag = Convert.FromBase64String(dto.WrappedMasterKey.Tag);
            ciphertext = Convert.FromBase64String(dto.WrappedMasterKey.Ciphertext);
        }
        catch (FormatException)
        {
            return null;
        }

        if (salt.Length != Argon2Id.SaltLength
            || nonce.Length != VaultCipher.NonceLength
            || tag.Length != VaultCipher.TagLength
            || ciphertext.Length != Argon2Id.HashLength)
        {
            return null;
        }

        KdfProfile profile;
        try
        {
            profile = new KdfProfile(dto.MemoryKb, dto.Iterations, dto.Parallelism);
            profile.Validate();
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }

        return new QuickUnlockState { Wrap = new ShortWrapMaterial(profile, salt, nonce, ciphertext, tag) };
    }

    private static void WriteQuickFile(string path, byte[] stored)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(stored);
            stream.Flush(true);
        }

        FileProtection.RestrictToCurrentUser(temp);
        File.Move(temp, path, overwrite: true);
        FileProtection.RestrictToCurrentUser(path);
    }

    private static string QuickUnlockPath(string vaultPath) => vaultPath + ".quick";

    private static byte[] ShortWrapAad() => Encoding.UTF8.GetBytes("keep-password/short-unlock/v1");

    private static readonly byte[] QuickEntropy = Encoding.UTF8.GetBytes("keep-password/quick-unlock/v1");

    private static CipherDto CipherFrom(ShortWrapMaterial wrap) => new()
    {
        Algorithm = "aes-256-gcm",
        Nonce = Convert.ToBase64String(wrap.Nonce),
        Tag = Convert.ToBase64String(wrap.Tag),
        Ciphertext = Convert.ToBase64String(wrap.Ciphertext)
    };

    private static byte[] ProtectQuick(byte[] json)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ProtectedData.Protect(json, QuickEntropy, DataProtectionScope.CurrentUser);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return json.ToArray();
    }

    private static byte[]? OpenQuick(byte[] stored)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return ProtectedData.Unprotect(stored, QuickEntropy, DataProtectionScope.CurrentUser);
            }
            catch (PlatformNotSupportedException)
            {
                return stored.ToArray();
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        return stored.ToArray();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (List<VaultEntry> Entries, VaultLabels Labels) DecryptPayload(string account, byte[] masterKey, CipherDto cipher)
    {
        if (!string.Equals(cipher.Algorithm, "aes-256-gcm", StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法识别的加密算法。");
        }

        var plaintext = VaultCipher.Decrypt(
            masterKey,
            Convert.FromBase64String(cipher.Nonce),
            Convert.FromBase64String(cipher.Ciphertext),
            Convert.FromBase64String(cipher.Tag),
            VaultCipher.AssociatedData(account));

        try
        {
            var payload = JsonSerializer.Deserialize<PayloadDto>(plaintext, PayloadOptions)
                ?? throw new InvalidDataException("保险库内容为空。");
            var entries = payload.Entries.Select(entry => entry.ToEntry()).ToList();
            VaultSession.DropDanglingKeyReferences(entries);
            return (entries, VaultLabels.From(payload.Spaces, payload.Tags, entries));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static EnvelopeDto ReadEnvelope(string path)
    {
        EnvelopeDto envelope;
        try
        {
            var json = File.ReadAllBytes(path);
            envelope = JsonSerializer.Deserialize<EnvelopeDto>(json, EnvelopeOptions)
                ?? throw new InvalidDataException("保险库文件无法读取。");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("保险库文件无法读取。", ex);
        }

        ValidateEnvelope(envelope);
        return envelope;
    }

    public VaultSession Recover(string path, IReadOnlyList<string> answers, string newMasterPassword)
    {
        if (answers is null || answers.Count != 3)
        {
            throw new ArgumentException("请回答三个安全问题。", nameof(answers));
        }

        if (string.IsNullOrEmpty(newMasterPassword))
        {
            throw new ArgumentException("请填写新的主密码。", nameof(newMasterPassword));
        }

        var envelope = ReadEnvelope(path);
        var recovery = envelope.Recovery ?? throw new InvalidOperationException("此保险库没有启用安全问题恢复。");
        var material = recovery.ToMaterial();
        var recoveryKey = Argon2Id.Derive(RecoverySecrets.Secret(answers), material.Profile, material.Salt);
        byte[]? oldMaster = null;
        var handedOff = false;
        try
        {
            oldMaster = Unwrap(recoveryKey, recovery.WrappedMasterKey, MasterWrapAad(material.Questions));
            var (entries, labels) = DecryptPayload(envelope.Account, oldMaster, envelope.Cipher!);
            var kdf = new KdfMaterial(material.Profile, RandomNumberGenerator.GetBytes(Argon2Id.SaltLength));
            var newMaster = Argon2Id.Derive(newMasterPassword, kdf.Profile, kdf.Salt);
            var secrets = new RecoverySecrets
            {
                Profile = material.Profile,
                Salt = material.Salt,
                Questions = material.Questions,
                Key = recoveryKey
            };
            try
            {
                WriteFile(path, envelope.Account, kdf, envelope.ShortKey!.ToMaterial(), newMaster, entries, secrets, labels);
                DeleteQuickUnlock(path);
                handedOff = true;
                return new VaultSession(path, envelope.Account, kdf, envelope.ShortKey.ToMaterial(), newMaster, entries, secrets, labels);
            }
            catch
            {
                CryptographicOperations.ZeroMemory(newMaster);
                throw;
            }
        }
        catch (CryptographicException)
        {
            throw new RecoveryFailedException();
        }
        finally
        {
            if (oldMaster is not null)
            {
                CryptographicOperations.ZeroMemory(oldMaster);
            }

            if (!handedOff)
            {
                CryptographicOperations.ZeroMemory(recoveryKey);
            }
        }
    }

    private static void RequireSecrets(string account, string masterPassword, string shortKey)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            throw new ArgumentException("请填写账号。", nameof(account));
        }

        if (string.IsNullOrEmpty(masterPassword))
        {
            throw new ArgumentException("请填写主密码。", nameof(masterPassword));
        }

        if (string.IsNullOrEmpty(shortKey))
        {
            throw new ArgumentException("请填写短密钥。", nameof(shortKey));
        }
    }

    private static RecoverySecrets? UnwrapRecovery(RecoveryDto? recovery, byte[] masterKey)
    {
        if (recovery is null)
        {
            return null;
        }

        var material = recovery.ToMaterial();
        var recoveryKey = Unwrap(masterKey, recovery.WrappedRecoveryKey, RecoveryKeyAad());
        return new RecoverySecrets
        {
            Profile = material.Profile,
            Salt = material.Salt,
            Questions = material.Questions,
            Key = recoveryKey
        };
    }

    private static byte[] Unwrap(byte[] key, CipherDto? cipher, byte[] associatedData)
    {
        if (cipher is null)
        {
            throw new InvalidDataException("保险库恢复数据已损坏。");
        }

        return VaultCipher.Decrypt(
            key,
            Convert.FromBase64String(cipher.Nonce),
            Convert.FromBase64String(cipher.Ciphertext),
            Convert.FromBase64String(cipher.Tag),
            associatedData);
    }

    private static byte[] MasterWrapAad(IReadOnlyList<string> questions) =>
        Encoding.UTF8.GetBytes("keep-password/recovery-master/v1\n" + string.Join('\n', questions));

    private static byte[] RecoveryKeyAad() =>
        Encoding.UTF8.GetBytes("keep-password/recovery-key/v1");

    private static void ValidateEnvelope(EnvelopeDto envelope)
    {
        if (envelope.Version != FormatVersion
            || !string.Equals(envelope.Format, "keep-password-vault", StringComparison.Ordinal)
            || envelope.Kdf is null
            || envelope.ShortKey is null
            || envelope.Cipher is null)
        {
            throw new InvalidDataException("保险库文件版本不受支持。");
        }

        if (string.IsNullOrWhiteSpace(envelope.Account) || envelope.Account.Length > 256)
        {
            throw new InvalidDataException("保险库文件无法读取。");
        }

        ValidateKdf(envelope.Kdf.Algorithm, envelope.Kdf.MemoryKb, envelope.Kdf.Iterations, envelope.Kdf.Parallelism, envelope.Kdf.Salt, hash: null);
        ValidateKdf(
            envelope.ShortKey.Algorithm,
            envelope.ShortKey.MemoryKb,
            envelope.ShortKey.Iterations,
            envelope.ShortKey.Parallelism,
            envelope.ShortKey.Salt,
            envelope.ShortKey.Hash);
        if (!string.Equals(envelope.Cipher.Algorithm, "aes-256-gcm", StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法识别的加密算法。");
        }

        RequireDecodedLength(envelope.Cipher.Nonce, VaultCipher.NonceLength);
        RequireDecodedLength(envelope.Cipher.Tag, VaultCipher.TagLength);
        var ciphertext = Convert.FromBase64String(envelope.Cipher.Ciphertext);
        if (ciphertext.Length is 0 or > 16 * 1024 * 1024)
        {
            throw new InvalidDataException("保险库文件无法读取。");
        }

        if (envelope.Recovery is not null)
        {
            ValidateRecovery(envelope.Recovery);
        }
    }

    private static void ValidateRecovery(RecoveryDto recovery)
    {
        if (!recovery.RiskAcknowledged || recovery.Questions.Count != 3 || recovery.Questions.Any(question => string.IsNullOrWhiteSpace(question)))
        {
            throw new InvalidDataException("保险库恢复数据已损坏。");
        }

        ValidateKdf(recovery.Algorithm, recovery.MemoryKb, recovery.Iterations, recovery.Parallelism, recovery.Salt, hash: null);
        RequireDecodedLength(recovery.WrappedMasterKey?.Nonce, VaultCipher.NonceLength);
        RequireDecodedLength(recovery.WrappedMasterKey?.Tag, VaultCipher.TagLength);
        RequireDecodedLength(recovery.WrappedRecoveryKey?.Nonce, VaultCipher.NonceLength);
        RequireDecodedLength(recovery.WrappedRecoveryKey?.Tag, VaultCipher.TagLength);
        if (Convert.FromBase64String(recovery.WrappedMasterKey!.Ciphertext).Length != Argon2Id.HashLength
            || Convert.FromBase64String(recovery.WrappedRecoveryKey!.Ciphertext).Length != Argon2Id.HashLength)
        {
            throw new InvalidDataException("保险库恢复数据已损坏。");
        }
    }

    private static void ValidateKdf(string? algorithm, int memoryKb, int iterations, int parallelism, string? salt, string? hash)
    {
        if (!string.Equals(algorithm, "argon2id", StringComparison.Ordinal))
        {
            throw new InvalidDataException("无法识别的密钥派生算法。");
        }

        try
        {
            new KdfProfile(memoryKb, iterations, parallelism).Validate();
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("保险库密钥参数不受支持。", ex);
        }

        RequireDecodedLength(salt, Argon2Id.SaltLength);
        if (hash is not null)
        {
            RequireDecodedLength(hash, Argon2Id.HashLength);
        }
    }

    private static void RequireDecodedLength(string? text, int expected)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text ?? "");
        }
        catch (FormatException ex)
        {
            throw new InvalidDataException("保险库文件无法读取。", ex);
        }

        if (bytes.Length != expected)
        {
            throw new InvalidDataException("保险库文件无法读取。");
        }
    }

    internal sealed record KdfMaterial(KdfProfile Profile, byte[] Salt);

    internal sealed record ShortKeyMaterial(KdfProfile Profile, byte[] Salt, byte[] Hash);

    internal sealed record ShortWrapMaterial(KdfProfile Profile, byte[] Salt, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

    internal sealed class QuickUnlockState
    {
        public ShortWrapMaterial? Wrap { get; init; }

        public byte[]? DeviceMasterKey { get; init; }

        public bool IsDevice => DeviceMasterKey is not null;

        public void Zero()
        {
            if (DeviceMasterKey is not null)
            {
                CryptographicOperations.ZeroMemory(DeviceMasterKey);
            }
        }
    }

    private sealed class EnvelopeDto
    {
        public int Version { get; set; }

        public string Format { get; set; } = "";

        public string Account { get; set; } = "";

        public KdfDto? Kdf { get; set; }

        public ShortKeyDto? ShortKey { get; set; }

        public RecoveryDto? Recovery { get; set; }

        public CipherDto? Cipher { get; set; }
    }

    private sealed class RecoveryDto
    {
        public bool RiskAcknowledged { get; set; }

        public List<string> Questions { get; set; } = [];

        public string Algorithm { get; set; } = "argon2id";

        public string Salt { get; set; } = "";

        public int MemoryKb { get; set; }

        public int Iterations { get; set; }

        public int Parallelism { get; set; }

        public CipherDto? WrappedMasterKey { get; set; }

        public CipherDto? WrappedRecoveryKey { get; set; }

        public static RecoveryDto From(RecoverySecrets secrets, byte[] masterKey)
        {
            var master = VaultCipher.Encrypt(secrets.Key, masterKey, MasterWrapAad(secrets.Questions));
            var wrappedKey = VaultCipher.Encrypt(masterKey, secrets.Key, RecoveryKeyAad());
            return new RecoveryDto
            {
                RiskAcknowledged = true,
                Questions = secrets.Questions.ToList(),
                Salt = Convert.ToBase64String(secrets.Salt),
                MemoryKb = secrets.Profile.MemoryKb,
                Iterations = secrets.Profile.Iterations,
                Parallelism = secrets.Profile.Parallelism,
                WrappedMasterKey = CipherDto.From(master),
                WrappedRecoveryKey = CipherDto.From(wrappedKey)
            };
        }

        public (KdfProfile Profile, byte[] Salt, string[] Questions) ToMaterial() => (
            new KdfProfile(MemoryKb, Iterations, Parallelism),
            Convert.FromBase64String(Salt),
            Questions.ToArray());
    }

    private sealed class KdfDto
    {
        public string Algorithm { get; set; } = "argon2id";

        public string Salt { get; set; } = "";

        public int MemoryKb { get; set; }

        public int Iterations { get; set; }

        public int Parallelism { get; set; }

        public static KdfDto From(KdfMaterial material) => new()
        {
            Salt = Convert.ToBase64String(material.Salt),
            MemoryKb = material.Profile.MemoryKb,
            Iterations = material.Profile.Iterations,
            Parallelism = material.Profile.Parallelism
        };

        public KdfMaterial ToMaterial() => new(
            new KdfProfile(MemoryKb, Iterations, Parallelism),
            Convert.FromBase64String(Salt));
    }

    private sealed class ShortKeyDto
    {
        public string Algorithm { get; set; } = "argon2id";

        public string Salt { get; set; } = "";

        public string Hash { get; set; } = "";

        public int MemoryKb { get; set; }

        public int Iterations { get; set; }

        public int Parallelism { get; set; }

        public static ShortKeyDto From(ShortKeyMaterial material) => new()
        {
            Salt = Convert.ToBase64String(material.Salt),
            Hash = Convert.ToBase64String(material.Hash),
            MemoryKb = material.Profile.MemoryKb,
            Iterations = material.Profile.Iterations,
            Parallelism = material.Profile.Parallelism
        };

        public ShortKeyMaterial ToMaterial() => new(
            new KdfProfile(MemoryKb, Iterations, Parallelism),
            Convert.FromBase64String(Salt),
            Convert.FromBase64String(Hash));
    }

    private sealed class QuickUnlockDto
    {
        public int Version { get; set; }

        public string Mode { get; set; } = "";

        public string VaultSalt { get; set; } = "";

        public string DeviceMasterKey { get; set; } = "";

        public string Algorithm { get; set; } = "argon2id";

        public string Salt { get; set; } = "";

        public int MemoryKb { get; set; }

        public int Iterations { get; set; }

        public int Parallelism { get; set; }

        public CipherDto? WrappedMasterKey { get; set; }
    }

    private sealed class CipherDto
    {
        public string Algorithm { get; set; } = "aes-256-gcm";

        public string Nonce { get; set; } = "";

        public string Tag { get; set; } = "";

        public string Ciphertext { get; set; } = "";

        public static CipherDto From((byte[] Nonce, byte[] Ciphertext, byte[] Tag) material) => new()
        {
            Nonce = Convert.ToBase64String(material.Nonce),
            Tag = Convert.ToBase64String(material.Tag),
            Ciphertext = Convert.ToBase64String(material.Ciphertext)
        };
    }

    private sealed class PayloadDto
    {
        public List<string>? Spaces { get; set; }

        public List<string>? Tags { get; set; }

        public List<EntryDto> Entries { get; set; } = [];
    }

    private sealed class EntryDto
    {
        public Guid Id { get; set; }

        public string Name { get; set; } = "";

        public string Url { get; set; } = "";

        public string? Username { get; set; }

        public string? Password { get; set; }

        public string Note { get; set; } = "";

        public string? TotpSecret { get; set; }

        public List<AccountDto>? Accounts { get; set; }

        public string? Space { get; set; }

        public List<string>? Tags { get; set; }

        public string? Kind { get; set; }

        public bool? Favorite { get; set; }

        public Dictionary<string, string>? Fields { get; set; }

        public List<CustomFieldDto>? CustomFields { get; set; }

        public Guid? SshKeyId { get; set; }

        public List<NodeDto>? Nodes { get; set; }

        public static EntryDto From(VaultEntry entry) => new()
        {
            Id = entry.Id,
            Kind = entry.Kind == VaultItemKind.Login ? null : VaultItemKinds.Code(entry.Kind),
            Favorite = entry.Favorite ? true : null,
            Name = entry.Name ?? "",
            Url = entry.Url ?? "",
            Note = entry.Note ?? "",
            Accounts = entry.Accounts.Count == 0
                ? null
                : entry.Accounts.Select(account => new AccountDto
                {
                    Id = account.Id,
                    Username = account.Username ?? "",
                    Password = account.Password ?? "",
                    Note = string.IsNullOrEmpty(account.Note) ? null : account.Note,
                    TotpSecret = string.IsNullOrWhiteSpace(account.TotpSecret) ? null : account.TotpSecret.Trim(),
                    Auth = account.Auth == FieldKeys.AuthSshKey ? FieldKeys.AuthSshKey : null,
                    SshKeyId = account.SshKeyId
                }).ToList(),
            Space = string.IsNullOrEmpty(entry.Space) ? null : entry.Space,
            Tags = entry.Tags.Count == 0 ? null : entry.Tags.ToList(),
            Fields = entry.Fields.Count == 0 ? null : new Dictionary<string, string>(entry.Fields, StringComparer.Ordinal),
            CustomFields = entry.CustomFields.Count == 0
                ? null
                : entry.CustomFields.Select(field => new CustomFieldDto { Name = field.Name, Value = field.Value, Sensitive = field.Sensitive }).ToList(),
            SshKeyId = entry.SshKeyId,
            Nodes = entry.Nodes.Count == 0
                ? null
                : entry.Nodes.Select(node => new NodeDto { Name = node.Name, Role = node.Role, Host = node.Host, Port = node.Port, Service = node.Service }).ToList()
        };

        public VaultEntry ToEntry()
        {
            var entry = Build();
            ItemTemplates.MigrateLegacyFields(entry);
            MigrateLegacyCredentials(entry);
            ItemTemplates.AdoptLegacyKeyAuth(entry);
            return entry;
        }

        private void MigrateLegacyCredentials(VaultEntry entry)
        {
            if (Accounts is not null)
            {
                return;
            }

            var username = Username ?? "";
            var password = Password ?? "";
            var totp = string.IsNullOrWhiteSpace(TotpSecret) ? null : TotpSecret.Trim();
            if (entry.Kind == VaultItemKind.ApiCredential)
            {
                if (entry.Field(FieldKeys.ClientId).Length == 0)
                {
                    entry.SetField(FieldKeys.ClientId, username);
                }

                return;
            }

            if (VaultItemKinds.HasAccounts(entry.Kind) && (username.Length > 0 || password.Length > 0 || totp is not null))
            {
                entry.Accounts.Add(new VaultAccount
                {
                    Username = username,
                    Password = password,
                    TotpSecret = entry.Kind == VaultItemKind.Login ? totp : null
                });
            }
        }

        private VaultEntry Build() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Kind = VaultItemKinds.Parse(Kind),
            Favorite = Favorite == true,
            Name = Name ?? "",
            Url = Url ?? "",
            Note = Note ?? "",
            Accounts = Accounts?.Select(account => new VaultAccount
            {
                Id = account.Id == Guid.Empty ? Guid.NewGuid() : account.Id,
                Username = account.Username ?? "",
                Password = account.Password ?? "",
                Note = account.Note ?? "",
                TotpSecret = string.IsNullOrWhiteSpace(account.TotpSecret) ? null : account.TotpSecret.Trim(),
                Auth = account.Auth == FieldKeys.AuthSshKey ? FieldKeys.AuthSshKey : "",
                SshKeyId = account.SshKeyId
            }).ToList() ?? [],
            Space = LabelName.Normalize(Space),
            Tags = LabelName.NormalizeAll(Tags),
            Fields = Fields is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : Fields.Where(pair => !string.IsNullOrEmpty(pair.Key) && !string.IsNullOrEmpty(pair.Value))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            CustomFields = CustomFields?.Select(field => new VaultCustomField
            {
                Name = field.Name ?? "",
                Value = field.Value ?? "",
                Sensitive = field.Sensitive
            }).ToList() ?? [],
            SshKeyId = SshKeyId,
            Nodes = Nodes?.Select(node => new ClusterNode
            {
                Name = node.Name ?? "",
                Role = node.Role ?? "",
                Host = node.Host ?? "",
                Port = node.Port ?? "",
                Service = node.Service ?? ""
            }).ToList() ?? []
        };
    }

    private sealed class AccountDto
    {
        public Guid Id { get; set; }

        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public string? Note { get; set; }

        public string? TotpSecret { get; set; }

        public string? Auth { get; set; }

        public Guid? SshKeyId { get; set; }
    }

    private sealed class CustomFieldDto
    {
        public string Name { get; set; } = "";

        public string Value { get; set; } = "";

        public bool Sensitive { get; set; }
    }

    private sealed class NodeDto
    {
        public string Name { get; set; } = "";

        public string Role { get; set; } = "";

        public string Host { get; set; } = "";

        public string Port { get; set; } = "";

        public string Service { get; set; } = "";
    }
}
