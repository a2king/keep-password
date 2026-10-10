using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Import;
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
        View?.CloseDetail();
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

    private void OnKindFilter(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key })
        {
            View?.SelectKind(key);
        }
    }

    private async void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (View?.ToggleFavorite() is { } error)
        {
            await Dialogs.AlertAsync(this, "无法收藏", error);
        }
    }

    private void OnAuthPassword(object? sender, RoutedEventArgs e) => View?.SetKeyAuth(false);

    private void OnAuthKey(object? sender, RoutedEventArgs e) => View?.SetKeyAuth(true);

    private void OnGenerateField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FieldEditorView field })
        {
            field.Value = MainViewModel.GeneratePassword();
            field.Revealed = true;
            if (View is not null)
            {
                View.Status = "已填入 16 位随机密码（字母和数字）。";
            }
        }
    }

    private void OnRevealField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SecretFieldView field })
        {
            field.Revealed = !field.Revealed;
        }
    }

    private async void OnCopyField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SecretFieldView field })
        {
            await CopyAsync(field.Value, field.Label, field.Sensitive);
        }
    }

    private async void OnCopyConnection(object? sender, RoutedEventArgs e)
    {
        if (View is not null)
        {
            await CopyAsync(View.ConnectionString, "连接字符串", sensitive: true);
        }
    }

    private async void OnCopyFingerprint(object? sender, RoutedEventArgs e)
    {
        if (View is { SshFingerprint: var fingerprint } && fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            await CopyAsync(fingerprint, "指纹", sensitive: false);
        }
    }

    private void OnAddNode(object? sender, RoutedEventArgs e) => View?.AddNode();

    private async void OnCopyNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NodeEditorView node })
        {
            await CopyAsync(node.Address, "节点地址", sensitive: false);
        }
    }

    private void OnRemoveNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: NodeEditorView node })
        {
            View?.RemoveNode(node);
        }
    }

    private async void OnRemoveCustomField(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: CustomFieldEditorView field } || View is null)
        {
            return;
        }

        if (await Dialogs.ConfirmAsync(this, "删除字段", $"删除自定义字段「{field.Name}」？保存后生效。"))
        {
            View.RemoveCustomField(field);
        }
    }

    private async void OnAddCustomField(object? sender, RoutedEventArgs e) => await AddCustomFieldAsync();

    private async void OnNewFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await AddCustomFieldAsync();
        }
    }

    private async Task AddCustomFieldAsync()
    {
        if (View?.AddCustomField() is { } error)
        {
            await Dialogs.AlertAsync(this, "无法添加字段", error);
        }
    }

    private async Task CopyAsync(string value, string label, bool sensitive)
    {
        if (View is null)
        {
            return;
        }

        if (string.IsNullOrEmpty(value) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            View.Status = $"{label}为空，没有复制。";
            return;
        }

        if (!sensitive)
        {
            await clipboard.SetTextAsync(value);
            View.Status = $"已复制{label}。";
            return;
        }

        View.Status = $"已复制{label}。若 30 秒后剪贴板仍是该内容，会自动清除。";
        await SecretClipboard.CopyAsync(clipboard, value);
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

    private async void OnNewItem(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string code } || View is null || !await ConfirmDiscardAsync())
        {
            return;
        }

        View.NewItem(VaultItemKinds.Parse(code));
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

        var references = View.ReferencingNames();
        var message = references.Count == 0
            ? "删除后无法恢复。确定删除这条记录吗？"
            : $"以下 {references.Count} 个条目正在使用这把 SSH 密钥：{string.Join("、", references)}。\n删除后会同时解除它们的关联（改回密码认证），且无法恢复。确定删除吗？";
        if (!await Dialogs.ConfirmAsync(this, "删除条目", message))
        {
            return;
        }

        View.Delete();
    }

    private async void OnUndoImport(object? sender, RoutedEventArgs e)
    {
        if (View is not { CanUndoImport: true })
        {
            return;
        }

        if (await Dialogs.ConfirmAsync(this, "撤销导入", "将删除上一次导入创建的全部条目（导入后修改过的也会删除）。确定撤销吗？"))
        {
            View.UndoImport();
        }
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
            var table = CsvImporter.ReadTable(text);
            if (table.Headers.Count == 0 || table.Rows.Count == 0)
            {
                await Dialogs.AlertAsync(this, "无法导入", "CSV 文件为空，或只有表头没有数据行。");
                return;
            }

            var entries = await new ImportWizardWindow(table, Path.GetFileName(path)).ShowDialog<IReadOnlyList<VaultEntry>?>(this);
            if (entries is { Count: > 0 })
            {
                View.ApplyImport(entries);
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
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

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (View is not { IsDirty: true })
        {
            return true;
        }

        return await Dialogs.ConfirmAsync(this, "放弃修改", "当前条目还有未保存的修改。要放弃这些修改吗？");
    }
}
