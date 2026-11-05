using System.Security.Cryptography;
using KeepPassword.Core.Update;

namespace KeepPassword.Core.Tests;

public class UpdateSignatureTests
{
    private const string Hash = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";

    private static (ECDsa Key, string Public) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    [Fact]
    public void ValidSignature_Verifies()
    {
        var (key, pub) = NewKey();
        var signature = UpdateSignature.Sign(key, "0.9.1", Hash);
        Assert.True(UpdateSignature.Verify("0.9.1", Hash, signature, [pub]));
        // 版本号带不带 v、哈希大小写，都不影响同一份内容的验证。
        Assert.True(UpdateSignature.Verify("v0.9.1", Hash.ToLowerInvariant(), signature, [pub]));
    }

    [Fact]
    public void ReplayedTamperedOrForeignSignatures_AreRejected()
    {
        var (key, pub) = NewKey();
        var (_, otherPub) = NewKey();
        var signature = UpdateSignature.Sign(key, "0.9.1", Hash);

        // 把旧版本的合法签名搬到新版本标签下（重放）。
        Assert.False(UpdateSignature.Verify("0.9.2", Hash, signature, [pub]));
        // 安装包被换掉：哈希变了。
        Assert.False(UpdateSignature.Verify("0.9.1", new string('0', 64), signature, [pub]));
        // 签名来自别人的密钥。
        Assert.False(UpdateSignature.Verify("0.9.1", Hash, signature, [otherPub]));
        // 签名内容损坏 / 不是 base64 / 公钥配置坏了，都只是"不通过"，不抛异常。
        Assert.False(UpdateSignature.Verify("0.9.1", Hash, "not-base64!!", [pub]));
        Assert.False(UpdateSignature.Verify("0.9.1", Hash, signature, ["garbage"]));
        Assert.False(UpdateSignature.Verify("0.9.1", Hash, signature, []));
    }

    [Fact]
    public void KeyRotation_AcceptsEitherTrustedKey()
    {
        var (oldKey, oldPub) = NewKey();
        var (_, newPub) = NewKey();
        var signature = UpdateSignature.Sign(oldKey, "1.0.0", Hash);
        Assert.True(UpdateSignature.Verify("1.0.0", Hash, signature, [newPub, oldPub]));
    }

    [Fact]
    public void TrustList_IsEmptyUntilTheOwnerGeneratesAKey()
    {
        // 仓库里不内置任何私钥或测试公钥；留空时更新仍只做哈希校验。
        Assert.Equal(UpdateTrust.PublicKeys.Count > 0, UpdateTrust.SignatureRequired);
    }
}
