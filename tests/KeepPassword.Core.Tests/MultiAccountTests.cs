using KeepPassword.Core.Import;
using KeepPassword.Core.Security;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public sealed class MultiAccountTests
{
    private static VaultEntry MySql() => new()
    {
        Name = "生产 MySQL",
        Kind = VaultItemKind.Database,
        Note = "只读地址：ro.db.local",
        Accounts =
        [
            new VaultAccount { Username = "reader", Password = "Ro-Pass-2026!x", Note = "只读账号" },
            new VaultAccount { Username = "writer", Password = "Rw-Pass-2026!y", Note = "读写账号\n仅限后台任务" },
            new VaultAccount { Username = "project", Password = "Pj-Pass-2026!z" }
        ]
    };

    [Fact]
    public void Accounts_PersistInOrderAcrossUnlock()
    {
        var path = TestVault.NewPath();
        var entry = MySql();
        entry.SetField(FieldKeys.Driver, "mysql");
        entry.SetField(FieldKeys.Host, "db.local");
        using (var session = TestVault.CreateUnlocked(path))
        {
            session.Upsert(entry);
            session.Save();
        }

        using var reopened = TestVault.Store().Unlock(path, "correct horse");
        var restored = Assert.Single(reopened.Entries);
        Assert.Equal(["reader", "writer", "project"], restored.Accounts.Select(account => account.Username));
        Assert.Equal("读写账号\n仅限后台任务", restored.Accounts[1].Note);
        Assert.Equal("Pj-Pass-2026!z", restored.Accounts[2].Password);
        Assert.All(restored.Accounts, account => Assert.NotEqual(Guid.Empty, account.Id));
        Assert.Equal(3, restored.Accounts.Select(account => account.Id).Distinct().Count());
    }

    [Fact]
    public void Clone_CopiesAccountsDeeply()
    {
        var entry = MySql();
        var copy = entry.Clone();
        copy.Accounts[0].Password = "changed";
        copy.Accounts.RemoveAt(2);
        Assert.Equal("Ro-Pass-2026!x", entry.Accounts[0].Password);
        Assert.Equal(3, entry.Accounts.Count);
    }

    [Fact]
    public void Rules_RequireUsernameAndPasswordPerAccount()
    {
        var entry = MySql();
        entry.SetField(FieldKeys.Driver, "mysql");
        entry.SetField(FieldKeys.Host, "db.local");
        Assert.Null(VaultItemRules.Validate(entry));

        entry.Accounts[1].Password = "";
        Assert.Contains("第 2 个账号缺少密码", VaultItemRules.Validate(entry));
        entry.Accounts[1].Password = "pw";
        entry.Accounts[2].Username = " ";
        Assert.Contains("第 3 个账号缺少用户名", VaultItemRules.Validate(entry));

        entry.Accounts.Clear();
        Assert.Contains("至少添加一个账号", VaultItemRules.Validate(entry));
    }

    [Fact]
    public void Rules_KeyAuthNeedsOnlyUsername_AndNonAccountKindsRejectAccounts()
    {
        var server = new VaultEntry
        {
            Name = "srv",
            Kind = VaultItemKind.Server,
            Accounts =
            [
                new VaultAccount { Username = "root" },
                new VaultAccount { Username = "readonly", Password = "pw" }
            ]
        };
        server.SetField(FieldKeys.Host, "10.0.0.1");
        Assert.Contains("第 1 个账号缺少密码", VaultItemRules.Validate(server));
        server.Accounts[0].Auth = FieldKeys.AuthSshKey;
        Assert.Contains("第 1 个账号请选择", VaultItemRules.Validate(server));
        server.Accounts[0].SshKeyId = Guid.NewGuid();
        Assert.Null(VaultItemRules.Validate(server));
        server.Accounts[1].Password = "";
        Assert.Contains("第 2 个账号缺少密码", VaultItemRules.Validate(server));

        var api = new VaultEntry { Name = "api", Kind = VaultItemKind.ApiCredential, Accounts = [new VaultAccount { Username = "a", Password = "b" }] };
        api.SetField(FieldKeys.ApiKey, "k");
        Assert.NotNull(VaultItemRules.Validate(api));
        api.Accounts.Clear();
        Assert.Null(VaultItemRules.Validate(api));
    }

    [Fact]
    public void Rules_TotpOnlyOnLogin_AndTotpOnlyLoginAllowed()
    {
        var totpOnly = new VaultEntry { Name = "2fa", Accounts = [new VaultAccount { TotpSecret = "GEZDGNBVGY3TQOJQ" }] };
        Assert.Null(VaultItemRules.Validate(totpOnly));

        totpOnly.Accounts.Add(new VaultAccount { TotpSecret = "GEZDGNBVGY3TQOJQ" });
        Assert.NotNull(VaultItemRules.Validate(totpOnly));

        var db = MySql();
        db.SetField(FieldKeys.Driver, "mysql");
        db.SetField(FieldKeys.Host, "db.local");
        db.Accounts[0].TotpSecret = "GEZDGNBVGY3TQOJQ";
        Assert.Contains("只有登录条目", VaultItemRules.Validate(db));
    }

    [Fact]
    public void KeyAuth_ClearsAccountPasswordsOnSave()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var key = new VaultEntry { Name = "k", Kind = VaultItemKind.SshKey };
        key.SetField(FieldKeys.PrivateKey, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh", "ed.key")));
        session.Upsert(key);

        var server = new VaultEntry
        {
            Name = "srv",
            Kind = VaultItemKind.Server,
            Accounts =
            [
                new VaultAccount { Username = "root", Password = "stale", Auth = FieldKeys.AuthSshKey, SshKeyId = key.Id },
                new VaultAccount { Username = "deploy", Password = "kept" }
            ]
        };
        server.SetField(FieldKeys.Host, "10.0.0.1");
        session.Upsert(server);

        var saved = session.Entries.Single(entry => entry.Kind == VaultItemKind.Server);
        Assert.Equal("", saved.Accounts[0].Password);
        Assert.Equal(key.Id, saved.Accounts[0].SshKeyId);
        Assert.Equal("kept", saved.Accounts[1].Password);
        Assert.Null(saved.Accounts[1].SshKeyId);
    }

    [Fact]
    public void Search_MatchesAnyAccountUsernameAndNote()
    {
        var entries = new[] { MySql(), new VaultEntry { Name = "其他", Accounts = [new VaultAccount { Username = "ada", Password = "x" }] } };
        Assert.Equal("生产 MySQL", Assert.Single(EntrySearch.Query(entries, "writer")).Name);
        Assert.Equal("生产 MySQL", Assert.Single(EntrySearch.Query(entries, "后台任务")).Name);
        Assert.Equal("其他", Assert.Single(EntrySearch.Query(entries, "ada")).Name);
    }

    [Fact]
    public void Audit_ReportsEachAccountSeparately()
    {
        var db = MySql();
        db.Accounts[1].Password = "123";
        db.Accounts[2].Password = db.Accounts[0].Password;
        var findings = VaultAudit.Analyze([db]);

        Assert.Contains(findings, finding => finding.Kind == AuditKind.Weak && finding.Name == "生产 MySQL · writer");
        Assert.Equal(2, findings.Count(finding => finding.Kind == AuditKind.Reused));
        Assert.Contains(findings, finding => finding.Kind == AuditKind.Reused && finding.Name == "生产 MySQL · project");
        Assert.Equal("单账号", VaultAudit.AccountTitle(new VaultEntry { Name = "单账号", Accounts = [new VaultAccount { Username = "a" }] }, new VaultAccount { Username = "a" }));
    }

    [Fact]
    public void CsvMapper_MapsAccountColumnsToFirstAccount()
    {
        var table = CsvImporter.ReadTable("名称,数据库类型,主机,用户名,密码,账号说明\n生产,mysql,db.local,reader,pw,只读\n");
        var targets = CsvMapper.GuessAll(table.Headers, VaultItemKind.Database);
        Assert.Equal(CsvMapper.FieldPrefix + FieldKeys.AccountNote, targets[5]);
        Assert.DoesNotContain(CsvMapper.Targets(VaultItemKind.Database), target => target.Code == CsvMapper.FieldPrefix + FieldKeys.Totp);
        Assert.Contains(CsvMapper.Targets(VaultItemKind.Login), target => target.Code == CsvMapper.FieldPrefix + FieldKeys.Totp);
        Assert.DoesNotContain(CsvMapper.Targets(VaultItemKind.ApiCredential), target => target.Code == CsvMapper.FieldPrefix + FieldKeys.Username);

        var plan = CsvMapper.Build(table, VaultItemKind.Database, targets, new HashSet<int>());
        var account = Assert.Single(Assert.Single(plan.Entries).Accounts);
        Assert.Equal(("reader", "pw", "只读"), (account.Username, account.Password, account.Note));

        var empty = CsvImporter.ReadTable("名称,数据库类型,主机\n生产,mysql,db.local\n");
        var emptyPlan = CsvMapper.Build(empty, VaultItemKind.Database, CsvMapper.GuessAll(empty.Headers, VaultItemKind.Database), new HashSet<int>());
        Assert.Equal(0, emptyPlan.Valid);
    }
}
