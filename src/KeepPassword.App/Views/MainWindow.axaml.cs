using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Security;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class MainWindow : Window
{
    private const double AccountCardMinWidth = 270;
    private readonly DispatcherTimer _totpTimer;
    private VaultStore? _store;
    private VaultSession? _session;
    private VaultLocation? _location;
    private Action<bool>? _pauseWatcher;
    private bool _platformAutofill;

    public bool AllowClose { get; set; }

    public event EventHandler? LockRequested;

    public event Action<VaultSession>? Unlocked;

    public MainWindow()
    {
        InitializeComponent();
        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => View?.RefreshTotp();
        UnlockRoot.Unlocked += session => Unlocked?.Invoke(session);
        AccountScroller.SizeChanged += (_, _) => UpdateAccountColumns();
        UpdateThemeButtonState(ThemeManager.CurrentTheme);
        ThemeManager.ThemeChanged += UpdateThemeButtonState;
        Opened += (_, _) =>
        {
            UpdateThemeButtonState(ThemeManager.CurrentTheme);
            if (!VaultRoot.IsVisible)
            {
                UnlockRoot.FocusMaster();
            }
        };
    }

    public MainWindow(VaultStore store, VaultLocation location, bool platformAutofillSupported, Action<bool>? pauseWatcher = null)
        : this()
    {
        _store = store;
        _location = location;
        _platformAutofill = platformAutofillSupported;
        _pauseWatcher = pauseWatcher;
        ShowUnlockScreen();
    }

    public MainViewModel? View => VaultRoot.DataContext as MainViewModel;

    public void ShowUnlockScreen()
    {
        if (_store is null || _location is null)
        {
            return;
        }

        _totpTimer.Stop();
        UnlockRoot.Bind(_store, _location.VaultFile);
        UnlockRoot.IsVisible = true;
        if (View is not null)
        {
            View.Name = "";
            View.Url = "";
            View.Username = "";
            View.Password = "";
            View.Note = "";
            View.TotpSecret = "";
            View.PasswordVisible = false;
        }

        PasswordBox.Text = "";
        PasswordBox.PasswordChar = '•';
        GeneratorRoot.ClearSecret();
        ShowItemsPage();
        VaultRoot.IsVisible = false;
        VaultRoot.DataContext = null;
        _session = null;
        if (IsVisible)
        {
            Activate();
            UnlockRoot.FocusMaster();
        }
    }

    public void BringToFront()
    {
        Show();
        Activate();
        if (!VaultRoot.IsVisible)
        {
            UnlockRoot.FocusMaster();
        }
    }

    public void ShowVault(VaultSession session)
    {
        _session = session;
        VaultRoot.DataContext = new MainViewModel(session, _platformAutofill);
        PasswordBox.PasswordChar = '•';
        ShowItemsPage();
        VaultRoot.IsVisible = true;
        UpdateAccountColumns();
        UnlockRoot.ClearSecrets();
        UnlockRoot.IsVisible = false;
        _totpTimer.Start();
    }

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

    private async void OnCloseDetail(object? sender, RoutedEventArgs e)
    {
        if (View is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        View.CloseDetail();
    }

    private void UpdateAccountColumns()
    {
        var width = AccountScroller.Bounds.Width;
        if (width > 0)
        {
            View?.SetColumns((int)(width / AccountCardMinWidth));
        }
    }

    private void OnTagFilter(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            View?.SelectTag(key);
        }
    }

    private void OnChooseSpace(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            View?.ChooseSpace(name);
        }
    }

    private void OnToggleTag(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name })
        {
            View?.ToggleTag(name);
        }
    }

    private async void OnAddSpace(object? sender, RoutedEventArgs e) => await AddLabelAsync(isSpace: true);

    private async void OnAddTag(object? sender, RoutedEventArgs e) => await AddLabelAsync(isSpace: false);

    private async void OnNewSpaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddLabelAsync(isSpace: true);
        }
    }

    private async void OnNewTagKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddLabelAsync(isSpace: false);
        }
    }

    private async Task AddLabelAsync(bool isSpace)
    {
        if (View is null)
        {
            return;
        }

        var error = isSpace ? View.AddSpaceChoice() : View.AddTagChoice();
        if (error is not null)
        {
            await Dialogs.AlertAsync(this, "无法新增", error);
        }
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

        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(
                this,
                "导入 CSV",
                [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var text = await File.ReadAllTextAsync(path);
            View.ImportCsv(text);
        }
        catch (Exception ex)
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

        var settings = new UserManagementWindow(_session, _location, _pauseWatcher);
        await settings.ShowDialog(this);
        if (settings.VaultChanged)
        {
            View?.Reload();
        }
    }

    private void OnNavItems(object? sender, RoutedEventArgs e) => ShowItemsPage();

    private void OnNavGenerator(object? sender, RoutedEventArgs e) => ShowGeneratorPage();

    private void ShowItemsPage()
    {
        ItemsPage.IsVisible = true;
        GeneratorPage.IsVisible = false;
        NavGenerator.Classes.Remove("active");
        NavItems.Classes.Add("active");
    }

    private void ShowGeneratorPage()
    {
        ItemsPage.IsVisible = false;
        GeneratorPage.IsVisible = true;
        NavItems.Classes.Remove("active");
        NavGenerator.Classes.Add("active");
    }

    private void OnFillGenerated(object? sender, RoutedEventArgs e)
    {
        if (View is not { IsNewLogin: true })
        {
            return;
        }

        View.FillGeneratedPassword();
        PasswordBox.PasswordChar = '\0';
    }

    private async void OnCopyPassword(object? sender, RoutedEventArgs e)
    {
        if (View is null || string.IsNullOrEmpty(View.Password) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        await SecretClipboard.CopyAsync(clipboard, View.Password);
        await Dialogs.AlertAsync(this, "已复制", "密码已复制。若 30 秒后剪贴板仍是该内容，会自动清除。");
    }

    private async void OnAudit(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        await new AuditWindow(View.Audit()).ShowDialog(this);
    }

    private async void OnBreach(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(this, "泄露检查", BreachChecker.PrivacyNotice))
        {
            return;
        }

        try
        {
            var findings = await View.CheckBreachesAsync();
            await new BreachWindow(findings).ShowDialog(this);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            await Dialogs.AlertAsync(this, "泄露检查失败", "网络查询没有完成。密码明文和完整哈希都没有上传。");
        }
    }

    private void OnLock(object? sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);

    private void OnCycleTheme(object? sender, RoutedEventArgs e)
    {
        var next = ThemeManager.CycleTheme();
        UpdateThemeButtonState(next);
    }

    private void UpdateThemeButtonState(string theme)
    {
        if (ThemeLabel is not null)
        {
            ThemeLabel.Text = ThemeManager.GetThemeDisplayName(theme);
        }
    }

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
