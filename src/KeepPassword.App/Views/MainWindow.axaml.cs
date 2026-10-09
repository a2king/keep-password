using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _totpTimer;
    private readonly VaultSession? _session;
    private readonly VaultLocation? _location;
    private readonly Action<bool>? _pauseWatcher;

    public bool AllowClose { get; set; }

    public event EventHandler? LockRequested;

    public MainWindow()
    {
        InitializeComponent();
        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => View?.RefreshTotp();
    }

    public MainWindow(VaultSession session, bool platformAutofillSupported, VaultLocation location, Action<bool>? pauseWatcher = null)
        : this()
    {
        _session = session;
        _location = location;
        _pauseWatcher = pauseWatcher;
        DataContext = new MainViewModel(session, platformAutofillSupported);
        _totpTimer.Start();
    }

    public MainViewModel? View => DataContext as MainViewModel;

    public void PrepareClose() => AllowClose = true;

    private async void OnEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } || View is null)
        {
            return;
        }

        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        View.Load(id);
    }

    private async void OnNewLogin(object? sender, RoutedEventArgs e)
    {
        if (View is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        View.NewLogin();
    }

    private async void OnNewTotp(object? sender, RoutedEventArgs e)
    {
        if (View is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        View.NewTotp();
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        var error = View.Save();
        if (error is not null)
        {
            await Dialogs.AlertAsync(this, "无法保存", error);
        }
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, "删除条目", "删除后无法恢复。确定删除这条记录吗？"))
        {
            return;
        }

        View.Delete();
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        var path = await SafeStoragePickers.PickOpenFileAsync(
            this,
            "导入 CSV",
            [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] }],
            _pauseWatcher);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            View.ImportCsv(text);
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.AlertAsync(this, "无法导入", ex.Message);
        }
    }

    private async void OnTotp(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        await new TotpWindow(_session).ShowDialog(this);
    }

    private async void OnUsers(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (_location is null)
        {
            return;
        }

        await new UserManagementWindow(_session, _location, _pauseWatcher).ShowDialog(this);
    }

    private void OnNavItems(object? sender, RoutedEventArgs e)
    {
        // 主界面本身就是条目页，侧栏按钮只保留选中感。
    }

    private void OnLock(object? sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);

    private void OnTogglePassword(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        View.PasswordVisible = !View.PasswordVisible;
        PasswordBox.PasswordChar = View.PasswordVisible ? '\0' : '•';
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (View is not { IsDirty: true })
        {
            return true;
        }

        return await Dialogs.ConfirmAsync(this, "放弃修改", "当前条目还有未保存的修改。要放弃这些修改吗？");
    }
}
