using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Import;

public sealed record ImportTarget(string Code, string Label);

public sealed record ImportRowResult(int RowNumber, VaultEntry? Entry, string? Error);

public sealed class ImportPlan
{
    public required IReadOnlyList<ImportRowResult> Rows { get; init; }

    public required int Ignored { get; init; }

    public IReadOnlyList<VaultEntry> Entries => Rows.Where(row => row.Entry is not null).Select(row => row.Entry!).ToList();

    public int Valid => Rows.Count(row => row.Entry is not null);

    public int Invalid => Rows.Count(row => row.Error is not null);
}

public static class CsvMapper
{
    public const string Ignore = "ignore";
    public const string Name = "name";
    public const string Note = "note";
    public const string Space = "space";
    public const string Tags = "tags";
    public const string Favorite = "favorite";
    public const string Nodes = "nodes";
    public const string CustomText = "custom";
    public const string CustomSecret = "customSecret";
    public const string FieldPrefix = "field:";

    public static IReadOnlyList<VaultItemKind> ImportableKinds { get; } =
    [
        VaultItemKind.Login,
        VaultItemKind.Server,
        VaultItemKind.Database,
        VaultItemKind.ApiCredential,
        VaultItemKind.Cluster
    ];

    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        [Name] = ["name", "title", "名称", "标题", "条目名称"],
        [Note] = ["note", "notes", "备注", "说明", "comment"],
        [Space] = ["space", "vault", "folder", "空间", "空间分类", "分组", "文件夹"],
        [Tags] = ["tags", "tag", "标签", "账号标签"],
        [Favorite] = ["favorite", "favourite", "收藏"],
        [Nodes] = ["nodes", "节点", "节点地址", "hosts"],
        [FieldPrefix + FieldKeys.Url] = ["url", "website", "login_uri", "uri", "网址", "地址", "服务地址", "endpoint url"],
        [FieldPrefix + FieldKeys.Username] = ["username", "user", "login", "login_username", "account", "用户名", "账号", "账户", "accesskey id", "client id"],
        [FieldPrefix + FieldKeys.Password] = ["password", "login_password", "pass", "密码", "accesskey secret"],
        [FieldPrefix + FieldKeys.Totp] = ["otpauth", "otp", "totp", "onetimepassword", "one-time password", "login_totp", "验证码"],
        [FieldPrefix + FieldKeys.Host] = ["host", "hostname", "server", "ip", "主机", "服务器", "endpoint"],
        [FieldPrefix + FieldKeys.Port] = ["port", "端口"],
        [FieldPrefix + FieldKeys.Protocol] = ["protocol", "协议"],
        [FieldPrefix + FieldKeys.Driver] = ["driver", "type", "database type", "数据库类型"],
        [FieldPrefix + FieldKeys.Database] = ["database", "db", "dbname", "bucket", "数据库", "数据库名"],
        [FieldPrefix + FieldKeys.Connection] = ["connection", "connection string", "connectionstring", "连接字符串"],
        [FieldPrefix + FieldKeys.ApiKey] = ["api key", "apikey", "api_key", "key"],
        [FieldPrefix + FieldKeys.Secret] = ["secret", "client secret", "secret key"],
        [FieldPrefix + FieldKeys.Token] = ["token", "access token", "bearer"],
        [FieldPrefix + FieldKeys.Environment] = ["environment", "env", "环境"],
        [FieldPrefix + FieldKeys.Product] = ["product", "service", "服务类型"]
    };

    public static IReadOnlyList<ImportTarget> Targets(VaultItemKind kind)
    {
        var template = Template(kind);
        var targets = new List<ImportTarget>
        {
            new(Ignore, "忽略此列"),
            new(Name, "条目名称")
        };

        if (kind == VaultItemKind.Server)
        {
            targets.Add(new ImportTarget(FieldPrefix + FieldKeys.Protocol, "协议"));
        }

        if (kind == VaultItemKind.Database)
        {
            targets.Add(new ImportTarget(FieldPrefix + FieldKeys.Driver, "数据库类型"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in ItemTemplates.Fields(template).Concat(ExtraFields(kind)))
        {
            if (seen.Add(spec.Key))
            {
                targets.Add(new ImportTarget(FieldPrefix + spec.Key, spec.Label));
            }
        }

        if (kind == VaultItemKind.Cluster)
        {
            targets.Add(new ImportTarget(Nodes, "节点地址（逗号分隔 host:port）"));
        }

        targets.Add(new ImportTarget(Note, "备注"));
        targets.Add(new ImportTarget(Space, "空间分类"));
        targets.Add(new ImportTarget(Tags, "账号标签（逗号或分号分隔）"));
        targets.Add(new ImportTarget(Favorite, "收藏（是/否）"));
        targets.Add(new ImportTarget(CustomText, "自定义文本字段（以列名为字段名）"));
        targets.Add(new ImportTarget(CustomSecret, "自定义敏感字段（以列名为字段名）"));
        return targets;
    }

    public static string Guess(string header, VaultItemKind kind)
    {
        var normalized = header.Trim().Replace('_', ' ').ToLowerInvariant();
        var available = Targets(kind).Select(target => target.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var (code, aliases) in Aliases)
        {
            if (available.Contains(code) && aliases.Any(alias => alias.Replace('_', ' ') == normalized))
            {
                return code;
            }
        }

        return normalized.Length == 0 ? Ignore : CustomText;
    }

    public static IReadOnlyList<string> GuessAll(IReadOnlyList<string> headers, VaultItemKind kind)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var header in headers)
        {
            var guess = Guess(header, kind);
            if (!IsRepeatable(guess) && !used.Add(guess))
            {
                guess = CustomText;
            }

            result.Add(guess);
        }

        return result;
    }

    public static string? ValidateMapping(IReadOnlyList<string> headers, IReadOnlyList<string> targets)
    {
        if (targets.Count != headers.Count)
        {
            return "列映射数量与表头不一致。";
        }

        var used = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            if (!IsRepeatable(target) && !used.Add(target))
            {
                return $"第 {i + 1} 列与其他列映射到了同一个字段。";
            }

            if (target is CustomText or CustomSecret && headers[i].Trim().Length == 0)
            {
                return $"第 {i + 1} 列没有列名，无法作为自定义字段。";
            }
        }

        var customNames = headers.Where((_, i) => targets[i] is CustomText or CustomSecret).Select(name => name.Trim());
        if (customNames.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
        {
            return $"自定义字段「{duplicate.Key}」重复。";
        }

        return used.Count == 0 && !targets.Any(target => target is CustomText or CustomSecret) ? "请至少映射一列。" : null;
    }

    public static ImportPlan Build(CsvTable table, VaultItemKind kind, IReadOnlyList<string> targets, IReadOnlySet<int> ignoredRows)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!ImportableKinds.Contains(kind))
        {
            throw new ArgumentException("此条目类型不支持 CSV 导入。", nameof(kind));
        }

        if (ValidateMapping(table.Headers, targets) is { } error)
        {
            throw new ArgumentException(error, nameof(targets));
        }

        var rows = new List<ImportRowResult>();
        var ignored = 0;
        for (var i = 0; i < table.Rows.Count; i++)
        {
            if (ignoredRows.Contains(i))
            {
                ignored++;
                continue;
            }

            rows.Add(BuildRow(table, kind, targets, i));
        }

        return new ImportPlan { Rows = rows, Ignored = ignored };
    }

    private static ImportRowResult BuildRow(CsvTable table, VaultItemKind kind, IReadOnlyList<string> targets, int index)
    {
        var row = table.Rows[index];
        var entry = new VaultEntry { Id = Guid.NewGuid(), Kind = kind };
        for (var column = 0; column < targets.Count; column++)
        {
            var value = column < row.Count ? row[column] : "";
            var target = targets[column];
            if (target == Ignore || (value.Length == 0 && target is not (CustomText or CustomSecret)))
            {
                continue;
            }

            if (Apply(entry, target, table.Headers[column].Trim(), value) is { } fieldError)
            {
                return new ImportRowResult(index + 2, null, fieldError);
            }
        }

        entry.CustomFields.RemoveAll(field => field.Value.Length == 0);
        ItemTemplates.ApplyDefaults(entry);
        if (entry.Name.Trim().Length == 0)
        {
            entry.Name = FallbackName(entry);
        }

        if (entry.Name.Length == 0)
        {
            return new ImportRowResult(index + 2, null, "缺少条目名称。");
        }

        if (kind == VaultItemKind.Login && entry.Url.Length == 0 && entry.Username.Length == 0 && entry.Password.Length == 0 && entry.TotpSecret is null)
        {
            return new ImportRowResult(index + 2, null, "网址、用户名、密码和验证码都为空。");
        }

        var error = VaultItemRules.Validate(entry);
        return error is null ? new ImportRowResult(index + 2, entry, null) : new ImportRowResult(index + 2, null, error);
    }

    private static string? Apply(VaultEntry entry, string target, string header, string value)
    {
        switch (target)
        {
            case Name:
                entry.Name = value.Trim();
                return null;
            case Note:
                entry.Note = value;
                return null;
            case Space:
                entry.Space = LabelName.Normalize(value);
                return null;
            case Tags:
                entry.Tags = LabelName.NormalizeAll(value.Split([',', ';', '，', '；', '|'], StringSplitOptions.RemoveEmptyEntries));
                return null;
            case Favorite:
                entry.Favorite = value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "y" or "是" or "收藏";
                return null;
            case Nodes:
                entry.Nodes = value.Split([',', ';', '，', '；', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseNode)
                    .ToList();
                return null;
            case CustomText or CustomSecret:
                entry.CustomFields.Add(new VaultCustomField { Name = header, Value = value, Sensitive = target == CustomSecret });
                return null;
        }

        if (!target.StartsWith(FieldPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var key = target[FieldPrefix.Length..];
        switch (key)
        {
            case FieldKeys.Protocol:
            {
                var protocol = ItemTemplates.Protocols.FirstOrDefault(item => Same(item.Code, value) || Same(item.Name, value));
                if (protocol is null)
                {
                    return $"无法识别的协议「{value.Trim()}」。";
                }

                entry.SetField(FieldKeys.Protocol, protocol.Code);
                if (entry.Field(FieldKeys.Port).Length == 0)
                {
                    entry.SetField(FieldKeys.Port, protocol.DefaultPort);
                }

                return null;
            }
            case FieldKeys.Driver:
            {
                var driver = ItemTemplates.Drivers.FirstOrDefault(item => Same(item.Code, value) || Same(item.Name, value));
                if (driver is null)
                {
                    return $"无法识别的数据库类型「{value.Trim()}」。";
                }

                entry.SetField(FieldKeys.Driver, driver.Code);
                if (entry.Field(FieldKeys.Port).Length == 0)
                {
                    entry.SetField(FieldKeys.Port, driver.DefaultPort);
                }

                return null;
            }
            case FieldKeys.Totp:
                entry.TotpSecret = CsvImporter.TotpSecret(value);
                return null;
            case FieldKeys.Port:
                entry.SetField(FieldKeys.Port, value.Trim());
                return null;
            default:
                FieldKeys.Write(entry, key, value);
                return null;
        }
    }

    private static ClusterNode ParseNode(string text)
    {
        var separator = text.LastIndexOf(':');
        if (separator > 0 && separator < text.Length - 1 && !text[..separator].Contains(':'))
        {
            return new ClusterNode { Host = text[..separator], Port = text[(separator + 1)..] };
        }

        return new ClusterNode { Host = text };
    }

    private static string FallbackName(VaultEntry entry)
    {
        var host = DomainGrouping.HostOf(entry.Url);
        if (host.Length > 0)
        {
            return host;
        }

        var target = entry.Field(FieldKeys.Host).Trim();
        if (target.Length > 0)
        {
            return target;
        }

        return entry.Username.Trim();
    }

    private static IEnumerable<FieldSpec> ExtraFields(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Server or VaultItemKind.Cluster => [new FieldSpec(FieldKeys.Password, "密码", Sensitive: true)],
        _ => []
    };

    private static VaultEntry Template(VaultItemKind kind)
    {
        var entry = new VaultEntry { Kind = kind };
        ItemTemplates.ApplyDefaults(entry);
        if (kind == VaultItemKind.Database)
        {
            entry.SetField(FieldKeys.Driver, "mysql");
        }

        return entry;
    }

    private static bool IsRepeatable(string target) => target is Ignore or CustomText or CustomSecret;

    private static bool Same(string left, string right) =>
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
