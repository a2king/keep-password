using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public sealed class VaultLabelTests
{
    [Fact]
    public void SpacesAndTags_PersistAcrossUnlock()
    {
        var path = TestVault.NewPath();
        using (var session = TestVault.CreateUnlocked(path))
        {
            session.AddSpace("  工作  ");
            session.AddTag("空闲标签");
            session.Upsert(new VaultEntry { Name = "GitHub", Space = "个人", Tags = ["开发", "开发", " 重要 "] });
            session.Save();
        }

        using var reopened = TestVault.Store().Unlock(path, "correct horse");
        Assert.Equal(["工作", "个人"], reopened.Spaces);
        Assert.Equal(["空闲标签", "开发", "重要"], reopened.Tags);
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal("个人", entry.Space);
        Assert.Equal(["开发", "重要"], entry.Tags);
    }

    [Fact]
    public void Upsert_ReusesExistingLabelCasing()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        session.AddTag("Dev");
        session.Upsert(new VaultEntry { Name = "a", Tags = ["dev"] });
        Assert.Equal(["Dev"], session.Tags);
        Assert.Equal(["Dev"], Assert.Single(session.Entries).Tags);
    }

    [Fact]
    public void Rename_UpdatesEntries_AndMergesIntoExisting()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        session.Upsert(new VaultEntry { Name = "a", Space = "旧空间", Tags = ["甲", "乙"] });
        session.Upsert(new VaultEntry { Name = "b", Space = "工作", Tags = ["乙"] });

        session.RenameSpace("旧空间", "新空间");
        session.RenameTag("甲", "乙");

        Assert.Equal(["新空间", "工作"], session.Spaces);
        Assert.Equal(["乙"], session.Tags);
        var a = session.Entries.Single(entry => entry.Name == "a");
        Assert.Equal("新空间", a.Space);
        Assert.Equal(["乙"], a.Tags);
    }

    [Fact]
    public void Delete_ClearsLabelFromEntries()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        session.Upsert(new VaultEntry { Name = "a", Space = "工作", Tags = ["甲", "乙"] });

        session.DeleteSpace("工作");
        session.DeleteTag("甲");

        Assert.Empty(session.Spaces);
        Assert.Equal(["乙"], session.Tags);
        var entry = Assert.Single(session.Entries);
        Assert.Equal("", entry.Space);
        Assert.Equal(["乙"], entry.Tags);
    }

    [Fact]
    public void AddLabel_RejectsBlankName()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        Assert.Throws<ArgumentException>(() => session.AddSpace("   "));
        Assert.Throws<ArgumentException>(() => session.RenameTag("不存在", "x"));
    }

    [Fact]
    public void Search_MatchesSpaceAndTags()
    {
        var entries = new[]
        {
            new VaultEntry { Name = "a", Space = "工作" },
            new VaultEntry { Name = "b", Tags = ["银行"] },
            new VaultEntry { Name = "c" }
        };
        Assert.Equal("a", Assert.Single(EntrySearch.Query(entries, "工作")).Name);
        Assert.Equal("b", Assert.Single(EntrySearch.Query(entries, "银行")).Name);
    }
}
