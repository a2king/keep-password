namespace KeepPassword.Core.Vault;

public static class VaultItemRules
{
    public const int MaxValueLength = 65_536;
    public const int MaxCustomFields = 100;
    public const int MaxNodes = 200;

    public static string? Validate(VaultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (TooLong(entry.Name) || TooLong(entry.Url) || TooLong(entry.Username) || TooLong(entry.Password) || TooLong(entry.Note)
            || TooLong(entry.TotpSecret) || entry.Fields.Values.Any(TooLong))
        {
            return "字段内容过长。";
        }

        if (entry.CustomFields.Count > MaxCustomFields)
        {
            return $"自定义字段最多 {MaxCustomFields} 个。";
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in entry.CustomFields)
        {
            var name = field.Name.Trim();
            if (name.Length == 0)
            {
                return "请填写自定义字段名称。";
            }

            if (name.Length > VaultCustomField.MaxNameLength)
            {
                return $"自定义字段名称最多 {VaultCustomField.MaxNameLength} 个字符。";
            }

            if (!names.Add(name))
            {
                return $"自定义字段「{name}」重复。";
            }

            if (TooLong(field.Value))
            {
                return "字段内容过长。";
            }
        }

        return entry.Kind switch
        {
            VaultItemKind.Server => ValidateServer(entry),
            VaultItemKind.Database => ValidateDatabase(entry),
            VaultItemKind.SshKey => entry.Field(FieldKeys.PrivateKey).Trim().Length == 0 ? "请粘贴私钥内容。" : null,
            VaultItemKind.ApiCredential => ValidateApi(entry),
            VaultItemKind.Cluster => ValidateCluster(entry),
            _ => null
        };
    }

    public static bool IsValidPort(string? port)
    {
        var text = port?.Trim() ?? "";
        return text.Length == 0 || (int.TryParse(text, out var value) && value is >= 1 and <= 65535);
    }

    private static string? ValidateServer(VaultEntry entry)
    {
        if (entry.Field(FieldKeys.Host).Trim().Length == 0)
        {
            return "请填写服务器主机或 IP。";
        }

        if (!IsValidPort(entry.Field(FieldKeys.Port)))
        {
            return "端口必须是 1-65535 之间的数字。";
        }

        return ValidateKeyAuth(entry);
    }

    private static string? ValidateDatabase(VaultEntry entry)
    {
        var driver = ItemTemplates.Driver(entry.Field(FieldKeys.Driver)).Code;
        if (driver == "sqlite")
        {
            return entry.Field(FieldKeys.Database).Trim().Length > 0 ? null : "请填写数据库文件路径。";
        }

        if (entry.Field(FieldKeys.Host).Trim().Length == 0)
        {
            return driver == "oss" ? "请填写 Endpoint。" : "请填写数据库主机或 IP。";
        }

        return IsValidPort(entry.Field(FieldKeys.Port)) ? null : "端口必须是 1-65535 之间的数字。";
    }

    private static string? ValidateApi(VaultEntry entry)
    {
        var hasSecret = new[] { FieldKeys.ApiKey, FieldKeys.Secret, FieldKeys.Token }
            .Any(key => entry.Field(key).Trim().Length > 0);
        return hasSecret ? null : "请至少填写 API Key、Secret 或 Token 中的一项。";
    }

    private static string? ValidateCluster(VaultEntry entry)
    {
        if (entry.Nodes.Count == 0)
        {
            return "集群服务至少需要一个节点。";
        }

        if (entry.Nodes.Count > MaxNodes)
        {
            return $"集群节点最多 {MaxNodes} 个。";
        }

        for (var i = 0; i < entry.Nodes.Count; i++)
        {
            var node = entry.Nodes[i];
            if (node.Host.Trim().Length == 0)
            {
                return $"第 {i + 1} 个节点缺少主机或 IP。";
            }

            if (!IsValidPort(node.Port))
            {
                return $"第 {i + 1} 个节点的端口必须是 1-65535 之间的数字。";
            }

            if (TooLong(node.Name) || TooLong(node.Role) || TooLong(node.Host) || TooLong(node.Service))
            {
                return "字段内容过长。";
            }
        }

        return ValidateKeyAuth(entry);
    }

    private static string? ValidateKeyAuth(VaultEntry entry) =>
        ItemTemplates.UsesKeyAuth(entry) && entry.SshKeyId is null ? "请选择要关联的 SSH 密钥。" : null;

    private static bool TooLong(string? value) => value is not null && value.Length > MaxValueLength;
}
