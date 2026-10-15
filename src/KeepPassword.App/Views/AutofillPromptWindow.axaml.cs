using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class AutofillPromptWindow : Window
{
    private readonly Func<string, bool>? _verifyShortKey;
    private IReadOnlyList<AutofillCandidate> _matches = [];

    public AutofillDecision? Decision { get; private set; }

    public AutofillPromptWindow()
    {
        InitializeComponent();
        Motion.AttachWindow(this);
    }

    public AutofillPromptWindow(AutofillRequest request, IReadOnlyList<AutofillCandidate> matches, Func<string, bool> verifyShortKey)
        : this()
    {
        _verifyShortKey = verifyShortKey;
        _matches = matches;
        var desktop = request.Origin == AutofillOrigin.WindowsApplication;
        var context = string.IsNullOrWhiteSpace(request.Url) ? request.WindowTitle : request.Url;
        ContextText.Text = string.IsNullOrWhiteSpace(context) ? "来源：未知页面" : "来源：" + context;
        var host = DomainGrouping.HostOf(request.Url);
        var site = DomainGrouping.TryRegistrableDomain(host, out var registrable) ? registrable : "无法识别的域名";
        var process = string.IsNullOrWhiteSpace(request.TargetProcess) ? "" : "，进程 " + request.TargetProcess;
        TargetText.Text = desktop
            ? "目标程序：" + (request.TargetProcess ?? "未知程序") + (request.PasswordOnly ? "；本次仅填密码。" : "；本次填入用户名和密码。")
            : "目标站点：" + site + process;
        GuidanceText.Text = desktop
            ? "程序窗口无法验证服务地址。请自行核对当前连接或登录页面，选择账号并输入短密钥。"
            : "确认目标与条目来源一致后输入短密钥以授权填入。";
        SearchBox.TextChanged += (_, _) => FilterMatches();
        MatchList.ItemsSource = _matches;
        MatchList.SelectionChanged += (_, _) => UpdateEntry(request);
        if (!desktop && matches.Count > 0)
        {
            MatchList.SelectedIndex = 0;
        }

        UpdateEntry(request);
    }

    private void FilterMatches()
    {
        var selected = MatchList.SelectedItem as AutofillCandidate;
        var query = SearchBox.Text?.Trim() ?? "";
        var visible = _matches.Where(item => query.Length == 0
            || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Username.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.Domain.Contains(query, StringComparison.OrdinalIgnoreCase)
            || item.KindName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        MatchList.ItemsSource = visible;
        MatchList.SelectedItem = selected is not null && visible.Contains(selected) ? selected : null;
    }

    private void UpdateEntry(AutofillRequest request)
    {
        if (MatchList.SelectedItem is not AutofillCandidate selected)
        {
            EntryText.Text = "尚未选择条目。";
            return;
        }

        if (request.Origin == AutofillOrigin.WindowsApplication)
        {
            var address = selected.Domain.Length > 0 ? selected.Domain : "未设置主机地址";
            EntryText.Text = $"所选：{selected.KindName} · {address}。请确认是当前目标使用的账号。";
            return;
        }

        var page = DomainGrouping.HostOf(request.Url);
        var same = page.Length == 0
            ? DomainGrouping.TitleContainsHost(request.WindowTitle, selected.Domain)
            : DomainGrouping.HostsMatch(selected.Domain, page);
        EntryText.Text = same
            ? "条目域名 " + selected.Domain + " 与当前目标匹配。"
            : "条目域名 " + selected.Domain + " 与当前目标不一致，请勿填入。";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private void OnFill(object? sender, RoutedEventArgs e)
    {
        if (_verifyShortKey is null)
        {
            Close();
            return;
        }

        if (MatchList.SelectedItem is not AutofillCandidate selected)
        {
            ShowError("请选择一条记录。");
            return;
        }

        var shortKey = ShortKeyBox.Text ?? "";
        if (!_verifyShortKey(shortKey))
        {
            ShowError("短密钥不正确。");
            ShortKeyBox.Text = "";
            return;
        }

        Decision = new AutofillDecision { EntryId = selected.Id, AccountId = selected.AccountId, ShortKey = shortKey };
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }
}
