using KeepPassword.Core.Security;

namespace KeepPassword.App.Views;

public sealed class AuditView : FindingsView
{
    public AuditView()
        : base("密码安全审计", "自动检测空密码、弱密码及跨站点重复使用的密码风险。")
    {
        AddAction("重新检测", accent: false, (_, _) => Rerun?.Invoke());
    }

    public Action? Rerun { get; set; }

    public void Show(IReadOnlyList<AuditFinding> findings)
    {
        ClearBody();
        if (findings.Count == 0)
        {
            AddSummary("安全状态良好", "当前保险库中所有条目均未发现空密码、常见弱密码或跨站重复使用的密码风险。", danger: false);
            return;
        }

        AddSummary($"检测到 {findings.Count} 项安全风险", "建议尽快更新以下条目的密码。", danger: true);
        AddFindings(findings.Select(finding => (finding.Name, finding.Domain, finding.Advice)));
    }

    public void Clear() => ClearBody();
}
