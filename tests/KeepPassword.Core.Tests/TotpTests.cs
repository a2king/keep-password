using KeepPassword.Core.Totp;

namespace KeepPassword.Core.Tests;

public class TotpTests
{
    // RFC 6238 附录 B，种子 ASCII "12345678901234567890"，SHA-1。
    // 文档中的 8 位结果对 10^6 取模即为 6 位口令。
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void Generate_MatchesRfc6238Sha1Vectors(long unixSeconds, string expected)
    {
        var key = Base32Encoding.Decode(Secret);
        Assert.Equal("12345678901234567890"u8.ToArray(), key);
        Assert.Equal(expected, TotpGenerator.Generate(key, unixSeconds));
        Assert.Equal(expected, TotpGenerator.GenerateFromBase32(Secret, unixSeconds));
    }

    [Theory]
    [InlineData(59, 1)]
    [InlineData(60, 30)]
    [InlineData(61, 29)]
    public void RemainingSeconds_FollowsThirtySecondWindow(long unixSeconds, int expected)
    {
        Assert.Equal(expected, TotpGenerator.RemainingSeconds(unixSeconds));
    }
}
