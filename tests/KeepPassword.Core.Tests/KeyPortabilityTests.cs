using System.Security.Cryptography;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

/// <summary>换电脑只需要 .kpkey：可以用它创建本机保险库，丢了也能在还能解锁的电脑上重新导出。</summary>
public class KeyPortabilityTests
{
    [Fact]
    public void KeyId_IsTheNumberInTheKeyFileName()
    {
        using var session = TestVault.Store().CreateLocalSession(TestVault.NewPath(), "short-key");
        var key = session.PendingEncryptionKey;
        Assert.Matches("^[0-9A-F]{12}$", session.EncryptionKeyId);
        Assert.Equal(session.EncryptionKeyId, VaultKeyFile.IdOf(key));
        var raw = session.CopyDataKey();
        Assert.Equal(session.EncryptionKeyId, VaultKeyFile.Id(raw));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(raw))[..12], VaultKeyFile.Id(raw));
        CryptographicOperations.ZeroMemory(raw);
        Assert.Throws<CredentialRejectedException>(() => VaultKeyFile.IdOf("KPB-not-a-data-key"));
    }

    [Fact]
    public void CreateWithExistingKey_ReusesTheKey_AndDoesNotAskToSaveANewOne()
    {
        var firstPath = TestVault.NewPath();
        string keyText;
        string keyId;
        using (var first = TestVault.CreateLocalUnlocked(firstPath))
        {
            first.Upsert(new VaultEntry { Name = "from-first-pc", Accounts = [new VaultAccount { Username = "u", Password = "p" }] });
            first.Save();
            keyText = VaultKeyFile.Read(firstPath + ".kpkey");
            keyId = first.EncryptionKeyId;
        }

        var secondPath = TestVault.NewPath();
        using (var second = TestVault.Store().CreateLocalSession(secondPath, "new-pc-short", encryptionKey: keyText))
        {
            Assert.Equal(keyId, second.EncryptionKeyId);
            Assert.False(second.NeedsKeyFileSave);
            Assert.Empty(second.Entries);
            var key = second.CopyDataKey();
            var fromFirst = VaultStore.ReadEntriesWithDataKey(File.ReadAllBytes(firstPath), key);
            Assert.Equal("from-first-pc", Assert.Single(fromFirst).Name);
            CryptographicOperations.ZeroMemory(key);
        }

        using var reopened = TestVault.Store().UnlockWithShortKey(secondPath, "new-pc-short");
        Assert.Equal(keyId, reopened.EncryptionKeyId);
        Assert.False(reopened.NeedsKeyFileSave);
        reopened.VerifyEncryptionKey(keyText);
    }

    [Fact]
    public void CreateWithMalformedKey_WritesNothing()
    {
        var path = TestVault.NewPath();
        Assert.Throws<CredentialRejectedException>(() =>
            TestVault.Store().CreateLocalSession(path, "short-key", encryptionKey: "KPB-this-is-a-binding-key"));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".quick"));
    }

    [Fact]
    public void ReExport_LocalKeyVault_RequiresTheShortKey_AndWritesTheSameKey()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateLocalUnlocked(path, "short-key");
        var original = VaultKeyFile.Read(path + ".kpkey");
        Assert.False(session.NeedsKeyFileSave);
        Assert.Throws<InvalidOperationException>(() => session.PendingEncryptionKey);

        Assert.Throws<CredentialRejectedException>(() => session.RevealEncryptionKey("wrong-short"));
        Assert.Throws<CredentialRejectedException>(() => session.RevealEncryptionKey(""));
        var revealed = session.RevealEncryptionKey("short-key");
        Assert.Equal(original, revealed);

        var copy = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("n"), $"KeepPassword-密钥-{session.EncryptionKeyId}.kpkey");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        session.SaveEncryptionKeyFile(copy, revealed);
        Assert.Equal(original, VaultKeyFile.Read(copy));
        Assert.Equal(session.EncryptionKeyId, VaultKeyFile.IdOf(File.ReadAllText(copy)));
    }

    [Fact]
    public void ReExport_MasterPasswordVault_RequiresTheMasterPassword()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path, masterPassword: "correct horse", shortKey: "short-key");
        session.SaveEncryptionKeyFile(path + ".kpkey", session.PendingEncryptionKey);
        Assert.Throws<CredentialRejectedException>(() => session.RevealEncryptionKey("short-key"));
        Assert.Equal(VaultKeyFile.Read(path + ".kpkey"), session.RevealEncryptionKey("correct horse"));
    }
}
