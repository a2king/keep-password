using KeepPassword.Core.Autofill;
using KeepPassword.Core.Import;
using KeepPassword.Core.Security;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public sealed class ItemTypeTests
{
    private const string Ed25519Fingerprint = "SHA256:u3ZTm5VD3E7k+oNqSox4yReb4qJGOARKQE44e2gVXvo";

    [Fact]
    public void LegacyEntry_LoadsAsLogin()
    {
        Assert.Equal(VaultItemKind.Login, VaultItemKinds.Parse(null));
        Assert.Equal(VaultItemKind.Login, VaultItemKinds.Parse("future-kind"));
        Assert.Equal(VaultItemKind.Cluster, VaultItemKinds.Parse("cluster"));

        var path = TestVault.NewPath();
        using (var session = TestVault.CreateUnlocked(path))
        {
            session.Upsert(new VaultEntry { Name = "GitHub", Url = "https://github.com", Username = "ada", Password = "pw" });
            session.Save();
        }

        using var reopened = TestVault.Store().Unlock(path, "correct horse");
        var entry = Assert.Single(reopened.Entries);
        Assert.Equal(VaultItemKind.Login, entry.Kind);
        Assert.False(entry.Favorite);
        Assert.Empty(entry.Fields);
        Assert.Empty(entry.CustomFields);
        Assert.Empty(entry.Nodes);
    }

    [Fact]
    public void TypedEntries_PersistAllFields()
    {
        var path = TestVault.NewPath();
        Guid keyId;
        Guid serverId;
        using (var session = TestVault.CreateUnlocked(path))
        {
            var key = new VaultEntry { Name = "部署密钥", Kind = VaultItemKind.SshKey };
            key.SetField(FieldKeys.PrivateKey, Fixture("ed.key"));
            key.SetField(FieldKeys.PublicKey, Fixture("ed.pub"));
            session.Upsert(key);
            keyId = Assert.Single(session.Entries).Id;

            var server = new VaultEntry { Name = "生产机", Kind = VaultItemKind.Server, Username = "root", Favorite = true, SshKeyId = keyId };
            server.SetField(FieldKeys.Protocol, "ssh");
            server.SetField(FieldKeys.Host, "10.0.0.8");
            server.SetField(FieldKeys.Port, "2222");
            server.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
            server.CustomFields.Add(new VaultCustomField { Name = " 机房 ", Value = "杭州" });
            server.CustomFields.Add(new VaultCustomField { Name = "堡垒机口令", Value = "s3cret", Sensitive = true });
            session.Upsert(server);
            serverId = session.Entries.Single(entry => entry.Kind == VaultItemKind.Server).Id;

            var cluster = new VaultEntry { Name = "Nebula", Kind = VaultItemKind.Cluster, Username = "root", Password = "nebula" };
            cluster.SetField(FieldKeys.Product, "NebulaGraph");
            cluster.Nodes.Add(new ClusterNode { Name = "graphd-1", Role = "graphd", Host = "10.0.1.1", Port = "9669", Service = "graph" });
            cluster.Nodes.Add(new ClusterNode { Name = "metad-1", Role = "metad", Host = "10.0.1.2", Port = "9559" });
            session.Upsert(cluster);
            session.Save();
        }

        using var reopened = TestVault.Store().Unlock(path, "correct horse");
        var storedKey = reopened.Entries.Single(entry => entry.Id == keyId);
        Assert.Equal(VaultItemKind.SshKey, storedKey.Kind);
        Assert.Equal("ED25519", storedKey.Field(FieldKeys.KeyType));
        Assert.Equal(Ed25519Fingerprint, storedKey.Field(FieldKeys.Fingerprint));

        var storedServer = reopened.Entries.Single(entry => entry.Id == serverId);
        Assert.True(storedServer.Favorite);
        Assert.Equal(keyId, storedServer.SshKeyId);
        Assert.Equal("2222", storedServer.Field(FieldKeys.Port));
        Assert.Equal(["机房", "堡垒机口令"], storedServer.CustomFields.Select(field => field.Name));
        Assert.True(storedServer.CustomFields[1].Sensitive);
        Assert.Equal(["生产机"], reopened.ReferencingNames(keyId));

        var storedCluster = reopened.Entries.Single(entry => entry.Kind == VaultItemKind.Cluster);
        Assert.Equal(2, storedCluster.Nodes.Count);
        Assert.Equal("graph", storedCluster.Nodes[0].Service);
        Assert.Equal("NebulaGraph", storedCluster.Field(FieldKeys.Product));
    }

    [Fact]
    public void Kind_CannotChangeAfterCreation()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        session.Upsert(new VaultEntry { Name = "a", Username = "ada" });
        var entry = Assert.Single(session.Entries);
        entry.Kind = VaultItemKind.ApiCredential;
        entry.SetField(FieldKeys.Token, "t");
        Assert.Throws<InvalidOperationException>(() => session.Upsert(entry));
        Assert.Equal(VaultItemKind.Login, Assert.Single(session.Entries).Kind);
    }

    [Fact]
    public void SshReference_MustExist_AndIsUnlinkedOnRemove()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var server = Server("10.0.0.1");
        server.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
        server.SshKeyId = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => session.Upsert(server));

        server.SshKeyId = null;
        Assert.Throws<ArgumentException>(() => session.Upsert(server));

        var key = new VaultEntry { Name = "k", Kind = VaultItemKind.SshKey };
        key.SetField(FieldKeys.PrivateKey, Fixture("ed.key"));
        session.Upsert(key);
        var keyId = Assert.Single(session.Entries).Id;

        var login = new VaultEntry { Name = "login", Username = "ada", SshKeyId = keyId };
        session.Upsert(login);
        Assert.Null(session.Entries.Single(entry => entry.Name == "login").SshKeyId);

        server.SshKeyId = keyId;
        session.Upsert(server);
        Assert.True(session.Remove(keyId));
        var unlinked = session.Entries.Single(entry => entry.Kind == VaultItemKind.Server);
        Assert.Null(unlinked.SshKeyId);
        Assert.Equal(FieldKeys.AuthPassword, unlinked.Field(FieldKeys.Auth));
        session.Upsert(unlinked);
    }

    [Fact]
    public void KeyReference_IgnoredForProtocolsWithoutKeySupport()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var key = new VaultEntry { Name = "k", Kind = VaultItemKind.SshKey };
        key.SetField(FieldKeys.PrivateKey, Fixture("ed.key"));
        session.Upsert(key);

        var rdp = Server("win.local");
        rdp.SetField(FieldKeys.Protocol, "rdp");
        rdp.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
        rdp.SshKeyId = Assert.Single(session.Entries).Id;
        Assert.False(ItemTemplates.UsesKeyAuth(rdp));
        Assert.Contains(ItemTemplates.Fields(rdp), spec => spec.Key == FieldKeys.Password);
        session.Upsert(rdp);
        Assert.Null(session.Entries.Single(entry => entry.Kind == VaultItemKind.Server).SshKeyId);
    }

    [Fact]
    public void ReplaceAll_DropsDanglingReferences()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var server = Server("10.0.0.2");
        server.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
        server.SshKeyId = Guid.NewGuid();
        session.ReplaceAll([server, new VaultEntry { Name = "web", Username = "ada" }]);

        Assert.Equal(2, session.Entries.Count);
        var restored = session.Entries.Single(entry => entry.Kind == VaultItemKind.Server);
        Assert.Null(restored.SshKeyId);
        Assert.Equal(FieldKeys.AuthPassword, restored.Field(FieldKeys.Auth));
    }

    [Fact]
    public void SshKeyInfo_ComputesOpenSshFingerprints()
    {
        Assert.Equal(new SshKeyDetails("ED25519", Ed25519Fingerprint), SshKeyInfo.FromPublicKey(Fixture("ed.pub")));
        Assert.Equal(new SshKeyDetails("ED25519", Ed25519Fingerprint), SshKeyInfo.FromPrivateKey(Fixture("ed.key")));

        var encrypted = SshKeyInfo.Inspect(Fixture("enc.key"), null);
        Assert.Null(encrypted.Warning);
        Assert.Equal("SHA256:pUto8nnu7E++n8ScbkpQuU2MRnUbBHTV3k5UVPbpAIU", encrypted.Details?.Fingerprint);
    }

    [Fact]
    public void SshKeyInfo_ComputesPemFingerprints()
    {
        Assert.Equal(new SshKeyDetails("RSA 2048", "SHA256:nRInySNGjhIDHwdixHTIpelKzemeZfujydmUawEPvf4"), SshKeyInfo.FromPrivateKey(Fixture("rsa.key")));
        Assert.Equal(SshKeyInfo.FromPrivateKey(Fixture("rsa.key")), SshKeyInfo.FromPublicKey(Fixture("rsa.pub")));
        Assert.Equal(new SshKeyDetails("ECDSA P-256", "SHA256:sE7VgmB1uAwmyhibfjkcMCRBJpK9FSQHGpv2cmf0mkM"), SshKeyInfo.FromPrivateKey(Fixture("ec.key")));
        Assert.Equal(SshKeyInfo.FromPrivateKey(Fixture("ec.key")), SshKeyInfo.FromPublicKey(Fixture("ec.pub")));
    }

    [Fact]
    public void SshKeyInfo_WarnsButAcceptsUnknownInput()
    {
        var encryptedPem = SshKeyInfo.Inspect(Fixture("encpem.key"), null);
        Assert.Null(encryptedPem.Details);
        Assert.Contains("已加密", encryptedPem.Warning);
        Assert.NotNull(SshKeyInfo.Inspect(Fixture("encpem.key"), Fixture("encpem.pub")).Details);

        var unknown = SshKeyInfo.Inspect("not a key at all", null);
        Assert.Null(unknown.Details);
        Assert.Contains("无法识别", unknown.Warning);

        var mismatch = SshKeyInfo.Inspect(Fixture("ed.key"), Fixture("rsa.pub"));
        Assert.Contains("不匹配", mismatch.Warning);
        Assert.Contains("无法识别", SshKeyInfo.Inspect(null, "ssh-ed25519 ###").Warning);

        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var key = new VaultEntry { Name = "odd", Kind = VaultItemKind.SshKey };
        key.SetField(FieldKeys.PrivateKey, "not a key at all");
        session.Upsert(key);
        var stored = Assert.Single(session.Entries);
        Assert.Equal("", stored.Field(FieldKeys.Fingerprint));
        Assert.Equal("not a key at all", stored.Field(FieldKeys.PrivateKey));
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("postgresql")]
    [InlineData("sqlserver")]
    [InlineData("mongodb")]
    [InlineData("redis")]
    [InlineData("clickhouse")]
    [InlineData("doris")]
    [InlineData("elasticsearch")]
    [InlineData("oss")]
    public void Database_RequiresHostAndHasNoConnectionField(string driver)
    {
        var entry = new VaultEntry { Kind = VaultItemKind.Database, Username = "app", Password = "p@ss:w" };
        entry.SetField(FieldKeys.Driver, driver);
        Assert.NotNull(VaultItemRules.Validate(entry));
        entry.SetField(FieldKeys.Host, "db.local");
        entry.SetField(FieldKeys.Database, "shop");
        Assert.Null(VaultItemRules.Validate(entry));
        Assert.DoesNotContain(ItemTemplates.Fields(entry), spec => spec.Key == FieldKeys.Connection);
    }

    [Fact]
    public void Database_SqliteRequiresFile()
    {
        var sqlite = new VaultEntry { Kind = VaultItemKind.Database };
        sqlite.SetField(FieldKeys.Driver, "sqlite");
        Assert.NotNull(VaultItemRules.Validate(sqlite));
        sqlite.SetField(FieldKeys.Database, @"C:\data\app.db");
        Assert.Null(VaultItemRules.Validate(sqlite));
        Assert.DoesNotContain(ItemTemplates.Fields(sqlite), spec => spec.Key == FieldKeys.Host);
    }

    [Fact]
    public void LegacyConnectionString_BecomesSensitiveCustomField()
    {
        var entry = new VaultEntry { Kind = VaultItemKind.Database };
        entry.SetField(FieldKeys.Connection, "  jdbc:mysql://x/y  ");
        entry.CustomFields.Add(new VaultCustomField { Name = "连接字符串", Value = "old" });
        ItemTemplates.MigrateLegacyFields(entry);
        Assert.Equal("", entry.Field(FieldKeys.Connection));
        var migrated = Assert.Single(entry.CustomFields, field => field.Name == "连接字符串 2");
        Assert.Equal("jdbc:mysql://x/y", migrated.Value);
        Assert.True(migrated.Sensitive);
    }

    [Fact]
    public void LegacyConnectionString_MigratesWhenVaultReopens()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");
        using (var session = store.Unlock(path, "correct horse"))
        {
            var entry = new VaultEntry { Name = "db", Kind = VaultItemKind.Database };
            entry.SetField(FieldKeys.Driver, "mysql");
            entry.SetField(FieldKeys.Host, "db.local");
            entry.SetField(FieldKeys.Connection, "mysql://x/y");
            session.Upsert(entry);
            session.Save();
        }

        using var reopened = store.Unlock(path, "correct horse");
        var stored = Assert.Single(reopened.Entries);
        Assert.Equal("", stored.Field(FieldKeys.Connection));
        var field = Assert.Single(stored.CustomFields);
        Assert.Equal(("连接字符串", "mysql://x/y", true), (field.Name, field.Value, field.Sensitive));
    }

    [Fact]
    public void Rules_ValidatePerKind()
    {
        Assert.False(VaultItemRules.IsValidPort("0"));
        Assert.False(VaultItemRules.IsValidPort("65536"));
        Assert.False(VaultItemRules.IsValidPort("22a"));
        Assert.True(VaultItemRules.IsValidPort(""));
        Assert.True(VaultItemRules.IsValidPort(" 443 "));

        var server = Server("");
        Assert.Contains("主机", VaultItemRules.Validate(server));
        server.SetField(FieldKeys.Host, "h");
        server.SetField(FieldKeys.Port, "99999");
        Assert.Contains("端口", VaultItemRules.Validate(server));

        var api = new VaultEntry { Kind = VaultItemKind.ApiCredential, Url = "https://api.example.com" };
        Assert.NotNull(VaultItemRules.Validate(api));
        api.SetField(FieldKeys.Secret, "s");
        Assert.Null(VaultItemRules.Validate(api));

        var cluster = new VaultEntry { Kind = VaultItemKind.Cluster };
        Assert.Contains("节点", VaultItemRules.Validate(cluster));
        cluster.Nodes.Add(new ClusterNode { Host = "a", Port = "9669" });
        cluster.Nodes.Add(new ClusterNode { Host = "", Port = "9669" });
        Assert.Contains("第 2 个节点", VaultItemRules.Validate(cluster));
        cluster.Nodes[1].Host = "b";
        cluster.Nodes[1].Port = "x";
        Assert.Contains("端口", VaultItemRules.Validate(cluster));
        cluster.Nodes[1].Port = "";
        Assert.Null(VaultItemRules.Validate(cluster));
        cluster.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
        Assert.Contains("SSH 密钥", VaultItemRules.Validate(cluster));

        var login = new VaultEntry { Name = "a" };
        login.CustomFields.Add(new VaultCustomField { Name = "PIN" });
        login.CustomFields.Add(new VaultCustomField { Name = " pin " });
        Assert.Contains("重复", VaultItemRules.Validate(login));
        login.CustomFields[1].Name = "";
        Assert.NotNull(VaultItemRules.Validate(login));
        login.CustomFields[1].Name = new string('x', VaultCustomField.MaxNameLength + 1);
        Assert.NotNull(VaultItemRules.Validate(login));
        login.CustomFields.RemoveAt(1);
        login.Note = new string('n', VaultItemRules.MaxValueLength + 1);
        Assert.Contains("过长", VaultItemRules.Validate(login));
        Assert.Null(VaultItemRules.Validate(new VaultEntry()));
    }

    [Fact]
    public void Search_MatchesTechnicalFields_ButNotSecrets()
    {
        var server = Server("10.20.30.40");
        server.Password = "hunter2";
        server.CustomFields.Add(new VaultCustomField { Name = "机房", Value = "杭州" });
        server.CustomFields.Add(new VaultCustomField { Name = "PIN", Value = "998877", Sensitive = true });
        var cluster = new VaultEntry { Name = "图数据库", Kind = VaultItemKind.Cluster };
        cluster.Nodes.Add(new ClusterNode { Name = "graphd-1", Role = "graphd", Host = "nebula-a.local" });
        var api = new VaultEntry { Name = "支付", Kind = VaultItemKind.ApiCredential };
        api.SetField(FieldKeys.Token, "tok_secret_value");
        api.SetField(FieldKeys.Environment, "生产");
        VaultEntry[] entries = [server, cluster, api];

        Assert.Same(server, Assert.Single(EntrySearch.Query(entries, "10.20.30")));
        Assert.Same(server, Assert.Single(EntrySearch.Query(entries, "杭州")));
        Assert.Same(server, Assert.Single(EntrySearch.Query(entries, "PIN")));
        Assert.Same(cluster, Assert.Single(EntrySearch.Query(entries, "nebula-a")));
        Assert.Same(cluster, Assert.Single(EntrySearch.Query(entries, "graphd")));
        Assert.Same(cluster, Assert.Single(EntrySearch.Query(entries, "集群")));
        Assert.Same(api, Assert.Single(EntrySearch.Query(entries, "生产")));
        Assert.Empty(EntrySearch.Query(entries, "hunter2"));
        Assert.Empty(EntrySearch.Query(entries, "998877"));
        Assert.Empty(EntrySearch.Query(entries, "tok_secret"));
    }

    [Fact]
    public void AutofillAndAudit_OnlyConsiderLoginSecrets()
    {
        var login = new VaultEntry { Id = Guid.NewGuid(), Name = "web", Url = "https://example.com", Username = "ada", Password = "same" };
        var api = new VaultEntry { Id = Guid.NewGuid(), Name = "api", Kind = VaultItemKind.ApiCredential, Url = "https://example.com", Username = "ada" };
        var server = new VaultEntry { Id = Guid.NewGuid(), Name = "srv", Kind = VaultItemKind.Server, Url = "https://example.com", Username = "ada", Password = "same" };

        var matches = AutofillMatcher.Match([login, api, server], new AutofillRequest { Origin = AutofillOrigin.Browser, Url = "https://example.com/login" });
        Assert.Equal(login.Id, Assert.Single(matches).Id);

        var keyServer = server.Clone();
        keyServer.Id = Guid.NewGuid();
        keyServer.SetField(FieldKeys.Protocol, "ssh");
        keyServer.SetField(FieldKeys.Auth, FieldKeys.AuthSshKey);
        keyServer.Password = "";
        var findings = VaultAudit.Analyze([login, api, keyServer]);
        Assert.DoesNotContain(findings, finding => finding.EntryId == api.Id || finding.EntryId == keyServer.Id);
    }

    [Fact]
    public void CsvMapper_GuessesTargetsPerKind()
    {
        string[] headers = ["名称", "主机", "端口", "用户名", "密码", "协议", "机房", "Host", ""];
        var targets = CsvMapper.GuessAll(headers, VaultItemKind.Server);
        Assert.Equal(
            [CsvMapper.Name, "field:host", "field:port", "field:username", "field:password", "field:protocol", CsvMapper.Ignore, CsvMapper.Ignore, CsvMapper.Ignore],
            targets);
        Assert.Null(CsvMapper.ValidateMapping(headers, targets));

        Assert.Equal("field:driver", CsvMapper.Guess("数据库类型", VaultItemKind.Database));
        Assert.Equal(CsvMapper.Ignore, CsvMapper.Guess("数据库类型", VaultItemKind.Login));
        Assert.Equal(CsvMapper.Ignore, CsvMapper.Guess("连接字符串", VaultItemKind.Database));
        Assert.DoesNotContain(CsvMapper.Targets(VaultItemKind.Database), target => target.Code.StartsWith("custom", StringComparison.Ordinal));
        Assert.Equal(CsvMapper.Nodes, CsvMapper.Guess("节点", VaultItemKind.Cluster));
        Assert.Equal("field:token", CsvMapper.Guess("access_token", VaultItemKind.ApiCredential));
        Assert.DoesNotContain(VaultItemKind.SshKey, CsvMapper.ImportableKinds);
        Assert.DoesNotContain(CsvMapper.Targets(VaultItemKind.Login), target => target.Code == "field:host");
    }

    [Fact]
    public void CsvMapper_RejectsInvalidMappings()
    {
        string[] headers = ["a", "b"];
        Assert.Contains("同一个字段", CsvMapper.ValidateMapping(headers, ["name", "name"]));
        Assert.NotNull(CsvMapper.ValidateMapping(headers, ["ignore", "ignore"]));
        Assert.NotNull(CsvMapper.ValidateMapping(headers, ["name"]));

        var table = CsvImporter.ReadTable("a,b\n1,2");
        Assert.Throws<ArgumentException>(() => CsvMapper.Build(table, VaultItemKind.SshKey, ["name", "ignore"], new HashSet<int>()));
        Assert.Throws<ArgumentException>(() => CsvMapper.Build(table, VaultItemKind.Login, ["name", "name"], new HashSet<int>()));
    }

    [Fact]
    public void CsvMapper_BuildsServersWithIgnoredAndInvalidRows()
    {
        var table = CsvImporter.ReadTable(
            "名称,主机,端口,协议,用户名,密码,机房,标签,收藏\n" +
            "生产,10.0.0.1,,SSH,root,pw1,杭州,\"运维;生产\",是\n" +
            "坏端口,10.0.0.2,70000,ssh,root,pw2,,,\n" +
            "坏协议,10.0.0.3,,gopher,root,pw3,,,\n" +
            ",win.local,,RDP,admin,pw4,上海,,\n" +
            "跳过,10.0.0.5,,ssh,root,pw5,,,\n");
        var targets = CsvMapper.GuessAll(table.Headers, VaultItemKind.Server);
        var plan = CsvMapper.Build(table, VaultItemKind.Server, targets, new HashSet<int> { 4 });

        Assert.Equal(1, plan.Ignored);
        Assert.Equal(2, plan.Valid);
        Assert.Equal(2, plan.Invalid);
        Assert.Equal([3, 4], plan.Rows.Where(row => row.Error is not null).Select(row => row.RowNumber));
        Assert.Contains("gopher", plan.Rows.Single(row => row.RowNumber == 4).Error);

        var first = plan.Entries[0];
        Assert.Equal(VaultItemKind.Server, first.Kind);
        Assert.Equal("ssh", first.Field(FieldKeys.Protocol));
        Assert.Equal("22", first.Field(FieldKeys.Port));
        Assert.Equal(FieldKeys.AuthPassword, first.Field(FieldKeys.Auth));
        Assert.Equal("pw1", first.Password);
        Assert.Equal(["运维", "生产"], first.Tags);
        Assert.True(first.Favorite);
        Assert.Empty(first.CustomFields);

        var second = plan.Entries[1];
        Assert.Equal("win.local", second.Name);
        Assert.Equal("rdp", second.Field(FieldKeys.Protocol));
        Assert.Equal("3389", second.Field(FieldKeys.Port));
    }

    [Fact]
    public void CsvMapper_BuildsDatabasesClustersApisAndLogins()
    {
        var databases = CsvImporter.ReadTable("name,数据库类型,host,db,user,password,备注\nshop,PostgreSQL,pg.local,shop,app,pw,主库\nmissing,mysql,,shop,app,pw,\n");
        var dbPlan = CsvMapper.Build(databases, VaultItemKind.Database, CsvMapper.GuessAll(databases.Headers, VaultItemKind.Database), new HashSet<int>());
        var db = Assert.Single(dbPlan.Entries);
        Assert.Equal("postgresql", db.Field(FieldKeys.Driver));
        Assert.Equal("5432", db.Field(FieldKeys.Port));
        Assert.Equal("主库", db.Note);
        Assert.Equal(1, dbPlan.Invalid);

        var clusters = CsvImporter.ReadTable("name,服务类型,节点,user,password\nnebula,NebulaGraph,\"10.0.1.1:9669, 10.0.1.2:9669,10.0.1.3\",root,pw\nbad,Kafka,\"10.0.2.1:abc\",root,pw\n");
        var clusterPlan = CsvMapper.Build(clusters, VaultItemKind.Cluster, CsvMapper.GuessAll(clusters.Headers, VaultItemKind.Cluster), new HashSet<int>());
        var cluster = Assert.Single(clusterPlan.Entries);
        Assert.Equal(["10.0.1.1", "10.0.1.2", "10.0.1.3"], cluster.Nodes.Select(node => node.Host));
        Assert.Equal(["9669", "9669", ""], cluster.Nodes.Select(node => node.Port));
        Assert.Equal(1, clusterPlan.Invalid);

        var apis = CsvImporter.ReadTable("name,url,api key,环境\npay,https://api.pay.com,key-1,生产\nempty,https://x.com,,\n");
        var apiPlan = CsvMapper.Build(apis, VaultItemKind.ApiCredential, CsvMapper.GuessAll(apis.Headers, VaultItemKind.ApiCredential), new HashSet<int>());
        var api = Assert.Single(apiPlan.Entries);
        Assert.Equal("key-1", api.Field(FieldKeys.ApiKey));
        Assert.Equal("生产", api.Field(FieldKeys.Environment));

        var logins = CsvImporter.ReadTable("url,username,password,PIN\nhttps://github.com/login,ada,pw,1234\n,,,5678\n");
        var loginPlan = CsvMapper.Build(logins, VaultItemKind.Login, CsvMapper.GuessAll(logins.Headers, VaultItemKind.Login), new HashSet<int>());
        var login = Assert.Single(loginPlan.Entries);
        Assert.Equal("github.com", login.Name);
        Assert.Empty(login.CustomFields);
        Assert.Equal(1, loginPlan.Invalid);
    }

    [Fact]
    public void ImportedEntries_SaveThroughSession()
    {
        var table = CsvImporter.ReadTable("名称,主机,用户名,密码\n生产,10.0.0.1,root,pw\n");
        var plan = CsvMapper.Build(table, VaultItemKind.Server, CsvMapper.GuessAll(table.Headers, VaultItemKind.Server), new HashSet<int>());
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        foreach (var entry in plan.Entries)
        {
            session.Upsert(entry);
        }

        var stored = Assert.Single(session.Entries);
        Assert.Equal("10.0.0.1", stored.Field(FieldKeys.Host));
        Assert.True(session.Remove(stored.Id));
        Assert.Empty(session.Entries);
    }

    private static VaultEntry Server(string host)
    {
        var entry = new VaultEntry { Name = "srv", Kind = VaultItemKind.Server, Username = "root" };
        entry.SetField(FieldKeys.Protocol, "ssh");
        entry.SetField(FieldKeys.Port, "22");
        entry.SetField(FieldKeys.Host, host);
        return entry;
    }

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ssh", name));
}
