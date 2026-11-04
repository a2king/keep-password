using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class TotpView : UserControl
{
    private readonly DispatcherTimer _timer;

    public TotpView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => (DataContext as TotpPageViewModel)?.Refresh();
    }

    public void Start(VaultSession session)
    {
        DataContext = new TotpPageViewModel(session);
        _timer.Start();
    }

    public void Stop()
    {
        Pause();
        DataContext = null;
    }

    public void Pause() => _timer.Stop();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        CopyStatus.Text = "已复制。若 30 秒后剪贴板仍是该验证码，会自动清除。";
        CopyStatus.IsVisible = true;
        await SecretClipboard.CopyAsync(clipboard, code);
    }
}
