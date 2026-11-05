using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class TotpView : UserControl
{
    private IDisposable? _ticker;

    public TotpView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => Pause();
    }

    public void Start(VaultSession session)
    {
        DataContext = new TotpPageViewModel(session);
        _ticker?.Dispose();
        _ticker = TotpTicker.Subscribe(_ => (DataContext as TotpPageViewModel)?.Refresh());
    }

    public void Stop()
    {
        Pause();
        DataContext = null;
    }

    public void Pause()
    {
        _ticker?.Dispose();
        _ticker = null;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string code
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        CopyStatus.Text = "已复制。若 30 秒后剪贴板仍是该验证码，会自动清除。";
        CopyStatus.IsVisible = true;
        if (await ClipboardService.CopyAsync(clipboard, code, sensitive: true))
        {
            _ = CopyFeedback.ShowAsync(button);
        }
    }
}
