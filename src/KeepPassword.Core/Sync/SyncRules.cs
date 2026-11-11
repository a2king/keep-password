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

    public const string AdoptedHint = "本机已改用云端加密密钥，原先保存的 .kpkey 不能再解开此库；短密钥仍可解锁。";

    public const string EnabledPrefix = "已开启云同步";
    public const string KeyChanged =
        "检测到新的云端密钥，已暂停自动和手动同步。本地保险库仍可打开。若非本人操作，请修改账号密码，并到用户管理后台更新密钥文件。修改网站密码不会作废已泄露的保险库密钥；请在个人中心单独检查并解绑异常设备。确认是本人操作后，再用新的密钥文件恢复同步。";
    public const string UpgradeRequired =
        "这台客户端不能覆盖新的密钥版本。请升级到 0.9.1 后再同步。本地保险库未改动，同步已暂停。";
    public const string RotateNeedsCurrent =
        "当前密钥无法解开云端保险库，不能换钥。解密失败不能当成空库。本地数据未改动。";
    public const string AdoptRejected =
        "这个密钥文件无法解开云端保险库。同步仍暂停，本地数据未改动。";
}

public sealed class KeyChangeResult
{
    public bool NeedsConfirmation { get; init; }
    public int ConflictCount { get; init; }
    public bool Committed { get; init; }
    public long KeyVersion { get; init; }
    public long VaultVersion { get; init; }
}
