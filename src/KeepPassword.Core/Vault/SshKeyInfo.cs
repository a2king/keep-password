using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Utilities;
using Org.BouncyCastle.OpenSsl;

namespace KeepPassword.Core.Vault;

public sealed record SshKeyDetails(string KeyType, string Fingerprint);

public sealed record SshKeyInspection(SshKeyDetails? Details, string? Warning);

public static class SshKeyInfo
{
    private const string OpenSshHeader = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private const string OpenSshFooter = "-----END OPENSSH PRIVATE KEY-----";
    private static readonly byte[] OpenSshMagic = Encoding.ASCII.GetBytes("openssh-key-v1\0");

    public static SshKeyInspection Inspect(string? privateKey, string? publicKey)
    {
        var privateText = privateKey?.Trim() ?? "";
        var publicText = publicKey?.Trim() ?? "";
        var fromPrivate = privateText.Length == 0 ? null : FromPrivateKey(privateText);
        var fromPublic = publicText.Length == 0 ? null : FromPublicKey(publicText);
        if (fromPrivate is not null && fromPublic is not null && fromPrivate.Fingerprint != fromPublic.Fingerprint)
        {
            return new SshKeyInspection(fromPrivate, "公钥与私钥不匹配，请确认粘贴的是同一对密钥。");
        }

        if (publicText.Length > 0 && fromPublic is null)
        {
            return new SshKeyInspection(fromPrivate, "公钥格式无法识别。");
        }

        var details = fromPrivate ?? fromPublic;
        if (details is null && privateText.Length > 0)
        {
            var warning = IsEncryptedPem(privateText)
                ? "私钥已加密，无法直接读取；粘贴对应公钥后即可生成指纹。"
                : "无法识别私钥格式，仍会加密保存，但无法生成指纹。";
            return new SshKeyInspection(null, warning);
        }

        return new SshKeyInspection(details, null);
    }

    public static SshKeyDetails? FromPublicKey(string text)
    {
        var line = text.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (!part.StartsWith("AAAA", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                return FromBlob(Convert.FromBase64String(part));
            }
            catch (FormatException)
            {
                return null;
            }
        }

        return null;
    }

    public static SshKeyDetails? FromPrivateKey(string text)
    {
        try
        {
            if (text.Contains(OpenSshHeader, StringComparison.Ordinal))
            {
                return FromBlob(OpenSshPublicBlob(text));
            }

            if (!text.Contains("PRIVATE KEY-----", StringComparison.Ordinal) || IsEncryptedPem(text))
            {
                return null;
            }

            using var reader = new StringReader(text);
            var parsed = new PemReader(reader).ReadObject();
            var publicKey = parsed switch
            {
                AsymmetricCipherKeyPair pair => pair.Public,
                RsaPrivateCrtKeyParameters rsa => new RsaKeyParameters(false, rsa.Modulus, rsa.PublicExponent),
                Ed25519PrivateKeyParameters ed => ed.GeneratePublicKey(),
                ECPrivateKeyParameters ec => new ECPublicKeyParameters("EC", ec.Parameters.G.Multiply(ec.D).Normalize(), ec.Parameters),
                _ => null
            };
            return publicKey is null ? null : FromBlob(OpenSshPublicKeyUtilities.EncodePublicKey(publicKey));
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException or ArgumentException or InvalidOperationException or InvalidCastException or PasswordException or Org.BouncyCastle.Crypto.CryptoException)
        {
            return null;
        }
    }

    private static bool IsEncryptedPem(string text) =>
        text.Contains("ENCRYPTED", StringComparison.Ordinal) && !text.Contains(OpenSshHeader, StringComparison.Ordinal);

    private static byte[] OpenSshPublicBlob(string text)
    {
        var start = text.IndexOf(OpenSshHeader, StringComparison.Ordinal) + OpenSshHeader.Length;
        var end = text.IndexOf(OpenSshFooter, start, StringComparison.Ordinal);
        if (end < 0)
        {
            throw new FormatException("私钥结尾缺失。");
        }

        var body = new string(text[start..end].Where(c => !char.IsWhiteSpace(c)).ToArray());
        var bytes = Convert.FromBase64String(body);
        if (bytes.Length < OpenSshMagic.Length || !bytes.AsSpan(0, OpenSshMagic.Length).SequenceEqual(OpenSshMagic))
        {
            throw new FormatException("不是 OpenSSH 私钥。");
        }

        var offset = OpenSshMagic.Length;
        ReadString(bytes, ref offset);
        ReadString(bytes, ref offset);
        ReadString(bytes, ref offset);
        if (ReadUInt32(bytes, ref offset) < 1)
        {
            throw new FormatException("私钥中没有密钥。");
        }

        return ReadString(bytes, ref offset);
    }

    private static SshKeyDetails? FromBlob(byte[] blob)
    {
        var offset = 0;
        var algorithm = Encoding.ASCII.GetString(ReadString(blob, ref offset));
        var type = algorithm switch
        {
            "ssh-ed25519" => "ED25519",
            "ecdsa-sha2-nistp256" => "ECDSA P-256",
            "ecdsa-sha2-nistp384" => "ECDSA P-384",
            "ecdsa-sha2-nistp521" => "ECDSA P-521",
            "ssh-rsa" => RsaType(blob),
            _ => null
        };
        if (type is null)
        {
            return null;
        }

        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        return new SshKeyDetails(type, fingerprint);
    }

    private static string RsaType(byte[] blob)
    {
        try
        {
            return OpenSshPublicKeyUtilities.ParsePublicKey(blob) is RsaKeyParameters rsa ? $"RSA {rsa.Modulus.BitLength}" : "RSA";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return "RSA";
        }
    }

    private static uint ReadUInt32(byte[] data, ref int offset)
    {
        if (offset + 4 > data.Length)
        {
            throw new FormatException("密钥数据不完整。");
        }

        var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static byte[] ReadString(byte[] data, ref int offset)
    {
        var length = ReadUInt32(data, ref offset);
        if (length > data.Length - offset)
        {
            throw new FormatException("密钥数据不完整。");
        }

        var value = data.AsSpan(offset, (int)length).ToArray();
        offset += (int)length;
        return value;
    }
}
