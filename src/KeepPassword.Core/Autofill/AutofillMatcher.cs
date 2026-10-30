using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

public static class AutofillMatcher
{
    public static IReadOnlyList<AutofillCandidate> Catalog(IEnumerable<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var matches = new List<AutofillCandidate>();
        foreach (var entry in entries)
        {
            if (entry.Kind != VaultItemKind.Login)
            {
                continue;
            }

            var host = DomainGrouping.HostOf(entry.Url);
            if (!DomainGrouping.TryRegistrableDomain(host, out _))
            {
                continue;
            }

            foreach (var account in entry.Accounts)
            {
                if (!IsEligible(entry, account, AutofillOrigin.Browser))
                {
                    continue;
                }

                matches.Add(new AutofillCandidate
                {
                    Id = entry.Id,
                    AccountId = account.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? (host.Length > 0 ? host : "未命名") : entry.Name,
                    Username = account.Username,
                    Domain = host
                });
            }
        }

        return matches.OrderBy(match => match.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IReadOnlyList<AutofillCandidate> MatchCatalog(
        IEnumerable<AutofillCandidate> candidates,
        AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(request);
        if (request.Origin != AutofillOrigin.Browser)
        {
            return [];
        }

        var pageHost = DomainGrouping.HostOf(request.Url);
        if (pageHost.Length == 0)
        {
            return [];
        }

        return candidates
            .Where(candidate => DomainGrouping.HostsMatch(candidate.Domain, pageHost))
            .OrderByDescending(match => AddressSimilarity(match.Domain, pageHost))
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<AutofillCandidate> Match(IEnumerable<VaultEntry> entries, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var desktop = IsDesktopOrigin(request.Origin);
        var pageHost = DomainGrouping.HostOf(request.Url);
        var title = request.WindowTitle ?? "";
        var matches = new List<AutofillCandidate>();
        foreach (var entry in entries)
        {
            if (desktop ? !VaultItemKinds.HasAccounts(entry.Kind) : entry.Kind != VaultItemKind.Login)
            {
                continue;
            }

            var host = DomainGrouping.HostOf(entry.Kind is VaultItemKind.Server or VaultItemKind.Database
                ? entry.Field(FieldKeys.Host) : entry.Url);

            var urlHit = pageHost.Length > 0 && DomainGrouping.HostsMatch(host, pageHost);
            // A desktop window title is not a verified service address. Let the user
            // explicitly choose a password account; browsers must keep domain matching.
            if (!desktop && !urlHit)
            {
                continue;
            }

            foreach (var account in entry.Accounts)
            {
                if (!IsEligible(entry, account, request.Origin))
                {
                    continue;
                }

                matches.Add(new AutofillCandidate
                {
                    Id = entry.Id,
                    AccountId = account.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? (host.Length > 0 ? host : "未命名") : entry.Name,
                    Username = account.Username,
                    AccountNote = account.Note.Trim(),
                    Domain = host,
                    KindName = VaultItemKinds.DisplayName(entry.Kind)
                });
            }
        }

        return matches
            .OrderByDescending(match => desktop && DomainGrouping.TitleContainsHost(title, match.Domain))
            .ThenByDescending(match => AddressSimilarity(match.Domain, pageHost))
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool IsDesktopOrigin(AutofillOrigin origin) =>
        origin is AutofillOrigin.WindowsApplication or AutofillOrigin.MacOsApplication;

    /// <summary>
    /// 主机名越接近当前页面，分数越高。完全相同最高，其次是相差更少的上级或子域。
    /// </summary>
    internal static int AddressSimilarity(string? entryHost, string? pageHost)
    {
        var entry = HostLabels(entryHost);
        var page = HostLabels(pageHost);
        if (entry.Length == 0 || page.Length == 0)
        {
            return 0;
        }

        var shared = 0;
        while (shared < entry.Length && shared < page.Length
            && string.Equals(entry[entry.Length - 1 - shared], page[page.Length - 1 - shared], StringComparison.Ordinal))
        {
            shared++;
        }

        var leftover = (entry.Length - shared) + (page.Length - shared);
        return shared * 1000 - leftover * 10 - Math.Abs(entry.Length - page.Length);
    }

    private static string[] HostLabels(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return [];
        }

        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.StartsWith("www.", StringComparison.Ordinal) && normalized.Length > 4)
        {
            normalized = normalized[4..];
        }

        return normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
    }

    internal static bool IsEligible(VaultEntry entry, VaultAccount account, AutofillOrigin origin) =>
        (IsDesktopOrigin(origin) ? VaultItemKinds.HasAccounts(entry.Kind) : entry.Kind == VaultItemKind.Login)
        && !ItemTemplates.UsesKeyAuth(entry, account)
        && !string.IsNullOrEmpty(account.Password);
}
