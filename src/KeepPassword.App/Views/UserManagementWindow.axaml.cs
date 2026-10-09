using Avalonia.Controls;
using Avalonia.Interactivity;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UserManagementWindow : Window
{
    private readonly VaultSession? _session;

    public UserManagementWindow() => InitializeComponent();

    public UserManagementWindow(VaultSession session)
        : this()
    {
        _session = session;
        AccountText.Text = "当前账号：" + session.Account;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var next = NewKeyBox.Text ?? "";
        var confirm = ConfirmBox.Text ?? "";
        var master = MasterBox.Text ?? "";
        if (next.Length == 0)
        {
            ShowError("请填写新的短密钥。");
            return;
        }

        if (next != confirm)
        {
            ShowError("两次输入的短密钥不一致。");
            return;
        }

        SaveButton.IsEnabled = false;
        BusyText.IsVisible = true;
        ErrorText.IsVisible = false;
        try
        {
            await Task.Run(() => _session.ChangeShortKey(master, next));
            Close();
        }
        catch (Exception ex) when (ex is CredentialRejectedException or ArgumentException)
        {
            ShowError(ex.Message);
        }
        finally
        {
            SaveButton.IsEnabled = true;
            BusyText.IsVisible = false;
            MasterBox.Text = "";
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
