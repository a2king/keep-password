using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class TotpWindow : Window
{
    private readonly DispatcherTimer _timer;

    public TotpWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => (DataContext as TotpPageViewModel)?.Refresh();
        Closed += (_, _) => _timer.Stop();
    }

    public TotpWindow(VaultSession session)
        : this()
    {
        DataContext = new TotpPageViewModel(session);
        _timer.Start();
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string code } || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(code);
    }
}
