using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class VaultMergeTests
{
    [Fact]
    public void Merge_PrefersNewerUpdatedAt_AndKeepsTombstones()
    {
        var id = Guid.NewGuid();
        var older = new VaultEntry
        {
            Id = id,
            Name = "old",
            UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
        };
        var newer = new VaultEntry
        {
            Id = id,
            Name = "new",
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T00:00:00Z")
        };
        var deleted = new VaultEntry
        {
            Id = Guid.NewGuid(),
            Kind = VaultItemKind.Login,
            UpdatedAt = DateTimeOffset.Parse("2026-01-03T00:00:00Z"),
            DeletedAt = DateTimeOffset.Parse("2026-01-03T00:00:00Z")
        };

        var result = VaultMerge.Merge([older], [newer, deleted]);
        Assert.Equal("new", Assert.Single(result.Entries, e => e.Id == id).Name);
        Assert.Contains(result.Entries, e => e.Id == deleted.Id && e.IsDeleted);
        Assert.Equal(0, result.LocalWins);
        Assert.Equal(2, result.RemoteWins);
    }

    [Fact]
    public void LocalVault_UsesShortKeyAndKeyFile_WithoutMasterPassword()
    {
        var path = TestVault.NewPath();
        string key;
        using (var created = TestVault.Store().CreateLocalSession(path, "short-key"))
        {
            Assert.Equal(VaultStore.FormatVersion, created.FormatVersion);
            Assert.False(created.HasMasterPassword);
            Assert.True(created.NeedsKeyFileSave);
            created.Upsert(Login("alpha"));
            created.Save();
            key = created.PendingEncryptionKey;
            created.SaveEncryptionKeyFile(path + ".kpkey", key);
        }

        Assert.Throws<InvalidOperationException>(() => TestVault.Store().Unlock(path, "anything"));
        using var quick = TestVault.Store().UnlockWithShortKey(path, "short-key");
        Assert.Equal("alpha", Assert.Single(quick.Entries).Name);
        Assert.False(quick.NeedsLocalKeyUpgrade);

        File.Delete(path + ".quick");
        using var paired = TestVault.Store().UnlockWithEncryptionKey(path, key, "new-short");
        Assert.Equal("alpha", Assert.Single(paired.Entries).Name);
        using var again = TestVault.Store().UnlockWithShortKey(path, "new-short");
        Assert.Equal("alpha", Assert.Single(again.Entries).Name);
    }

    [Fact]
    public void Remove_OnV3_WritesTombstone_AndUpgradeFromV2()
    {
        var path = TestVault.NewPath();
        string key;
        using (var v2 = TestVault.CreateUnlocked(path))
        {
            Assert.Equal(VaultStore.IndependentKeyVersion, v2.FormatVersion);
            v2.Upsert(Login("keep"));
            v2.Save();
            key = v2.PendingEncryptionKey;
            v2.SaveEncryptionKeyFile(path + ".kpkey", key);
            Assert.True(v2.NeedsLocalKeyUpgrade);
            v2.UpgradeToLocalKeyFormat();
            Assert.False(v2.NeedsLocalKeyUpgrade);
            Assert.False(v2.HasMasterPassword);
            var id = Assert.Single(v2.Entries).Id;
            Assert.True(v2.Remove(id));
            Assert.Empty(v2.Entries);
            Assert.True(Assert.Single(v2.AllRecords).IsDeleted);
            v2.Save();
        }

        using var reloaded = TestVault.Store().UnlockWithShortKey(path, "short-key");
        Assert.Empty(reloaded.Entries);
        Assert.True(Assert.Single(reloaded.AllRecords).IsDeleted);
        Assert.Equal(3, (int)System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!["version"]!);
    }

    [Fact]
    public void MergeIntoSession_CombinesBothSides()
    {
        var path = TestVault.NewPath();
        using var local = TestVault.Store().CreateLocalSession(path, "short-key");
        var shared = Guid.NewGuid();
        var localOnly = Login("only-local", Guid.NewGuid(), DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var remoteNewer = Login("remote-newer", shared, DateTimeOffset.Parse("2026-03-01T00:00:00Z"));
        var remoteOnly = Login("only-remote", Guid.NewGuid(), DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        local.ReplaceAllRecords(
        [
            Login("local-newer", shared, DateTimeOffset.Parse("2026-02-01T00:00:00Z")),
            localOnly
        ]);

        var merged = VaultMerge.Merge(local.AllRecords, [remoteNewer, remoteOnly]);
        local.ReplaceAllRecords(merged.Entries, merged.Spaces, merged.Tags);
        var names = local.Entries.Select(e => e.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["only-local", "only-remote", "remote-newer"], names);
    }

    private static VaultEntry Login(string name, Guid? id = null, DateTimeOffset? updatedAt = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        UpdatedAt = updatedAt ?? default,
        Accounts = [new VaultAccount { Username = "u", Password = "p" }]
    };
}
