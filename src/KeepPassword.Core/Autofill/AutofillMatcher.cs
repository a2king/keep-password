using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Autofill;

public static class AutofillMatcher
{
    public static IReadOnlyList<AutofillCandidate> Match(IEnumerable<VaultEntry> entries, AutofillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var desktop = request.Origin == AutofillOrigin.WindowsApplication;
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
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal static bool IsEligible(VaultEntry entry, VaultAccount account, AutofillOrigin origin) =>
        (origin == AutofillOrigin.WindowsApplication ? VaultItemKinds.HasAccounts(entry.Kind) : entry.Kind == VaultItemKind.Login)
        && !ItemTemplates.UsesKeyAuth(entry, account)
        && !string.IsNullOrEmpty(account.Password);
}
