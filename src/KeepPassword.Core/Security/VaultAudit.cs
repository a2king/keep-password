using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Security;

public enum AuditKind
{
    Empty,
    Weak,
    Reused
}

public sealed record AuditFinding(Guid EntryId, string Name, string Domain, AuditKind Kind, string Advice);

public static class VaultAudit
{
    public static IReadOnlyList<AuditFinding> Analyze(IEnumerable<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var accounts = entries
            .Where(entry => VaultItemKinds.HasAccounts(entry.Kind))
            .SelectMany(entry => entry.Accounts.Select(account => (Entry: entry, Account: account)))
            .Where(item => !ItemTemplates.UsesKeyAuth(item.Entry, item.Account))
            .ToList();
        var reuse = accounts
            .Where(item => !string.IsNullOrEmpty(item.Account.Password))
            .GroupBy(item => item.Account.Password)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(item => (item.Account.Id, group.Count() - 1)))
            .ToDictionary(item => item.Id, item => item.Item2);

        var findings = new List<AuditFinding>();
        foreach (var (entry, account) in accounts)
        {
            if (string.IsNullOrEmpty(account.Username) && string.IsNullOrEmpty(account.Password))
            {
                continue;
            }

            var name = AccountTitle(entry, account);
            var domain = DomainGrouping.HostOf(entry.Url);
            if (string.IsNullOrEmpty(account.Password))
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Empty, "此账号没有密码。请生成并保存一个高强度密码。"));
                continue;
            }

            if (PasswordStrengthEvaluator.Evaluate(account.Password) == PasswordStrength.Weak)
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Weak, "密码强度不足。请换成更长、且包含多类字符的密码。"));
            }

            if (reuse.TryGetValue(account.Id, out var others))
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Reused, "此密码还用于其他 " + others + " 个账号。请为每个账号使用独立密码。"));
            }
        }

        return findings;
    }

    public static string AccountTitle(VaultEntry entry, VaultAccount account)
    {
        var name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name;
        return entry.Accounts.Count > 1 && account.Username.Trim().Length > 0 ? $"{name} · {account.Username.Trim()}" : name;
    }
}
