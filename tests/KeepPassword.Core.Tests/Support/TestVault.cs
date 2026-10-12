using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests.Support;

internal static class TestVault
{
    public static string NewPath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "keep-password-tests");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, Guid.NewGuid().ToString("n") + ".kpvault");
    }

    public static VaultStore Store() => new(KdfProfile.Fast);

    public static VaultSession CreateUnlocked(
        string path,
        string account = "ada",
        string masterPassword = "correct horse",
        string shortKey = "short-key")
    {
        return Store().CreateSession(path, account, masterPassword, shortKey);
    }
}
