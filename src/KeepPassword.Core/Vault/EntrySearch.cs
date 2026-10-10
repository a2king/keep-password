namespace KeepPassword.Core.Vault;

public static class EntrySearch
{
    private static readonly string[] SearchableFields =
    [
        FieldKeys.Host,
        FieldKeys.Port,
        FieldKeys.Protocol,
        FieldKeys.Driver,
        FieldKeys.Database,
        FieldKeys.Environment,
        FieldKeys.Product,
        FieldKeys.KeyType,
        FieldKeys.Fingerprint
    ];

    public static IReadOnlyList<VaultEntry> Query(IEnumerable<VaultEntry> entries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries.ToList();
        }

        var needle = query.Trim();
        return entries.Where(entry => Matches(entry, needle)).ToList();
    }

    public static bool Matches(VaultEntry entry, string needle) =>
        Contains(entry.Name, needle)
        || Contains(entry.Url, needle)
        || Contains(entry.Username, needle)
        || Contains(entry.Note, needle)
        || Contains(entry.Space, needle)
        || entry.Tags.Any(tag => Contains(tag, needle))
        || Contains(VaultItemKinds.DisplayName(entry.Kind), needle)
        || SearchableFields.Any(key => Contains(entry.Field(key), needle))
        || entry.CustomFields.Any(field => Contains(field.Name, needle) || (!field.Sensitive && Contains(field.Value, needle)))
        || entry.Nodes.Any(node => Contains(node.Name, needle) || Contains(node.Role, needle) || Contains(node.Host, needle) || Contains(node.Service, needle));

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
