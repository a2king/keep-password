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
        _timer.Stop();
        DataContext = null;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await SecretClipboard.CopyAsync(clipboard, code);
    }
}
