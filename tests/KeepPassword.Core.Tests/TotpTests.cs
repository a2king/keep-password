using System.Text;
using KeepPassword.Core.Import;
using KeepPassword.Core.Totp;
using ZXing;
using ZXing.QrCode;

namespace KeepPassword.Core.Tests;

public class TotpTests
{
    // RFC 6238 附录 B，种子 ASCII "12345678901234567890"，SHA-1。
    // 文档中的 8 位结果对 10^6 取模即为 6 位口令。
    private const string Secret = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
    private const string Seed256 = "12345678901234567890123456789012";
    private const string Seed512 = "1234567890123456789012345678901234567890123456789012345678901234";

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
    [InlineData(59, TotpAlgorithm.Sha256, "46119246")]
    [InlineData(1111111109, TotpAlgorithm.Sha256, "68084774")]
    [InlineData(20000000000, TotpAlgorithm.Sha256, "77737706")]
    [InlineData(59, TotpAlgorithm.Sha512, "90693936")]
    [InlineData(1111111109, TotpAlgorithm.Sha512, "25091201")]
    [InlineData(20000000000, TotpAlgorithm.Sha512, "47863826")]
    public void Generate_MatchesRfc6238Sha2Vectors(long unixSeconds, TotpAlgorithm algorithm, string expected)
    {
        var seed = algorithm == TotpAlgorithm.Sha256 ? Seed256 : Seed512;
        Assert.Equal(expected, TotpGenerator.Generate(Encoding.ASCII.GetBytes(seed), unixSeconds, 8, 30, algorithm));

        var uri = $"otpauth://totp/Test?secret={Base32(seed)}&digits=8&algorithm={algorithm.ToString().ToUpperInvariant()}";
        Assert.True(TotpGenerator.TryGenerate(uri, unixSeconds, out var code, out _));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData(59, 1)]
    [InlineData(60, 30)]
    [InlineData(61, 29)]
    public void RemainingSeconds_FollowsThirtySecondWindow(long unixSeconds, int expected)
    {
        Assert.Equal(expected, TotpGenerator.RemainingSeconds(unixSeconds));
    }

    [Fact]
    public void TryGenerate_AcceptsPlainBase32AndUsesCustomPeriod()
    {
        Assert.True(TotpGenerator.TryGenerate(Secret.ToLowerInvariant(), 59, out var code, out var remaining));
        Assert.Equal("287082", code);
        Assert.Equal(1, remaining);

        Assert.True(TotpGenerator.TryGenerate($"otpauth://totp/A?secret={Secret}&period=60", 59, out code, out remaining));
        Assert.Equal(TotpGenerator.Generate(Base32Encoding.Decode(Secret), 59, 6, 60), code);
        Assert.Equal(1, remaining);

        Assert.False(TotpGenerator.TryGenerate("不是密钥", 59, out _, out _));
        Assert.False(TotpGenerator.TryGenerate("otpauth://hotp/A?secret=" + Secret, 59, out _, out _));
    }

    [Fact]
    public void OtpAuthUri_ParsesLabelIssuerAndParameters()
    {
        Assert.True(OtpAuthUri.TryParse(
            "otpauth://totp/ACME%20Co:ada%40example.com?secret=gezd+gnbv&issuer=ACME+Co&algorithm=sha256&digits=8&period=45",
            out var settings));
        Assert.Equal("GEZDGNBV", settings.Secret);
        Assert.Equal("ACME Co", settings.Issuer);
        Assert.Equal("ada@example.com", settings.Account);
        Assert.Equal(8, settings.Digits);
        Assert.Equal(45, settings.PeriodSeconds);
        Assert.Equal(TotpAlgorithm.Sha256, settings.Algorithm);
        Assert.False(settings.IsDefault);
        Assert.Equal("ACME Co · ada@example.com", settings.DisplayName);

        Assert.True(OtpAuthUri.TryParse("otpauth://totp/bob?secret=" + Secret, out settings));
        Assert.True(settings.IsDefault);
        Assert.Equal("", settings.Issuer);
        Assert.Equal("bob", settings.Account);

        Assert.False(OtpAuthUri.TryParse("otpauth://totp/x?issuer=A", out _));
        Assert.False(OtpAuthUri.TryParse("otpauth://totp/x?secret=" + Secret + "&digits=9", out _));
        Assert.False(OtpAuthUri.TryParse("otpauth://totp/x?secret=" + Secret + "&algorithm=MD5", out _));
        Assert.False(OtpAuthUri.TryParse("https://example.com/?secret=" + Secret, out _));
    }

    [Fact]
    public void CsvImport_KeepsLinkOnlyWhenParametersAreNotDefault()
    {
        var custom = $"otpauth://totp/AWS?secret={Secret}&digits=8";
        var imported = CsvImporter.Import($"""
            Title,Website,Username,Password,OTPAuth
            GitHub,https://github.com,ada,s3cret,otpauth://totp/GitHub?secret={Secret}&issuer=GitHub
            AWS,https://aws.amazon.com,root,s3cret,{custom}
            """);
        Assert.Equal(Secret, imported[0].Accounts[0].TotpSecret);
        Assert.Equal(custom, imported[1].Accounts[0].TotpSecret);
    }

    [Fact]
    public void QrCodeScanner_FindsEveryCodeOnCanvas()
    {
        const int width = 900, height = 500;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)0xF0);
        var first = $"otpauth://totp/GitHub:ada?secret={Secret}&issuer=GitHub";
        var second = $"otpauth://totp/AWS:root?secret={Base32(Seed256)}&digits=8";
        Draw(pixels, width, first, 60, 80);
        Draw(pixels, width, second, 520, 120);

        var texts = QrCodeScanner.Decode(pixels, width, height);

        Assert.Equal(2, texts.Count);
        Assert.Contains(first, texts);
        Assert.Contains(second, texts);
        Assert.Empty(QrCodeScanner.Decode(new byte[64 * 64 * 4], 64, 64));
    }

    private static void Draw(byte[] pixels, int width, string text, int left, int top)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 300, 300);
        for (var y = 0; y < matrix.Height; y++)
        {
            for (var x = 0; x < matrix.Width; x++)
            {
                var value = matrix[x, y] ? (byte)0 : (byte)0xFF;
                var p = ((top + y) * width + left + x) * 4;
                pixels[p] = pixels[p + 1] = pixels[p + 2] = value;
                pixels[p + 3] = 0xFF;
            }
        }
    }

    private static string Base32(string ascii)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in Encoding.ASCII.GetBytes(ascii))
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                output.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            output.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }
}
