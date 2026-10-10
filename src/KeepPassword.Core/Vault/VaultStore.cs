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
        RequireSecrets(account, masterPassword, shortKey);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("保险库已存在。");
        }

        var trimmed = account.Trim();
        var kdf = new KdfMaterial(Profile, RandomNumberGenerator.GetBytes(Argon2Id.SaltLength));
        var masterKey = Argon2Id.Derive(masterPassword, kdf.Profile, kdf.Salt);
        RecoverySecrets? secrets = null;
        try
        {
            secrets = recovery is null ? null : RecoverySecrets.Create(recovery, Profile);
            var shortKeyMaterial = HashShortKey(shortKey, Profile);
            WriteFile(path, trimmed, kdf, shortKeyMaterial, masterKey, [], secrets);
        }
        finally
        {
            secrets?.Zero();
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public bool HasRecovery(string path) => ReadEnvelope(path).Recovery is not null;

    public IReadOnlyList<string> RecoveryQuestions(string path)
    {
        var recovery = ReadEnvelope(path).Recovery ?? throw new InvalidOperationException("此保险库没有启用安全问题恢复。");
        return recovery.Questions;
    }

    public string ReadAccount(string path) => ReadEnvelope(path).Account;

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

        return new VaultSession(path, envelope.Account, kdf, shortKeyDto.ToMaterial(), masterKey, entries, recovery, labels);
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

        public string Username { get; set; } = "";

        public string Password { get; set; } = "";

        public string Note { get; set; } = "";

        public string? TotpSecret { get; set; }

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
            Username = entry.Username ?? "",
            Password = entry.Password ?? "",
            Note = entry.Note ?? "",
            TotpSecret = string.IsNullOrWhiteSpace(entry.TotpSecret) ? null : entry.TotpSecret.Trim(),
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
            return entry;
        }

        private VaultEntry Build() => new()
        {
            Id = Id == Guid.Empty ? Guid.NewGuid() : Id,
            Kind = VaultItemKinds.Parse(Kind),
            Favorite = Favorite == true,
            Name = Name ?? "",
            Url = Url ?? "",
            Username = Username ?? "",
            Password = Password ?? "",
            Note = Note ?? "",
            TotpSecret = string.IsNullOrWhiteSpace(TotpSecret) ? null : TotpSecret.Trim(),
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
