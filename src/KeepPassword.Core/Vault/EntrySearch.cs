namespace KeepPassword.Core.Vault;

public static class EntrySearch
{
    public static IReadOnlyList<VaultEntry> Query(IEnumerable<VaultEntry> entries, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return entries.ToList();
        }

        var needle = query.Trim();
        return entries.Where(entry =>
                Contains(entry.Name, needle)
                || Contains(entry.Url, needle)
                || Contains(entry.Username, needle)
                || Contains(entry.Note, needle))
            .ToList();
    }

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
