using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

public static class AutofillMatcher
{
    public static IReadOnlyList<AutofillCandidate> Match(IEnumerable<VaultEntry> entries, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var pageHost = DomainGrouping.HostOf(request.Url);
        var title = request.WindowTitle ?? "";
        var matches = new List<AutofillCandidate>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Username) && string.IsNullOrEmpty(entry.Password))
            {
                continue;
            }

            var host = DomainGrouping.HostOf(entry.Url);
            if (host.Length == 0)
            {
                continue;
            }

            var urlHit = pageHost.Length > 0 && DomainGrouping.HostsMatch(host, pageHost);
            var titleHit = title.Length > 0 && (
                title.Contains(host, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(entry.Url) && title.Contains(entry.Url.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (!urlHit && !titleHit)
            {
                continue;
            }

            matches.Add(new AutofillCandidate
            {
                Id = entry.Id,
                Name = string.IsNullOrWhiteSpace(entry.Name) ? host : entry.Name,
                Username = entry.Username,
                Domain = host
            });
        }

        return matches
            .OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
