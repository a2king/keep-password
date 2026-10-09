using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class ShortKeyChangeTests
{
    [Fact]
    public void ChangeShortKey_WrongMasterPassword_IsRejected()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        session.Upsert(new VaultEntry { Name = "保留", Url = "https://example.com", Username = "ada", Password = "pw" });
        session.Save();

        var error = Assert.Throws<CredentialRejectedException>(() => session.ChangeShortKey("not-the-master", "new-short"));

        Assert.Contains("主密码", error.Message);
        session.Lock();
        using var still = TestVault.Store().Unlock(path, "correct horse");
        Assert.Equal("保留", Assert.Single(still.Entries).Name);
        Assert.True(still.VerifyShortKey("short-key"));
        Assert.False(still.VerifyShortKey("new-short"));
    }

    [Fact]
    public void ChangeShortKey_CorrectMasterPassword_ReplacesVerifier()
    {
        var path = TestVault.NewPath();
        using (var session = TestVault.CreateUnlocked(path))
        {
            session.Upsert(new VaultEntry { Name = "保留", Password = "pw", Username = "ada", Url = "https://example.com" });
            session.ChangeShortKey("correct horse", "new-short");
        }

        using var unlocked = TestVault.Store().Unlock(path, "correct horse");
        Assert.Equal("保留", Assert.Single(unlocked.Entries).Name);
        Assert.True(unlocked.VerifyShortKey("new-short"));
        Assert.False(unlocked.VerifyShortKey("short-key"));
        Assert.DoesNotContain("new-short", File.ReadAllText(path));
    }
}
