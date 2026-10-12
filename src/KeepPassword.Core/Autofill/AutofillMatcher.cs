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
            if (entry.Kind != VaultItemKind.Login)
            {
                continue;
            }

            var host = DomainGrouping.HostOf(entry.Url);
            if (host.Length == 0)
            {
                continue;
            }

            var urlHit = pageHost.Length > 0 && DomainGrouping.HostsMatch(host, pageHost);
            var titleHit = pageHost.Length == 0 && DomainGrouping.TitleContainsHost(title, host);
            if (!urlHit && !titleHit)
            {
                continue;
            }

            foreach (var account in entry.Accounts)
            {
                if (string.IsNullOrEmpty(account.Username) && string.IsNullOrEmpty(account.Password))
                {
                    continue;
                }

                matches.Add(new AutofillCandidate
                {
                    Id = entry.Id,
                    AccountId = account.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? host : entry.Name,
                    Username = account.Username,
                    AccountNote = account.Note.Trim(),
                    Domain = host
                });
            }
        }

        return matches
            .OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
