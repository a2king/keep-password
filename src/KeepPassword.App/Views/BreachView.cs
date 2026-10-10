using Avalonia.Controls;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Views;

public sealed class BreachView : FindingsView
{
    private readonly Button _start;
    private int _run;

    public BreachView()
        : base("公开泄露检查", "通过 k-Anonymity 查询密码是否出现在已知的公开泄露数据中。")
    {
        _start = AddAction("开始检查", accent: true, async (_, _) => await RunAsync());
        Reset();
    }

    public Func<CancellationToken, Task<IReadOnlyList<BreachFinding>>>? Checker { get; set; }

    public void Reset()
    {
        _run++;
        _start.IsEnabled = true;
        _start.Content = "开始检查";
        ClearBody();
        AddNotice(BreachChecker.PrivacyNotice);
    }

    private async Task RunAsync()
    {
        if (Checker is null)
        {
            return;
        }

        var run = ++_run;
        _start.IsEnabled = false;
        _start.Content = "正在检查…";
        ClearBody();
        AddNotice(BreachChecker.PrivacyNotice);
        try
        {
            var findings = await Checker(CancellationToken.None);
            if (run != _run)
            {
                return;
            }

            ClearBody();
            AddNotice(BreachChecker.PrivacyNotice);
            if (findings.Count == 0)
            {
                AddSummary("未在已知泄露库中发现匹配", "已检查的所有条目密码均未在公开泄露数据中发现。", danger: false);
            }
            else
            {
                AddSummary($"发现 {findings.Count} 个密码已在公开数据中泄露", "建议立即前往对应网站更换密码。", danger: true);
                AddFindings(findings.Select(finding => (finding.Name, finding.Domain, finding.Advice)));
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            if (run == _run)
            {
                AddSummary("泄露检查失败", "网络查询没有完成。密码明文和完整哈希都没有上传。", danger: true);
            }
        }
        finally
        {
            if (run == _run)
            {
                _start.IsEnabled = true;
                _start.Content = "重新检查";
            }
        }
    }
}
