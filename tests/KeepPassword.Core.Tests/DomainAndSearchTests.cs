using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class DomainAndSearchTests
{
    [Fact]
    public void Group_UsesHostAndPutsEmptyUrlLast()
    {
        var entries = new[]
        {
            Entry("令牌", ""),
            Entry("GitHub", "https://GitHub.com/login"),
            Entry("邮箱", "mail.example.com"),
            Entry("子域", "https://www.github.com/settings")
        };

        var groups = DomainGrouping.Group(entries);

        Assert.Equal(
            ["github.com", "mail.example.com", "www.github.com", DomainGrouping.Uncategorized],
            groups.Select(group => group.Domain).ToArray());
        Assert.Equal("令牌", Assert.Single(groups[^1].Entries).Name);
    }

    [Fact]
    public void Search_CoversNameUrlUsernameAndNote_NotPassword()
    {
        var entries = new[]
        {
            new VaultEntry
            {
                Name = "GitHub",
                Url = "https://github.com",
                Username = "ada",
                Password = "unique-secret-xyz",
                Note = "工作笔记"
            }
        };

        Assert.Equal("GitHub", Assert.Single(EntrySearch.Query(entries, "git")).Name);
        Assert.Equal("GitHub", Assert.Single(EntrySearch.Query(entries, "ADA")).Name);
        Assert.Equal("GitHub", Assert.Single(EntrySearch.Query(entries, "笔记")).Name);
        Assert.Empty(EntrySearch.Query(entries, "unique-secret-xyz"));
        Assert.Single(EntrySearch.Query(entries, "  "));
    }

    private static VaultEntry Entry(string name, string url) => new()
    {
        Name = name,
        Url = url,
        Username = "ada",
        Password = "pw"
    };
}
