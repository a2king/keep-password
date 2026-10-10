namespace KeepPassword.Core.Vault;

public sealed class UnlockFailedException : Exception
{
    public UnlockFailedException()
        : base("主密码不正确。")
    {
    }
}

public sealed class CredentialRejectedException : Exception
{
    public CredentialRejectedException(string message)
        : base(message)
    {
    }
}

public sealed class RecoveryFailedException : Exception
{
    public RecoveryFailedException()
        : base("安全问题答案不正确。")
    {
    }
}
