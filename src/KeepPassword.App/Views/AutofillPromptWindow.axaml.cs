using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class AutofillPromptWindow : Window
{
    private readonly Func<string, bool>? _verifyShortKey;

    public AutofillDecision? Decision { get; private set; }

    public AutofillPromptWindow() => InitializeComponent();

    public AutofillPromptWindow(AutofillRequest request, IReadOnlyList<AutofillCandidate> matches, Func<string, bool> verifyShortKey)
        : this()
    {
        _verifyShortKey = verifyShortKey;
        var context = string.IsNullOrWhiteSpace(request.Url) ? request.WindowTitle : request.Url;
        ContextText.Text = string.IsNullOrWhiteSpace(context) ? "来源：未知页面" : "来源：" + context;
        var host = DomainGrouping.HostOf(request.Url);
        var site = DomainGrouping.TryRegistrableDomain(host, out var registrable) ? registrable : "无法识别的域名";
        var process = string.IsNullOrWhiteSpace(request.TargetProcess) ? "" : "，进程 " + request.TargetProcess;
        TargetText.Text = "目标站点：" + site + process;
        MatchList.ItemsSource = matches;
        MatchList.SelectionChanged += (_, _) => UpdateEntry(request);
        if (matches.Count > 0)
        {
            MatchList.SelectedIndex = 0;
        }

        UpdateEntry(request);
    }

    private void UpdateEntry(AutofillRequest request)
    {
        if (MatchList.SelectedItem is not AutofillCandidate selected)
        {
            EntryText.Text = "尚未选择条目。";
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

        Decision = new AutofillDecision { EntryId = selected.Id, ShortKey = shortKey };
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }
}
