using System.Text;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Import;

public static class CsvExporter
{
    public static IReadOnlyList<string> Headers { get; } =
    [
        "类型", "条目名称", "空间分类", "账号标签", "收藏", "网址", "主机", "端口", "协议", "认证方式",
        "数据库类型", "数据库名", "服务类型", "用户名", "密码", "验证码密钥", "账号说明", "备注",
        "Client ID", "API Key", "Secret", "Token", "环境", "节点", "私钥", "公钥", "密码短语", "自定义字段"
    ];

    public static string Export(IEnumerable<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var builder = new StringBuilder();
        WriteRow(builder, Headers);
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            foreach (var account in AccountsOf(entry))
            {
                WriteRow(builder,
                [
                    VaultItemKinds.DisplayName(entry.Kind),
                    entry.Name,
                    entry.Space,
                    string.Join(";", entry.Tags),
                    entry.Favorite ? "是" : "否",
                    entry.Url,
                    entry.Field(FieldKeys.Host),
                    entry.Field(FieldKeys.Port),
                    ProtocolName(entry),
                    AuthName(entry, account),
                    DriverName(entry),
                    entry.Field(FieldKeys.Database),
                    entry.Field(FieldKeys.Product),
                    account.Username,
                    ItemTemplates.UsesKeyAuth(entry, account) ? "" : account.Password,
                    account.TotpSecret ?? "",
                    account.Note,
                    entry.Note,
                    entry.Field(FieldKeys.ClientId),
                    entry.Field(FieldKeys.ApiKey),
                    entry.Field(FieldKeys.Secret),
                    entry.Field(FieldKeys.Token),
                    entry.Field(FieldKeys.Environment),
                    NodesOf(entry),
                    entry.Field(FieldKeys.PrivateKey),
                    entry.Field(FieldKeys.PublicKey),
                    entry.Field(FieldKeys.Passphrase),
                    CustomFieldsOf(entry)
                ]);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<VaultAccount> AccountsOf(VaultEntry entry)
    {
        if (!VaultItemKinds.HasAccounts(entry.Kind))
        {
            return [new VaultAccount()];
        }

        var filled = entry.Accounts.Where(account => !account.IsBlank).ToList();
        return filled.Count > 0 ? filled : [new VaultAccount()];
    }

    private static string ProtocolName(VaultEntry entry)
    {
        if (entry.Kind != VaultItemKind.Server)
        {
            return "";
        }

        var code = entry.Field(FieldKeys.Protocol);
        return ItemTemplates.Protocols.FirstOrDefault(item => item.Code == code)?.Name ?? code;
    }

    private static string DriverName(VaultEntry entry)
    {
        if (entry.Kind != VaultItemKind.Database)
        {
            return "";
        }

        var code = entry.Field(FieldKeys.Driver);
        return ItemTemplates.Drivers.FirstOrDefault(item => item.Code == code)?.Name ?? code;
    }

    private static string AuthName(VaultEntry entry, VaultAccount account)
    {
        if (!ItemTemplates.SupportsKeyAuth(entry))
        {
            return "";
        }

        return ItemTemplates.UsesKeyAuth(entry, account) ? "密钥" : "密码";
    }

    private static string NodesOf(VaultEntry entry)
    {
        if (entry.Kind != VaultItemKind.Cluster)
        {
            return "";
        }

        return string.Join(", ", entry.Nodes
            .Where(node => node.Host.Trim().Length > 0 || node.Port.Trim().Length > 0)
            .Select(node => node.Port.Trim().Length == 0 ? node.Host.Trim() : $"{node.Host.Trim()}:{node.Port.Trim()}"));
    }

    private static string CustomFieldsOf(VaultEntry entry) =>
        string.Join(";", entry.CustomFields
            .Where(field => field.Name.Length > 0 || field.Value.Length > 0)
            .Select(field => EscapeToken(field.Name) + "=" + EscapeToken(field.Value)));

    private static string EscapeToken(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace(";", "\\;", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static void WriteRow(StringBuilder builder, IReadOnlyList<string> cells)
    {
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(Escape(cells[i]));
        }

        builder.Append("\r\n");
    }

    private static string Escape(string value)
    {
        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }
}
