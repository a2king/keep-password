using System.Security.Cryptography;
using System.Text;

namespace KeepPassword.Core.Update;

/// <summary>
/// 更新包的离线签名。只靠"同一个 Release 里的 .sha256"并不安全：Release 或账号被入侵时，
/// 攻击者会把安装包和校验文件一起换掉，哈希照样对得上。
/// 这里改成：发布者用只存在于自己机器上的私钥，对"版本号 + 安装包 SHA-256"签名；
/// 程序里只内置公钥，下载后先验签再运行。私钥从不进入 GitHub，所以仅仅控制 Release 也无法伪造。
/// 算法用 .NET 自带的 ECDSA P-256 + SHA-256，不引入额外依赖。
/// </summary>
public static class UpdateSignature
{
    public const string AssetSuffix = ".sig";

    /// <summary>被签名的内容。带上版本号，防止把旧版本的合法签名换到新版本标签下重放。</summary>
    public static byte[] Message(string version, string sha256Hex) =>
        Encoding.UTF8.GetBytes($"KeepPassword-Update-v1\nversion={version.Trim().TrimStart('v', 'V')}\nsha256={sha256Hex.Trim().ToUpperInvariant()}\n");

    public static string Sign(ECDsa privateKey, string version, string sha256Hex) =>
        Convert.ToBase64String(privateKey.SignData(
            Message(version, sha256Hex), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>任意一把受信公钥验签通过即可（支持换钥期间新旧两把并存）。</summary>
    public static bool Verify(string version, string sha256Hex, string signatureBase64, IEnumerable<string> trustedPublicKeys)
    {
        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(signatureBase64.Trim());
        }
        catch (FormatException)
        {
            return false;
        }

        var message = Message(version, sha256Hex);
        foreach (var publicKey in trustedPublicKeys)
        {
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey.Trim()), out _);
                if (key.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            {
                // 配置里某把公钥格式不对：跳过它，不影响用其它公钥验签。
            }
        }

        return false;
    }
}
