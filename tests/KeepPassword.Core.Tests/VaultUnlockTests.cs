using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class VaultUnlockTests
{
    [Fact]
    public void Unlock_WrongMasterPassword_Fails()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");

        var error = Assert.Throws<UnlockFailedException>(() => store.Unlock(path, "wrong horse"));

        Assert.Contains("不正确", error.Message);
    }

    [Fact]
    public void Unlock_DoesNotAskForShortKey()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");

        using var session = store.Unlock(path, "correct horse");
        Assert.Equal("ada", session.Account);
        Assert.True(session.VerifyShortKey("short-key"));
        Assert.False(session.VerifyShortKey("other-key"));
    }

    [Fact]
    public void Unlock_CorrectCredentials_ReturnsEntries()
    {
        var path = TestVault.NewPath();
        using (var created = TestVault.CreateUnlocked(path))
        {
            created.Upsert(new VaultEntry
            {
                Name = "GitHub",
                Url = "https://github.com/login",
                Username = "ada",
                Password = "s3cret",
                Note = "工作"
            });
            created.Save();
        }

        using var session = TestVault.Store().Unlock(path, "correct horse");
        var entry = Assert.Single(session.Entries);
        Assert.Equal("GitHub", entry.Name);
        Assert.Equal("ada", entry.Username);
        Assert.Equal("s3cret", entry.Password);
        Assert.Equal("工作", entry.Note);
    }

    [Fact]
    public void Secrets_AreNotStoredInPlaintext()
    {
        var path = TestVault.NewPath();
        const string master = "correct horse";
        const string shortKey = "short-key";
        using (var session = TestVault.CreateUnlocked(path, masterPassword: master, shortKey: shortKey))
        {
            session.Upsert(new VaultEntry
            {
                Name = "邮箱",
                Url = "https://mail.example.com",
                Username = "ada@example.com",
                Password = "plain-password"
            });
            session.Save();
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(master, text);
        Assert.DoesNotContain(shortKey, text);
        Assert.DoesNotContain("plain-password", text);
        Assert.Contains("argon2id", text);
        Assert.Contains("aes-256-gcm", text);
    }
}
