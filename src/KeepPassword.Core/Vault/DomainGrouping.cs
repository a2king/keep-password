using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

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

public static partial class DomainGrouping
{
    public const string Uncategorized = "（无域名）";

    private static readonly HashSet<string> MultiPartSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "com.au", "net.au", "org.au", "edu.au",
        "co.jp", "or.jp", "ne.jp", "ac.jp", "com.cn", "net.cn", "org.cn", "gov.cn",
        "com.hk", "com.tw", "com.sg", "co.kr", "co.nz", "com.br", "com.mx", "com.ar",
        "co.za", "com.tr", "co.in", "com.co"
    };

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

    public static bool TryRegistrableDomain(string? host, out string registrable)
    {
        registrable = "";
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || normalized.Length > 253 || normalized.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(normalized, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
        {
            registrable = normalized;
            return true;
        }

        var labels = normalized.Split('.');
        if (labels.Length < 2 || labels.Any(label => !IsLabel(label)))
        {
            return false;
        }

        var suffixLength = 1;
        var lastTwo = labels[^2] + "." + labels[^1];
        if (MultiPartSuffixes.Contains(lastTwo))
        {
            suffixLength = 2;
        }

        if (labels.Length <= suffixLength)
        {
            return false;
        }

        registrable = string.Join('.', labels[^(suffixLength + 1)..]);
        return true;
    }

    public static bool HostsMatch(string entryHost, string pageHost)
    {
        return TryRegistrableDomain(entryHost, out var entrySite)
            && TryRegistrableDomain(pageHost, out var pageSite)
            && entrySite.Equals(pageSite, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TitleContainsHost(string? title, string host)
    {
        if (string.IsNullOrWhiteSpace(title) || !TryRegistrableDomain(host, out var entrySite))
        {
            return false;
        }

        foreach (Match match in HostToken().Matches(title))
        {
            var token = match.Value.Trim('.');
            if (TryRegistrableDomain(token, out var site) && site.Equals(entrySite, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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

    private static bool IsLabel(string label)
    {
        if (label.Length is < 1 or > 63 || label.StartsWith('-') || label.EndsWith('-'))
        {
            return false;
        }

        return label.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-');
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9-])(?:[A-Za-z0-9-]+\.)+[A-Za-z]{2,}(?![A-Za-z0-9-])", RegexOptions.CultureInvariant)]
    private static partial Regex HostToken();
}
