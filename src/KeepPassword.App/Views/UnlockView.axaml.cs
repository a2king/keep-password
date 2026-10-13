using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UnlockView : UserControl
{
    private bool _animating;

    public event Action<VaultSession>? Unlocked;

    public UnlockView()
    {
        InitializeComponent();
    }

    public void Bind(VaultStore store, string vaultFile)
    {
        ClearSecrets();
        DataContext = new UnlockViewModel(store, vaultFile);
        Opacity = 1;
        Badge.Reset();
        _animating = false;
    }

    public void FocusMaster()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (View?.ShowShortUnlock == true)
            {
                UnlockShortBox.Focus();
            }
            else
            {
                MasterBox.Focus();
            }
        }, DispatcherPriority.Input);
    }

    public void ClearSecrets()
    {
        MasterBox.Text = "";
        ConfirmMasterBox.Text = "";
        ShortBox.Text = "";
        ConfirmShortBox.Text = "";
        UnlockShortBox.Text = "";
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
        if (View is not { IsBusy: false } view || _animating)
        {
            return;
        }

        CopyPasswords();
        Badge.StartSpin();
        var ok = await view.SubmitAsync();
        if (!ok || view.Session is not { } session)
        {
            Badge.StopSpin();
            if (view.HasError)
            {
                await Task.WhenAll(Badge.PlayRejectAsync(), ShakeAsync());
            }

            FocusMaster();
            return;
        }

        _animating = true;
        await Badge.PlayTurnAsync();
        await Task.WhenAll(
            Badge.PlayPopAsync(),
            Tween.RunAsync(TimeSpan.FromMilliseconds(280), t => Opacity = 1 - t, Tween.CubicOut));
        _animating = false;
        Unlocked?.Invoke(session);
    }

    private Task ShakeAsync()
    {
        if (InputBox.RenderTransform is not TranslateTransform shift)
        {
            return Task.CompletedTask;
        }

        return Tween.RunAsync(TimeSpan.FromMilliseconds(380), t => shift.X = Math.Sin(t * Math.PI * 6) * 8 * (1 - t));
    }

    private void OnUseMaster(object? sender, RoutedEventArgs e)
    {
        View?.UseMasterPassword();
        UnlockShortBox.Text = "";
        MasterBox.Focus();
    }

    private void OnUseShort(object? sender, RoutedEventArgs e)
    {
        View?.UseShortKey();
        MasterBox.Text = "";
        UnlockShortBox.Focus();
    }

    private void OnBeginRecovery(object? sender, RoutedEventArgs e)
    {
        View?.BeginRecovery();
        UnlockShortBox.Text = "";
        MasterBox.Text = "";
    }

    private void OnCancelRecovery(object? sender, RoutedEventArgs e)
    {
        View?.CancelRecovery();
        MasterBox.Text = "";
        RecoverConfirmBox.Text = "";
        FocusMaster();
    }

    private void CopyPasswords()
    {
        if (View is null)
        {
            return;
        }

        View.MasterPassword = MasterBox.Text ?? "";
        View.ConfirmMaster = View.IsRecovering ? RecoverConfirmBox.Text ?? "" : ConfirmMasterBox.Text ?? "";
        View.ShortKey = View.IsCreate ? ShortBox.Text ?? "" : UnlockShortBox.Text ?? "";
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
