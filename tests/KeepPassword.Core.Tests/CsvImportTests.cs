using KeepPassword.Core.Import;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class CsvImportTests
{
    [Fact]
    public void Import_CommaDelimiter_ReadsRows()
    {
        const string csv = """
            name,url,username,password,note
            GitHub,https://github.com,ada,"p,ass",工作账号
            邮箱,https://mail.example.com,ada@example.com,hunter2,个人
            """;

        var entries = CsvImporter.Import(csv);

        Assert.Equal(2, entries.Count);
        Assert.Equal("p,ass", entries[0].Password);
        Assert.Equal("ada@example.com", entries[1].Username);
        Assert.Equal("个人", entries[1].Note);
    }

    [Fact]
    public void Import_TabDelimiter_ReadsRows()
    {
        const string csv = "name\turl\tusername\tpassword\tnote\n银行\thttps://bank.example\tada\tsecret\t备注\n";

        var entries = CsvImporter.Import(csv);

        var entry = Assert.Single(entries);
        Assert.Equal("银行", entry.Name);
        Assert.Equal("https://bank.example", entry.Url);
        Assert.Equal("secret", entry.Password);
        Assert.Equal("备注", entry.Note);
    }

    [Fact]
    public void Import_HeaderIsCaseInsensitive_AndMissingColumnFails()
    {
        var entries = CsvImporter.Import("Name,URL,Username,Password,Note\nA,https://a.example,u,p,n\n");
        Assert.Single(entries);

        var chrome = CsvImporter.Import("name,url,username,password\nA,https://a.example,u,p\n");
        Assert.Equal("A", Assert.Single(chrome).Name);

        var error = Assert.Throws<FormatException>(() => CsvImporter.Import("name,url,username\nA,https://a.example,u\n"));
        Assert.Contains("password", error.Message);
    }

    [Fact]
    public void Import_IntoVault_IsGroupedAndSearchable()
    {
        var path = TestVault.NewPath();
        const string csv = "name,url,username,password,note\nGitHub,https://github.com/login,ada,pw,工作\n邮箱,https://mail.example.com,ada,pw,私人\n";
        using (var session = TestVault.CreateUnlocked(path))
        {
            foreach (var entry in CsvImporter.Import(csv))
            {
                session.Upsert(entry);
            }

            session.Save();
        }

        using var unlocked = TestVault.Store().Unlock(path, "correct horse");
        var groups = DomainGrouping.Group(unlocked.Entries);
        Assert.Contains(groups, group => group.Domain == "github.com");
        Assert.Contains(groups, group => group.Domain == "mail.example.com");
        var found = EntrySearch.Query(unlocked.Entries, "私人");
        Assert.Equal("邮箱", Assert.Single(found).Name);
    }
}
