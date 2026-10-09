using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KeepPassword.App.Services;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class UserManagementWindow : Window
{
    private readonly VaultSession? _session;
    private readonly VaultLocation? _location;
    private readonly Action<bool>? _pauseWatcher;

    public UserManagementWindow() => InitializeComponent();

    public UserManagementWindow(VaultSession session, VaultLocation location, Action<bool>? pauseWatcher = null)
        : this()
    {
        _session = session;
        _location = location;
        _pauseWatcher = pauseWatcher;
        AccountText.Text = "当前账号：" + session.Account;
        DirectoryBox.Text = location.Directory;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
    {
        _pauseWatcher?.Invoke(true);
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "选择缓存目录",
                AllowMultiple = false
            });
            if (folders.Count == 0)
            {
                return;
            }

            var path = folders[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                DirectoryBox.Text = path;
            }
        }
        finally
        {
            _pauseWatcher?.Invoke(false);
        }
    }

    private void OnSwitchDirectory(object? sender, RoutedEventArgs e)
    {
        if (_location is null)
        {
            return;
        }

        var before = _location.Directory;
        try
        {
            var updated = _location.Switch(DirectoryBox.Text ?? "", _session);
            DirectoryBox.Text = updated;
            DirectoryStatus.Text = updated == before
                ? "缓存目录没有变化。"
                : "已把缓存文件转移到新目录，并清除原目录。";
            ErrorText.IsVisible = false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            DirectoryBox.Text = _location.Directory;
            ShowError(ex.Message);
        }
    }

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
