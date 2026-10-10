using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UnlockView : UserControl
{
    public event Action<VaultSession>? Unlocked;

    public UnlockView()
    {
        InitializeComponent();
    }

    public void Bind(VaultStore store, string vaultFile)
    {
        ClearSecrets();
        DataContext = new UnlockViewModel(store, vaultFile);
    }

    public void FocusMaster()
    {
        Dispatcher.UIThread.Post(() => MasterBox.Focus(), DispatcherPriority.Input);
    }

    public void ClearSecrets()
    {
        MasterBox.Text = "";
        ConfirmMasterBox.Text = "";
        ShortBox.Text = "";
        ConfirmShortBox.Text = "";
        Answer1Box.Text = "";
        Answer2Box.Text = "";
        Answer3Box.Text = "";
        RecoverAnswer1Box.Text = "";
        RecoverAnswer2Box.Text = "";
        RecoverAnswer3Box.Text = "";
        RecoverConfirmBox.Text = "";
        DataContext = null;
    }

    private UnlockViewModel? View => DataContext as UnlockViewModel;

    private async void OnSubmit(object? sender, RoutedEventArgs e) => await SubmitAsync();

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitAsync();
        }
    }

    private async Task SubmitAsync()
    {
        if (View is null)
        {
            return;
        }

        CopyPasswords();
        if (!await View.SubmitAsync() || View.Session is not { } session)
        {
            return;
        }

        Unlocked?.Invoke(session);
    }

    private void OnBeginRecovery(object? sender, RoutedEventArgs e) => View?.BeginRecovery();

    private void OnCancelRecovery(object? sender, RoutedEventArgs e) => View?.CancelRecovery();

    private void CopyPasswords()
    {
        if (View is null)
        {
            return;
        }

        View.MasterPassword = MasterBox.Text ?? "";
        View.ConfirmMaster = View.IsRecovering ? RecoverConfirmBox.Text ?? "" : ConfirmMasterBox.Text ?? "";
        View.ShortKey = ShortBox.Text ?? "";
        View.ConfirmShort = ConfirmShortBox.Text ?? "";
        if (View.IsRecovering)
        {
            View.Answer1 = RecoverAnswer1Box.Text ?? "";
            View.Answer2 = RecoverAnswer2Box.Text ?? "";
            View.Answer3 = RecoverAnswer3Box.Text ?? "";
        }
        else
        {
            View.Answer1 = Answer1Box.Text ?? "";
            View.Answer2 = Answer2Box.Text ?? "";
            View.Answer3 = Answer3Box.Text ?? "";
        }
    }
}
