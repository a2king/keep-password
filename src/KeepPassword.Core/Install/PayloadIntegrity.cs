using System.Security.Cryptography;

namespace KeepPassword.Core.Install;

public static class PayloadIntegrity
{
    public static string Sha256Hex(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var hash = SHA256.HashData(stream);
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

        return Convert.ToHexString(hash);
    }

    public static void Verify(Stream stream, string expectedHex)
    {
        var actual = Convert.FromHexString(Sha256Hex(stream));
        var expected = Convert.FromHexString(Normalize(expectedHex));
        if (!CryptographicOperations.FixedTimeEquals(actual, expected))
        {
            throw new InvalidDataException("安装包完整性校验失败。");
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }
    }

    private static string Normalize(string hex) =>
        hex.Trim().Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
}
