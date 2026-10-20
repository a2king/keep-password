using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Services;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UnlockView : UserControl
{
    private bool _animating;
    private CancellationTokenSource _animation = new();

    public event Action<VaultSession>? Unlocked;

    public UnlockView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) => CancelAnimation();
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
            if (View is null)
            {
                return;
            }

            if (View.ShowShortUnlock)
            {
                UnlockShortBox.Focus();
            }
            else if (View.ShowKeyUnlock || View.ShowKeyRecovery)
            {
                RecoveryKeyBox.Focus();
            }
            else if (View.IsCreate)
            {
                ShortBox.Focus();
            }
            else
            {
                MasterBox.Focus();
            }
        }, DispatcherPriority.Input);
    }

    public void ClearSecrets()
    {
        CancelAnimation();
        ClearInputValues();
        DataContext = null;
    }

    private void ClearInputValues()
    {
        MasterBox.Text = "";
        ConfirmMasterBox.Text = "";
        ShortBox.Text = "";
        ConfirmShortBox.Text = "";
        UnlockShortBox.Text = "";
        KeyUnlockShortBox.Text = "";
        KeyUnlockConfirmShortBox.Text = "";
        RecoveryKeyBox.Text = "";
        RecoverAnswer1Box.Text = "";
        RecoverAnswer2Box.Text = "";
        RecoverAnswer3Box.Text = "";
        RecoverConfirmBox.Text = "";
    }

    private void CancelAnimation()
    {
        _animation.Cancel();
        _animation.Dispose();
        _animation = new CancellationTokenSource();
        Badge.Reset();
        Motion.ResetTree(this);
        if (InputBox.RenderTransform is TranslateTransform shift)
        {
            shift.X = 0;
        }
        Opacity = 1;
        _animating = false;
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
        var token = _animation.Token;
        Badge.StartSpin();
        var ok = await view.SubmitAsync();
        if (token.IsCancellationRequested || View != view)
        {
            view.Session?.Dispose();
            return;
        }

        try
        {
            if (!ok || view.Session is not { } session)
            {
                Badge.StopSpin();
                if (view.HasError)
                {
                    _animating = true;
                    await Task.WhenAll(Badge.PlayRejectAsync(), ShakeAsync(token));
                }

                if (!token.IsCancellationRequested && View == view)
                {
                    _animating = false;
                    FocusMaster();
                }
                return;
            }

            _animating = true;
            ClearInputValues();
            if (session.NeedsKeyFileSave)
            {
                using var setupCancellation = token.Register(session.Lock);
                Badge.StopSpin();
                if (TopLevel.GetTopLevel(this) is not Window owner
                    || !await EncryptionKeyDialog.SaveNewAsync(owner, session))
                {
                    view.CancelKeySetup();
                    MasterBox.Text = "";
                    ConfirmMasterBox.Text = "";
                    ShortBox.Text = "";
                    ConfirmShortBox.Text = "";
                    _animating = false;
                    return;
                }
            }
            if (session.NeedsLocalKeyUpgrade
                && TopLevel.GetTopLevel(this) is Window upgradeOwner
                && !session.NeedsKeyFileSave)
            {
                Badge.StopSpin();
                var upgrade = await Dialogs.ConfirmAsync(upgradeOwner, "升级保险库格式",
                    "检测到旧版保险库。升级后将去掉主密码保护层，本机继续用短密钥解锁；换电脑或忘记短密钥时使用已保存的加密密钥文件。开启云同步前必须升级。现在升级吗？",
                    "升级");
                if (token.IsCancellationRequested || View != view)
                {
                    session.Dispose();
                    return;
                }

                if (upgrade)
                {
                    try
                    {
                        await Task.Run(session.UpgradeToLocalKeyFormat);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
                    {
                        view.Error = ex.Message;
                        view.DropSession();
                        _animating = false;
                        FocusMaster();
                        return;
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            await Badge.PlayTurnAsync();
            token.ThrowIfCancellationRequested();
            await Task.WhenAll(
                Badge.PlayPopAsync(),
                Tween.RunAsync(Motion.IsEnabled ? TimeSpan.FromMilliseconds(180) : TimeSpan.Zero,
                    t => Opacity = 1 - t, Tween.CubicOut, token));
            token.ThrowIfCancellationRequested();
            if (View == view)
            {
                _animating = false;
                Unlocked?.Invoke(session);
            }
            else
            {
                session.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            view.Session?.Dispose();
        }
    }

    private Task ShakeAsync(CancellationToken token)
    {
        if (InputBox.RenderTransform is not TranslateTransform shift)
        {
            return Task.CompletedTask;
        }

        return Tween.RunAsync(Motion.IsEnabled ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero,
            t => shift.X = Math.Sin(t * Math.PI * 6) * 8 * (1 - t), cancellationToken: token);
    }

    private void OnUseMaster(object? sender, RoutedEventArgs e)
    {
        View?.UseMasterPassword();
        UnlockShortBox.Text = "";
        MasterBox.Focus();
        _ = Motion.EnterAsync(UnlockForm, 4);
    }

    private void OnUseEncryptionKey(object? sender, RoutedEventArgs e)
    {
        View?.UseEncryptionKey();
        UnlockShortBox.Text = "";
        RecoveryKeyBox.Focus();
        _ = Motion.EnterAsync(UnlockForm, 4);
    }

    private void OnUseShort(object? sender, RoutedEventArgs e)
    {
        View?.UseShortKey();
        MasterBox.Text = "";
        KeyUnlockShortBox.Text = "";
        KeyUnlockConfirmShortBox.Text = "";
        RecoveryKeyBox.Text = "";
        UnlockShortBox.Focus();
        _ = Motion.EnterAsync(UnlockForm, 4);
    }

    private void OnBeginRecovery(object? sender, RoutedEventArgs e)
    {
        View?.BeginRecovery();
        UnlockShortBox.Text = "";
        MasterBox.Text = "";
        _ = Motion.EnterAsync(UnlockForm, 4);
    }

    private void OnCancelRecovery(object? sender, RoutedEventArgs e)
    {
        View?.CancelRecovery();
        MasterBox.Text = "";
        RecoverConfirmBox.Text = "";
        RecoveryKeyBox.Text = "";
        FocusMaster();
        _ = Motion.EnterAsync(UnlockForm, 4);
    }

    private void CopyPasswords()
    {
        if (View is null)
        {
            return;
        }

        View.MasterPassword = MasterBox.Text ?? "";
        View.ConfirmMaster = View.IsRecovering ? RecoverConfirmBox.Text ?? "" : ConfirmMasterBox.Text ?? "";
        if (View.IsCreate)
        {
            View.ShortKey = ShortBox.Text ?? "";
            View.ConfirmShort = ConfirmShortBox.Text ?? "";
        }
        else if (View.ShowKeyUnlock)
        {
            View.ShortKey = KeyUnlockShortBox.Text ?? "";
            View.ConfirmShort = KeyUnlockConfirmShortBox.Text ?? "";
        }
        else
        {
            View.ShortKey = UnlockShortBox.Text ?? "";
            View.ConfirmShort = ConfirmShortBox.Text ?? "";
        }

        if (View.IsRecovering)
        {
            View.Answer1 = RecoverAnswer1Box.Text ?? "";
            View.Answer2 = RecoverAnswer2Box.Text ?? "";
            View.Answer3 = RecoverAnswer3Box.Text ?? "";
        }

        View.EncryptionKey = RecoveryKeyBox.Text ?? "";
    }

    private async void OnChooseRecoveryKey(object? sender, RoutedEventArgs e)
    {
        if (View is not { } view || TopLevel.GetTopLevel(this) is not Window owner) return;
        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(owner, "选择加密密钥文件", [EncryptionKeyDialog.KeyFileType]);
            if (View == view && !string.IsNullOrWhiteSpace(path)) RecoveryKeyBox.Text = VaultKeyFile.Read(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CredentialRejectedException)
        {
            if (View == view) view.Error = ex.Message;
        }
    }
}
