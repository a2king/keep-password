using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UnlockWindow : Window
{
    private readonly TaskCompletionSource<VaultSession?> _done = new();

    public UnlockWindow()
    {
        InitializeComponent();
    }

    public UnlockWindow(VaultStore store, VaultLocation location)
        : this()
    {
        DataContext = new UnlockViewModel(store, location.VaultFile);
        KeyDown += OnKeyDown;
    }

    public UnlockWindow(VaultSession softSession)
        : this()
    {
        DataContext = new UnlockViewModel(softSession);
        KeyDown += OnKeyDown;
    }

    public Task<VaultSession?> WaitAsync()
    {
        Closed += (_, _) => _done.TrySetResult(View.Session);
        Show();
        return _done.Task;
    }

    private UnlockViewModel View => (UnlockViewModel)DataContext!;

    private async void OnSubmit(object? sender, RoutedEventArgs e) => await SubmitAsync();

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        CopyPasswords();
        if (await View.SubmitAsync())
        {
            Close();
        }
    }

    private void CopyPasswords()
    {
        View.MasterPassword = MasterBox?.Text ?? "";
        View.ConfirmMaster = ConfirmMasterBox?.Text ?? "";
        if (View.IsSoftUnlock)
        {
            View.ShortKey = ShortBoxSoft?.Text ?? "";
            View.ConfirmShort = "";
            return;
        }

        View.ShortKey = ShortBoxCreate?.Text ?? "";
        View.ConfirmShort = ConfirmShortBox?.Text ?? "";
    }
}
