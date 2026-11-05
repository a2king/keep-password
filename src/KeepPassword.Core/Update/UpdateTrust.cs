namespace KeepPassword.Core.Update;

/// <summary>
/// 受信的更新签名公钥。列表为空时，只做 SHA-256 校验（和旧版行为一致）；
/// 一旦填入公钥，更新就必须同时带有效签名，缺签名或验签失败都会拒绝并删除下载的文件。
/// 公钥由 tools/KeepPassword.Release 的 keygen 命令生成并自动写入下面的标记区间，请不要手改。
/// 私钥只留在发布者自己的机器上，永远不要提交到仓库或上传到 GitHub。
/// </summary>
public static class UpdateTrust
{
    // BEGIN-TRUSTED-KEYS
    public static readonly IReadOnlyList<string> PublicKeys = [];
    // END-TRUSTED-KEYS

    /// <summary>是否已启用签名强制校验。</summary>
    public static bool SignatureRequired => PublicKeys.Count > 0;
}
