namespace KeepPassword.Core.Sync;

/// <summary>设备绑定验证码格式，与 keep-password-server tokens.issue_email_code 的 device_bind 一致。</summary>
public static class DeviceBindCode
{
    /// <summary>去掉易混淆字符后的 8 位字母数字。不是 6 位数字。</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string Normalize(string? code)
    {
        var trimmed = (code ?? "").Trim().Replace(" ", "");
        if (trimmed.Length == 0)
        {
            throw new ArgumentException("请填写邮箱验证码。");
        }

        var upper = trimmed.ToUpperInvariant();
        if (upper.Length == 6 && upper.All(char.IsDigit))
        {
            throw new ArgumentException("设备绑定验证码是 8 位字母数字，15 分钟内有效。6 位数字不能用于开启云同步。");
        }

        if (upper.Length != 8 || upper.Any(ch => !Alphabet.Contains(ch)))
        {
            throw new ArgumentException("请填写邮件中的 8 位字母数字验证码。");
        }

        return upper;
    }
}

/// <summary>给用户看的同步文案。成功句只描述已经确认的事实，不包含密钥或令牌。</summary>
public static class SyncMessages
{
    public const string CodeAccepted = "若该邮箱已完成验证，验证码将发到邮箱。请填写邮件中的 8 位字母数字，15 分钟内有效。";

    public const string VaultPending = "设备已绑定，保险库尚未上传成功。本地数据保留，请稍后刷新同步，不要再次绑定。";

    public const string SessionExpired = "云同步登录已失效，请重新绑定。本地保险库未改动。";

    public const string AdoptedHint = "本机已改用你选择的加密密钥。创建本机保险库时保存的那份 .kpkey 不再适用，请保管好刚才选择的那一份；短密钥仍可解锁。";

    public const string EnabledPrefix = "已开启云同步";
    public const string KeyChanged =
        "检测到云端换了加密密钥，已暂停自动和手动同步。本地保险库仍可打开。确认是本人操作后，用新的密钥文件恢复同步。若非本人操作，请修改网站密码，并在个人中心检查、解绑异常设备；修改网站密码不会作废已泄露的密钥文件。";
    public const string UpgradeRequired =
        "云端已由更新版本的客户端写入，这台客户端不能覆盖。请把客户端升级到最新版本后再同步。本地保险库未改动，同步已暂停。";
    public const string RotateNeedsCurrent =
        "当前密钥无法解开云端保险库，不能换钥。解密失败不能当成空库。本地数据未改动。";
    public const string AdoptRejected =
        "这个密钥文件无法解开云端保险库。同步仍暂停，本地数据未改动。";

    /// <summary>同步暂停原因：云端保险库用的是另一把密钥，等待用户选择对应的 .kpkey。</summary>
    public const string NeedsKeyReason = "needkey";

    /// <summary>绑定成功但本机密钥解不开云端数据时的提示。编号来自服务器，只是提示，不含密钥。</summary>
    public static string NeedsCloudKey(string? cloudKeyId) =>
        "设备已绑定，但云端保险库使用另一把加密密钥"
        + (string.IsNullOrWhiteSpace(cloudKeyId) ? "" : $"（编号 {cloudKeyId}，即文件名为 KeepPassword-密钥-{cloudKeyId}.kpkey 的那一份）")
        + "。请点击「用密钥文件恢复同步」并选择它，云端和本机数据会自动合并。在此之前同步暂停，本地数据未改动。";

    /// <summary>选错密钥文件时的提示：说明选中的和云端需要的分别是哪一份。</summary>
    public static string WrongKeyFile(string chosenId, string? cloudKeyId) =>
        string.IsNullOrWhiteSpace(cloudKeyId)
            ? AdoptRejected
            : $"选择的密钥编号是 {chosenId}，云端需要编号 {cloudKeyId} 的密钥文件。同步仍暂停，本地数据未改动。";
}

public sealed class KeyChangeResult
{
    public bool NeedsConfirmation { get; init; }
    public int ConflictCount { get; init; }
    public bool Committed { get; init; }
    public long KeyVersion { get; init; }
    public long VaultVersion { get; init; }
}
