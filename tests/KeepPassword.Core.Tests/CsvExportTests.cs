using KeepPassword.Core.Import;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public sealed class CsvExportTests
{
    [Fact]
    public void Export_SplitsAccountsAndRoundTripsLogin()
    {
        var entry = new VaultEntry
        {
            Name = "GitHub",
            Url = "https://github.com",
            Space = "工作",
            Tags = ["开发", "重要"],
            Favorite = true,
            Note = "总备注",
            Accounts =
            [
                new VaultAccount { Username = "ada", Password = "p,w\"x", Note = "第一行\n第二行", TotpSecret = "GEZDGNBVGY3TQOJQ" },
                new VaultAccount { Username = "bob", Password = "secret" },
                new VaultAccount()
            ]
        };

        var table = CsvImporter.ReadTable(CsvExporter.Export([entry]));

        Assert.Equal(CsvExporter.Headers, table.Headers);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["ada", "bob"], table.Rows.Select(row => Cell(table, row, "用户名")));
        Assert.Equal("https://github.com", Cell(table, table.Rows[1], "网址"));
        Assert.Equal("p,w\"x", Cell(table, table.Rows[0], "密码"));
        Assert.Equal("第一行\n第二行", Cell(table, table.Rows[0], "账号说明"));

        var plan = CsvMapper.Build(table, VaultItemKind.Login, CsvMapper.GuessAll(table.Headers, VaultItemKind.Login), new HashSet<int>());
        Assert.Equal(2, plan.Valid);
        var first = plan.Entries[0];
        Assert.Equal("ada", first.Accounts[0].Username);
        Assert.Equal("p,w\"x", first.Accounts[0].Password);
        Assert.Equal("第一行\n第二行", first.Accounts[0].Note);
        Assert.Equal("GEZDGNBVGY3TQOJQ", first.Accounts[0].TotpSecret);
        Assert.Equal("https://github.com", first.Url);
        Assert.Equal("工作", first.Space);
        Assert.Equal(["开发", "重要"], first.Tags);
        Assert.True(first.Favorite);
        Assert.Equal("总备注", first.Note);
        Assert.Equal("bob", plan.Entries[1].Accounts[0].Username);
    }

    [Fact]
    public void Export_RepeatsSharedDatabaseFieldsPerAccount()
    {
        var entry = new VaultEntry
        {
            Name = "生产 MySQL",
            Kind = VaultItemKind.Database,
            Accounts =
            [
                new VaultAccount { Username = "reader", Password = "ro" },
                new VaultAccount { Username = "writer", Password = "rw" }
            ]
        };
        entry.SetField(FieldKeys.Driver, "mysql");
        entry.SetField(FieldKeys.Host, "db.local");
        entry.SetField(FieldKeys.Port, "3306");
        entry.SetField(FieldKeys.Database, "shop");

        var table = CsvImporter.ReadTable(CsvExporter.Export([entry]));

        Assert.Equal(2, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.Equal(("数据库", "MySQL", "db.local", "3306", "shop"), (
            Cell(table, row, "类型"),
            Cell(table, row, "数据库类型"),
            Cell(table, row, "主机"),
            Cell(table, row, "端口"),
            Cell(table, row, "数据库名"))));

        var plan = CsvMapper.Build(table, VaultItemKind.Database, CsvMapper.GuessAll(table.Headers, VaultItemKind.Database), new HashSet<int>());
        Assert.Equal(2, plan.Valid);
        Assert.All(plan.Entries, item => Assert.Equal("mysql", item.Field(FieldKeys.Driver)));
    }

    [Fact]
    public void Export_KeyAuthOmitsPassword_AndKeepsSshMaterial()
    {
        var keyId = Guid.NewGuid();
        var server = new VaultEntry
        {
            Name = "生产机",
            Kind = VaultItemKind.Server,
            SshKeyId = keyId,
            Accounts = [new VaultAccount { Username = "root", Password = "stale", Auth = FieldKeys.AuthSshKey, SshKeyId = keyId }]
        };
        server.SetField(FieldKeys.Protocol, "ssh");
        server.SetField(FieldKeys.Host, "10.0.0.1");

        var key = new VaultEntry { Name = "运维钥匙", Kind = VaultItemKind.SshKey };
        key.SetField(FieldKeys.PrivateKey, "-----BEGIN-----\nsecret-line");
        key.SetField(FieldKeys.PublicKey, "ssh-ed25519 AAAA");
        key.SetField(FieldKeys.Passphrase, "phrase");

        var table = CsvImporter.ReadTable(CsvExporter.Export([server, key]));

        Assert.Equal("密钥", Cell(table, table.Rows[0], "认证方式"));
        Assert.Equal("SSH", Cell(table, table.Rows[0], "协议"));
        Assert.Equal("", Cell(table, table.Rows[0], "密码"));
        Assert.Equal("-----BEGIN-----\nsecret-line", Cell(table, table.Rows[1], "私钥"));
        Assert.Equal("ssh-ed25519 AAAA", Cell(table, table.Rows[1], "公钥"));
        Assert.Equal("phrase", Cell(table, table.Rows[1], "密码短语"));
        Assert.Equal("", Cell(table, table.Rows[1], "用户名"));
    }

    [Fact]
    public void Export_FormatsNodesApiAndCustomFields()
    {
        var cluster = new VaultEntry
        {
            Name = "图库",
            Kind = VaultItemKind.Cluster,
            Accounts = [new VaultAccount { Username = "root", Password = "pw" }],
            Nodes = [new ClusterNode { Host = "10.0.1.1", Port = "9669" }, new ClusterNode { Host = "10.0.1.2", Port = "9669" }]
        };
        cluster.SetField(FieldKeys.Product, "NebulaGraph");

        var api = new VaultEntry { Name = "支付", Kind = VaultItemKind.ApiCredential, Url = "https://api.pay.com" };
        api.SetField(FieldKeys.ClientId, "client-1");
        api.SetField(FieldKeys.ApiKey, "key-1");
        api.CustomFields.Add(new VaultCustomField { Name = "区域;华东", Value = "a=b" });

        var table = CsvImporter.ReadTable(CsvExporter.Export([cluster, api]));

        Assert.Equal("10.0.1.1:9669, 10.0.1.2:9669", Cell(table, table.Rows[0], "节点"));
        Assert.Equal("NebulaGraph", Cell(table, table.Rows[0], "服务类型"));
        Assert.Equal("client-1", Cell(table, table.Rows[1], "Client ID"));
        Assert.Equal("key-1", Cell(table, table.Rows[1], "API Key"));
        Assert.Equal("区域\\;华东=a\\=b", Cell(table, table.Rows[1], "自定义字段"));

        var clusters = CsvMapper.Build(table, VaultItemKind.Cluster, CsvMapper.GuessAll(table.Headers, VaultItemKind.Cluster), new HashSet<int> { 1 });
        var restored = Assert.Single(clusters.Entries);
        Assert.Equal(["10.0.1.1", "10.0.1.2"], restored.Nodes.Select(node => node.Host));
        Assert.Equal("NebulaGraph", restored.Field(FieldKeys.Product));
    }

    private static string Cell(CsvTable table, IReadOnlyList<string> row, string header)
    {
        var index = table.Headers.ToList().IndexOf(header);
        Assert.True(index >= 0, header);
        return index < row.Count ? row[index] : "";
    }
}
