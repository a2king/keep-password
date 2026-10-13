using System.Security.Cryptography;

namespace KeepPassword.Core.Totp;

public static class TotpGenerator
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    public static string Generate(
        ReadOnlySpan<byte> key,
        long unixSeconds,
        int digits = Digits,
        int periodSeconds = PeriodSeconds,
        TotpAlgorithm algorithm = TotpAlgorithm.Sha1)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException("验证码密钥为空。", nameof(key));
        }

        if (digits is < 6 or > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(digits));
        }

        if (periodSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        }

        var counter = unixSeconds / periodSeconds;
        Span<byte> counterBytes = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);
        var hash = algorithm switch
        {
            TotpAlgorithm.Sha256 => HMACSHA256.HashData(key, counterBytes),
            TotpAlgorithm.Sha512 => HMACSHA512.HashData(key, counterBytes),
            _ => HMACSHA1.HashData(key, counterBytes)
        };
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24)
            | (hash[offset + 1] << 16)
            | (hash[offset + 2] << 8)
            | hash[offset + 3];
        var modulus = 1;
        for (var i = 0; i < digits; i++)
        {
            modulus *= 10;
        }

        return (binary % modulus).ToString(new string('0', digits));
    }

    public static string GenerateFromBase32(string secret, long unixSeconds) =>
        Generate(Base32Encoding.Decode(secret), unixSeconds);

    /// <summary>
    /// 保存的密钥可以是纯 Base32（6 位 / 30 秒 / SHA1），也可以是携带参数的 otpauth:// 链接。
    /// </summary>
    public static bool TryParseSecret(string? secret, out TotpSettings settings)
    {
        settings = new TotpSettings("");
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        if (OtpAuthUri.IsOtpAuth(secret))
        {
            return OtpAuthUri.TryParse(secret, out settings);
        }

        try
        {
            if (Base32Encoding.Decode(secret).Length == 0)
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }

        settings = new TotpSettings(secret.Trim());
        return true;
    }

    public static bool TryGenerate(string? secret, long unixSeconds, out string code, out int remainingSeconds)
    {
        code = "";
        remainingSeconds = 0;
        if (!TryParseSecret(secret, out var settings))
        {
            return false;
        }

        var key = Base32Encoding.Decode(settings.Secret);
        try
        {
            code = Generate(key, unixSeconds, settings.Digits, settings.PeriodSeconds, settings.Algorithm);
            remainingSeconds = RemainingSeconds(unixSeconds, settings.PeriodSeconds);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static int RemainingSeconds(long unixSeconds, int periodSeconds = PeriodSeconds)
    {
        if (periodSeconds < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(periodSeconds));
        }

        var elapsed = (int)(unixSeconds % periodSeconds);
        return elapsed == 0 ? periodSeconds : periodSeconds - elapsed;
    }
}
