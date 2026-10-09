using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Autofill;

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
        MatchList.ItemsSource = matches;
        if (matches.Count > 0)
        {
            MatchList.SelectedIndex = 0;
        }
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
        ErrorText.IsVisible = true;
    }
}
