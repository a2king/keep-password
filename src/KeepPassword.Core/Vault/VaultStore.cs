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
        WriteIndented = true
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

    public void Create(string path, string account, string masterPassword, string shortKey)
    {
        RequireSecrets(account, masterPassword, shortKey);
        if (File.Exists(path))
        {
            throw new InvalidOperationException("保险库已存在。");
        }

        var trimmed = account.Trim();
        var kdf = new KdfMaterial(Profile, RandomNumberGenerator.GetBytes(Argon2Id.SaltLength));
        var masterKey = Argon2Id.Derive(masterPassword, kdf.Profile, kdf.Salt);
        try
        {
            var shortKeyMaterial = HashShortKey(shortKey, Profile);
            WriteFile(path, trimmed, kdf, shortKeyMaterial, masterKey, []);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(masterKey);
        }
    }

    public VaultSession Unlock(string path, string account, string masterPassword, string shortKey)
    {
        RequireSecrets(account, masterPassword, shortKey);
        var envelope = ReadEnvelope(path);
        if (!string.Equals(envelope.Account, account.Trim(), StringComparison.Ordinal))
        {
            throw new UnlockFailedException();
        }

        var shortKeyDto = envelope.ShortKey ?? throw new InvalidDataException("保险库文件无法读取。");
        var kdfDto = envelope.Kdf ?? throw new InvalidDataException("保险库文件无法读取。");
        var cipher = envelope.Cipher ?? throw new InvalidDataException("保险库文件无法读取。");
        var shortKeyMaterial = shortKeyDto.ToMaterial();
        if (!Argon2Id.Verify(shortKey, shortKeyMaterial.Profile, shortKeyMaterial.Salt, shortKeyMaterial.Hash))
        {
            throw new UnlockFailedException();
        }

        var kdf = kdfDto.ToMaterial();
        var masterKey = Argon2Id.Derive(masterPassword, kdf.Profile, kdf.Salt);
        try
        {
            var entries = DecryptEntries(envelope.Account, masterKey, cipher);
            return new VaultSession(path, envelope.Account, kdf, shortKeyMaterial, masterKey, entries);
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(masterKey);
            throw new UnlockFailedException();
        }
    }

    internal static void WriteFile(
        string path,
        string account,
        KdfMaterial kdf,
        ShortKeyMaterial shortKey,
        byte[] masterKey,
        IReadOnlyList<VaultEntry> entries)
    {
        var payload = new PayloadDto
        {
            Entries = entries.Select(entry => new EntryDto
            {
                Id = entry.Id,
                Name = entry.Name ?? "",
                Url = entry.Url ?? "",
                Username = entry.Username ?? "",
                Password = entry.Password ?? "",
                Note = entry.Note ?? "",
                TotpSecret = string.IsNullOrWhiteSpace(entry.TotpSecret) ? null : entry.TotpSecret.Trim()
            }).ToList()
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
            File.WriteAllBytes(temp, json);
            File.Move(temp, path, overwrite: true);
            TryRestrictPermissions(path);
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

    private static List<VaultEntry> DecryptEntries(string account, byte[] masterKey, CipherDto cipher)
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
            return payload.Entries.Select(entry => new VaultEntry
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                Name = entry.Name ?? "",
                Url = entry.Url ?? "",
                Username = entry.Username ?? "",
                Password = entry.Password ?? "",
                Note = entry.Note ?? "",
                TotpSecret = string.IsNullOrWhiteSpace(entry.TotpSecret) ? null : entry.TotpSecret.Trim()
            }).ToList();
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

        if (envelope.Version != FormatVersion || envelope.Kdf is null || envelope.ShortKey is null || envelope.Cipher is null)
        {
            throw new InvalidDataException("保险库文件版本不受支持。");
        }

        return envelope;
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

    private static void TryRestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (PlatformNotSupportedException)
        {
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

        public CipherDto? Cipher { get; set; }
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
    }

    private sealed class PayloadDto
    {
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
    }
}
