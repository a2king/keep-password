namespace KeepPassword.Core.Vault;

public static class FieldKeys
{
    public const string Url = "url";
    public const string Username = "username";
    public const string Password = "password";
    public const string Totp = "totp";
    public const string Protocol = "protocol";
    public const string Host = "host";
    public const string Port = "port";
    public const string Auth = "auth";
    public const string Driver = "driver";
    public const string Database = "database";
    public const string Connection = "connection";
    public const string PrivateKey = "privateKey";
    public const string PublicKey = "publicKey";
    public const string Passphrase = "passphrase";
    public const string Fingerprint = "fingerprint";
    public const string KeyType = "keyType";
    public const string ApiKey = "apiKey";
    public const string Secret = "secret";
    public const string Token = "token";
    public const string Environment = "environment";
    public const string Product = "product";

    public const string AuthPassword = "password";
    public const string AuthSshKey = "sshKey";

    public static bool IsEntryProperty(string key) => key is Url or Username or Password or Totp;

    public static string Read(VaultEntry entry, string key) => key switch
    {
        Url => entry.Url,
        Username => entry.Username,
        Password => entry.Password,
        Totp => entry.TotpSecret ?? "",
        _ => entry.Field(key)
    };

    public static void Write(VaultEntry entry, string key, string value)
    {
        switch (key)
        {
            case Url:
                entry.Url = value.Trim();
                break;
            case Username:
                entry.Username = value;
                break;
            case Password:
                entry.Password = value;
                break;
            case Totp:
                entry.TotpSecret = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                break;
            default:
                entry.SetField(key, value);
                break;
        }
    }
}

public sealed record FieldSpec(string Key, string Label, bool Sensitive = false, bool Multiline = false, string Placeholder = "");

public sealed record ServerProtocol(string Code, string Name, string DefaultPort, bool SupportsKey);

public sealed record DatabaseDriver(string Code, string Name, string DefaultPort);

public static class ItemTemplates
{
    public static IReadOnlyList<ServerProtocol> Protocols { get; } =
    [
        new("ssh", "SSH", "22", true),
        new("sftp", "SFTP", "22", true),
        new("rdp", "RDP", "3389", false),
        new("ftp", "FTP", "21", false),
        new("ftps", "FTPS", "990", false),
        new("vnc", "VNC", "5900", false),
        new("telnet", "Telnet", "23", false),
        new("generic", "通用服务器", "", false)
    ];

    public static IReadOnlyList<DatabaseDriver> Drivers { get; } =
    [
        new("mysql", "MySQL", "3306"),
        new("postgresql", "PostgreSQL", "5432"),
        new("sqlserver", "SQL Server", "1433"),
        new("sqlite", "SQLite", ""),
        new("redis", "Redis", "6379"),
        new("mongodb", "MongoDB", "27017"),
        new("elasticsearch", "Elasticsearch", "9200"),
        new("oss", "OSS 对象存储", ""),
        new("clickhouse", "ClickHouse", "9000"),
        new("doris", "Doris", "9030")
    ];

    public static ServerProtocol Protocol(string? code) =>
        Protocols.FirstOrDefault(item => item.Code == code) ?? Protocols[0];

    public static DatabaseDriver Driver(string? code) =>
        Drivers.FirstOrDefault(item => item.Code == code) ?? Drivers[0];

    public static void ApplyDefaults(VaultEntry entry)
    {
        switch (entry.Kind)
        {
            case VaultItemKind.Server:
                if (entry.Field(FieldKeys.Protocol).Length == 0)
                {
                    entry.SetField(FieldKeys.Protocol, Protocols[0].Code);
                    entry.SetField(FieldKeys.Port, Protocols[0].DefaultPort);
                }

                if (entry.Field(FieldKeys.Auth).Length == 0)
                {
                    entry.SetField(FieldKeys.Auth, FieldKeys.AuthPassword);
                }

                break;
            case VaultItemKind.Database:
                if (entry.Field(FieldKeys.Driver).Length == 0)
                {
                    entry.SetField(FieldKeys.Driver, Drivers[0].Code);
                    entry.SetField(FieldKeys.Port, Drivers[0].DefaultPort);
                }

                break;
            case VaultItemKind.Cluster:
                if (entry.Field(FieldKeys.Auth).Length == 0)
                {
                    entry.SetField(FieldKeys.Auth, FieldKeys.AuthPassword);
                }

                break;
        }
    }

    public static bool UsesKeyAuth(VaultEntry entry) =>
        VaultItemKinds.UsesSshKey(entry.Kind)
        && entry.Field(FieldKeys.Auth) == FieldKeys.AuthSshKey
        && (entry.Kind == VaultItemKind.Cluster || Protocol(entry.Field(FieldKeys.Protocol)).SupportsKey);

    public static IReadOnlyList<FieldSpec> Fields(VaultEntry entry) => entry.Kind switch
    {
        VaultItemKind.Server => ServerFields(entry),
        VaultItemKind.Database => DatabaseFields(Driver(entry.Field(FieldKeys.Driver)).Code),
        VaultItemKind.SshKey =>
        [
            new(FieldKeys.PrivateKey, "私钥", Sensitive: true, Multiline: true, Placeholder: "粘贴私钥内容，例如 -----BEGIN OPENSSH PRIVATE KEY-----"),
            new(FieldKeys.PublicKey, "公钥", Multiline: true, Placeholder: "粘贴公钥，例如 ssh-ed25519 AAAA..."),
            new(FieldKeys.Passphrase, "密码短语", Sensitive: true, Placeholder: "私钥未加密时留空")
        ],
        VaultItemKind.ApiCredential =>
        [
            new(FieldKeys.Url, "服务地址", Placeholder: "https://api.example.com"),
            new(FieldKeys.Username, "账号 / Client ID"),
            new(FieldKeys.ApiKey, "API Key", Sensitive: true),
            new(FieldKeys.Secret, "Secret", Sensitive: true),
            new(FieldKeys.Token, "Token", Sensitive: true),
            new(FieldKeys.Environment, "环境", Placeholder: "开发 / 测试 / 生产")
        ],
        VaultItemKind.Cluster => ClusterFields(entry),
        _ =>
        [
            new(FieldKeys.Url, "网址", Placeholder: "https://example.com"),
            new(FieldKeys.Username, "用户名"),
            new(FieldKeys.Password, "密码", Sensitive: true),
            new(FieldKeys.Totp, "验证码密钥", Sensitive: true, Placeholder: "Base32 密钥或 otpauth:// 链接")
        ]
    };

    private static IReadOnlyList<FieldSpec> ServerFields(VaultEntry entry)
    {
        var fields = new List<FieldSpec>
        {
            new(FieldKeys.Host, "主机 / IP"),
            new(FieldKeys.Port, "端口"),
            new(FieldKeys.Username, "用户名")
        };
        if (!UsesKeyAuth(entry))
        {
            fields.Add(new FieldSpec(FieldKeys.Password, "密码", Sensitive: true));
        }

        return fields;
    }

    private static IReadOnlyList<FieldSpec> ClusterFields(VaultEntry entry)
    {
        var fields = new List<FieldSpec>
        {
            new(FieldKeys.Product, "服务类型", Placeholder: "例如 NebulaGraph、Kafka、Elasticsearch"),
            new(FieldKeys.Username, "共用用户名")
        };
        if (!UsesKeyAuth(entry))
        {
            fields.Add(new FieldSpec(FieldKeys.Password, "共用密码", Sensitive: true));
        }

        return fields;
    }

    private static IReadOnlyList<FieldSpec> DatabaseFields(string driver)
    {
        return driver switch
        {
            "sqlite" =>
            [
                new(FieldKeys.Database, "数据库文件", Placeholder: @"C:\data\app.db"),
                new(FieldKeys.Password, "密码（可选）", Sensitive: true)
            ],
            "redis" =>
            [
                new(FieldKeys.Host, "主机 / IP"),
                new(FieldKeys.Port, "端口"),
                new(FieldKeys.Database, "DB 编号", Placeholder: "0"),
                new(FieldKeys.Username, "用户名（可选）"),
                new(FieldKeys.Password, "密码", Sensitive: true)
            ],
            "elasticsearch" =>
            [
                new(FieldKeys.Host, "主机 / IP"),
                new(FieldKeys.Port, "端口"),
                new(FieldKeys.Username, "用户名"),
                new(FieldKeys.Password, "密码", Sensitive: true)
            ],
            "oss" =>
            [
                new(FieldKeys.Host, "Endpoint", Placeholder: "oss-cn-hangzhou.aliyuncs.com"),
                new(FieldKeys.Database, "Bucket"),
                new(FieldKeys.Username, "AccessKey ID"),
                new(FieldKeys.Password, "AccessKey Secret", Sensitive: true)
            ],
            _ =>
            [
                new(FieldKeys.Host, "主机 / IP"),
                new(FieldKeys.Port, "端口"),
                new(FieldKeys.Database, "数据库名"),
                new(FieldKeys.Username, "用户名"),
                new(FieldKeys.Password, "密码", Sensitive: true)
            ]
        };
    }

    public const string LegacyConnectionFieldName = "连接字符串";

    public static void MigrateLegacyFields(VaultEntry entry)
    {
        var legacy = entry.Field(FieldKeys.Connection);
        if (legacy.Length == 0)
        {
            return;
        }

        entry.Fields.Remove(FieldKeys.Connection);
        if (legacy.Trim().Length == 0)
        {
            return;
        }

        var name = LegacyConnectionFieldName;
        for (var i = 2; entry.CustomFields.Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)); i++)
        {
            name = $"{LegacyConnectionFieldName} {i}";
        }

        entry.CustomFields.Add(new VaultCustomField { Name = name, Value = legacy.Trim(), Sensitive = true });
    }

    public static string Summary(VaultEntry entry)
    {
        switch (entry.Kind)
        {
            case VaultItemKind.Server:
            {
                var protocol = Protocol(entry.Field(FieldKeys.Protocol));
                var host = entry.Field(FieldKeys.Host).Trim();
                var port = entry.Field(FieldKeys.Port).Trim();
                return host.Length == 0 ? protocol.Name : $"{protocol.Name} · {host}{(port.Length == 0 ? "" : ":" + port)}";
            }
            case VaultItemKind.Database:
            {
                var driver = Driver(entry.Field(FieldKeys.Driver));
                var target = driver.Code == "sqlite" ? entry.Field(FieldKeys.Database) : entry.Field(FieldKeys.Host);
                return target.Trim().Length == 0 ? driver.Name : $"{driver.Name} · {target.Trim()}";
            }
            case VaultItemKind.SshKey:
            {
                var type = entry.Field(FieldKeys.KeyType);
                return type.Length == 0 ? "SSH 密钥" : type;
            }
            case VaultItemKind.ApiCredential:
                return DomainGrouping.HostOf(entry.Url) is { Length: > 0 } apiHost ? apiHost : entry.Field(FieldKeys.Environment);
            case VaultItemKind.Cluster:
            {
                var product = entry.Field(FieldKeys.Product).Trim();
                var nodes = $"{entry.Nodes.Count} 个节点";
                return product.Length == 0 ? nodes : $"{product} · {nodes}";
            }
            default:
                return DomainGrouping.HostOf(entry.Url);
        }
    }
}
