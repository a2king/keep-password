namespace KeepPassword.Core.Vault;

public sealed class EntryGroup
{
    public EntryGroup(string domain, IReadOnlyList<VaultEntry> entries)
    {
        Domain = domain;
        Entries = entries;
    }

    public string Domain { get; }

    public IReadOnlyList<VaultEntry> Entries { get; }
}

public static class DomainGrouping
{
    public const string Uncategorized = "（无域名）";

    public static string HostOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        var text = url.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "https://" + text;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.IdnHost))
        {
            return uri.IdnHost.ToLowerInvariant();
        }

        return "";
    }

    public static bool HostsMatch(string entryHost, string pageHost)
    {
        if (entryHost.Length == 0 || pageHost.Length == 0)
        {
            return false;
        }

        if (entryHost.Equals(pageHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (pageHost.EndsWith("." + entryHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return entryHost.EndsWith("." + pageHost, StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<EntryGroup> Group(IEnumerable<VaultEntry> entries)
    {
        return entries
            .GroupBy(entry =>
            {
                var host = HostOf(entry.Url);
                return host.Length == 0 ? Uncategorized : host;
            })
            .OrderBy(group => group.Key == Uncategorized ? 1 : 0)
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new EntryGroup(
                group.Key,
                group.OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
    }
}
