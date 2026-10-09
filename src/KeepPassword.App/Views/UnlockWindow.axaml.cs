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
    private readonly VaultLocation? _location;

    public UnlockWindow()
    {
        InitializeComponent();
    }

    public UnlockWindow(VaultStore store, VaultLocation location)
        : this()
    {
        _location = location;
        DirectoryBox.Text = location.Directory;
        DataContext = new UnlockViewModel(store, location.VaultFile);
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

    private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
    {
        var path = await SafeStoragePickers.PickFolderAsync(this, "选择缓存目录");
        if (!string.IsNullOrWhiteSpace(path))
        {
            DirectoryBox.Text = path;
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
            var updated = _location.Switch(DirectoryBox.Text ?? "", session: null);
            DirectoryBox.Text = updated;
            View.UseVaultFile(_location.VaultFile);
            DirectoryStatus.Text = updated == before
                ? "缓存目录没有变化。"
                : "已把缓存文件转移到新目录，并清除原目录。";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            View.Error = ex.Message;
            DirectoryBox.Text = _location.Directory;
            View.UseVaultFile(_location.VaultFile);
        }
    }

    private void CopyPasswords()
    {
        View.MasterPassword = MasterBox.Text ?? "";
        View.ConfirmMaster = ConfirmMasterBox.Text ?? "";
        View.ShortKey = ShortBox.Text ?? "";
        View.ConfirmShort = ConfirmShortBox.Text ?? "";
    }
}
