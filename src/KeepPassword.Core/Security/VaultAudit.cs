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
        var list = entries.ToList();
        var findings = new List<AuditFinding>();
        var reuse = list
            .Where(entry => !string.IsNullOrEmpty(entry.Password))
            .GroupBy(entry => entry.Password)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.Select(entry => (entry.Id, group.Count() - 1)))
            .ToDictionary(item => item.Id, item => item.Item2);

        foreach (var entry in list)
        {
            if (string.IsNullOrEmpty(entry.Username) && string.IsNullOrEmpty(entry.Password) && string.IsNullOrWhiteSpace(entry.Url))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name;
            var domain = DomainGrouping.HostOf(entry.Url);
            if (string.IsNullOrEmpty(entry.Password))
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Empty, "此登录没有密码。请生成并保存一个高强度密码。"));
                continue;
            }

            if (PasswordStrengthEvaluator.Evaluate(entry.Password) == PasswordStrength.Weak)
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Weak, "密码强度不足。请换成更长、且包含多类字符的密码。"));
            }

            if (reuse.TryGetValue(entry.Id, out var others))
            {
                findings.Add(new AuditFinding(entry.Id, name, domain, AuditKind.Reused, "此密码还用于其他 " + others + " 个条目。请为每个站点使用独立密码。"));
            }
        }

        return findings;
    }
}
