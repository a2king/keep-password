using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class SoftLockTests
{
    [Fact]
    public void SoftLock_ThenShortKey_UnlocksWithoutMasterPassword()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path, shortKey: "pin-42");
        session.Upsert(new VaultEntry { Name = "A", Username = "u", Password = "p" });
        session.Save();

        session.SoftLock();
        Assert.False(session.IsUnlocked);
        Assert.True(session.CanSoftUnlock);
        Assert.Throws<InvalidOperationException>(() => _ = session.Entries);

        session.UnlockWithShortKey("pin-42");
        Assert.True(session.IsUnlocked);
        Assert.Equal("A", Assert.Single(session.Entries).Name);
    }

    [Fact]
    public void SoftLock_WrongShortKey_Fails()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path, shortKey: "pin-42");
        session.SoftLock();

        Assert.Throws<UnlockFailedException>(() => session.UnlockWithShortKey("wrong"));
        Assert.False(session.IsUnlocked);
        Assert.True(session.CanSoftUnlock);
    }

    [Fact]
    public void HardLock_ClearsSession_AndBlocksShortKeyUnlock()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path, shortKey: "pin-42");
        session.SoftLock();
        session.Lock();

        Assert.False(session.CanSoftUnlock);
        Assert.Throws<UnlockFailedException>(() => session.UnlockWithShortKey("pin-42"));
    }
}
