using System.Security.Cryptography;

namespace KeepPassword.Core.Totp;

public static class TotpGenerator
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    public static string Generate(ReadOnlySpan<byte> key, long unixSeconds, int digits = Digits, int periodSeconds = PeriodSeconds)
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
        var hash = HMACSHA1.HashData(key, counterBytes);
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

    public static bool TryGenerateFromBase32(string? secret, long unixSeconds, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(secret))
        {
            return false;
        }

        try
        {
            code = GenerateFromBase32(secret, unixSeconds);
            return true;
        }
        catch (FormatException)
        {
            return false;
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
