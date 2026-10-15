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
                Accounts = [new VaultAccount { Username = "ada", Password = "s3cret" }],
                Note = "工作"
            });
            created.Save();
        }

        using var session = TestVault.Store().Unlock(path, "correct horse");
        var entry = Assert.Single(session.Entries);
        Assert.Equal("GitHub", entry.Name);
        var account = Assert.Single(entry.Accounts);
        Assert.Equal("ada", account.Username);
        Assert.Equal("s3cret", account.Password);
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
                Accounts = [new VaultAccount { Username = "ada@example.com", Password = "plain-password" }]
            });
            session.Save();
        }

        var text = File.ReadAllText(path);
        Assert.DoesNotContain(master, text);
        Assert.DoesNotContain(shortKey, text);
        Assert.DoesNotContain("plain-password", text);
        Assert.Contains("argon2id", text);
        Assert.Contains("aes-256-gcm", text);
        var quick = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path + ".quick"));
        Assert.DoesNotContain(master, quick);
        Assert.DoesNotContain(shortKey, quick);
        Assert.DoesNotContain("plain-password", quick);
    }

    [Fact]
    public void ShortUnlock_AfterCreate_OpensTheSameVault()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        using (var created = store.CreateSession(path, "ada", "correct horse", "short-key"))
        {
            created.Upsert(new VaultEntry
            {
                Name = "GitHub",
                Accounts = [new VaultAccount { Username = "ada", Password = "s3cret" }]
            });
            created.Save();
        }

        Assert.True(store.HasShortUnlock(path));
        using var session = store.UnlockWithShortKey(path, "short-key");
        Assert.Equal("s3cret", Assert.Single(Assert.Single(session.Entries).Accounts).Password);
        Assert.True(session.ShortUnlockReady);

        var wrong = Assert.Throws<UnlockFailedException>(() => store.UnlockWithShortKey(path, "other-key"));
        Assert.Contains("短密钥", wrong.Message);
    }

    [Fact]
    public void FirstUnlock_UsesMasterPassword_ThenShortKeyOnly()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");
        File.Delete(path + ".quick");
        Assert.False(store.HasShortUnlock(path));
        Assert.Throws<InvalidOperationException>(() => store.UnlockWithShortKey(path, "short-key"));

        using (var session = store.Unlock(path, "correct horse"))
        {
            Assert.True(session.ShortUnlockReady);
            Assert.Equal("ada", session.Account);
        }

        Assert.True(store.HasShortUnlock(path));
        var quick = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path + ".quick"));
        Assert.DoesNotContain("correct horse", quick);
        Assert.DoesNotContain("short-key", quick);
        var wrong = Assert.Throws<UnlockFailedException>(() => store.UnlockWithShortKey(path, "other-key"));
        Assert.Contains("短密钥", wrong.Message);
        using var unlocked = store.UnlockWithShortKey(path, "short-key");
        Assert.Equal("ada", unlocked.Account);
    }

    [Fact]
    public void ShortUnlock_SurvivesMasterAndShortKeyChanges()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        using (var session = store.CreateSession(path, "ada", "correct horse", "short-key"))
        {
            session.Upsert(new VaultEntry
            {
                Name = "保留",
                Accounts = [new VaultAccount { Username = "ada", Password = "pw" }]
            });
            session.Save();
            session.ChangeMasterPassword("correct horse", "new horse");
            session.ChangeShortKey("new horse", "new-short");
        }

        Assert.Throws<UnlockFailedException>(() => store.UnlockWithShortKey(path, "short-key"));
        using var unlocked = store.UnlockWithShortKey(path, "new-short");
        Assert.Equal("pw", Assert.Single(Assert.Single(unlocked.Entries).Accounts).Password);
        using var byMaster = store.Unlock(path, "new horse");
        Assert.Equal("保留", Assert.Single(byMaster.Entries).Name);
    }

    [Fact]
    public void ChangingMasterAfterPasswordUnlock_PreservesExistingShortWrap()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");
        using (var session = store.Unlock(path, "correct horse"))
        {
            Assert.True(session.ShortUnlockReady);
            session.ChangeMasterPassword("correct horse", "new horse");
            Assert.True(session.ShortUnlockReady);
        }

        Assert.True(store.HasShortUnlock(path));
        using var unlocked = store.Unlock(path, "new horse");
        Assert.Equal("ada", unlocked.Account);
        using var quick = store.UnlockWithShortKey(path, "short-key");
        Assert.Equal("ada", quick.Account);
    }

    [Fact]
    public void Recovery_DisablesShortUnlockUntilItIsEnabledAgain()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        using var created = store.CreateSession(path, "ada", "correct horse", "short-key");
        var encryptionKey = created.PendingEncryptionKey;
        Assert.True(store.HasShortUnlock(path));

        using (var recovered = store.RecoverWithKey(path, encryptionKey, "other horse"))
        {
            Assert.False(recovered.ShortUnlockReady);
        }

        Assert.False(store.HasShortUnlock(path));
        using (var session = store.Unlock(path, "other horse"))
        {
            Assert.True(session.ShortUnlockReady);
        }

        using var byShort = store.UnlockWithShortKey(path, "short-key");
        Assert.True(byShort.VerifyShortKey("short-key"));
    }

    [Fact]
    public void VaultFileAlone_DoesNotEnableShortUnlock()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");
        var copy = path + ".copy";
        File.Copy(path, copy);
        Assert.False(store.HasShortUnlock(copy));
        using var session = store.Unlock(copy, "correct horse");
        Assert.Equal("ada", session.Account);
    }
}
