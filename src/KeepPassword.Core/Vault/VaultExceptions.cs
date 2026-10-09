namespace KeepPassword.Core.Vault;

public sealed class UnlockFailedException : Exception
{
    public UnlockFailedException()
        : base("账号、主密码或短密钥不正确。")
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
