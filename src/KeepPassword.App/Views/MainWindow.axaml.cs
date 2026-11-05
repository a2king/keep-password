using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KeepPassword.App.Controls;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Security;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class MainWindow : Window
{
    private const double AccountCardMinWidth = 270;
    private IDisposable? _legacyTotpSubscription;
    private Guid? _presentedDetailId;
    private VaultStore? _store;
    private VaultSession? _session;
    private VaultLocation? _location;
    private Action<bool>? _pauseWatcher;
    private CloudSyncCoordinator? _cloudSync;
    private bool _platformAutofill;
    private bool _scanning;
    private Control? _activePage;
    private MainViewModel? _observedView;
    private CancellationTokenSource? _filterAnimation;
    private CancellationTokenSource? _detailAnimation;
    private readonly TranslateTransform _detailTranslation = new();
    private bool _detailLayoutOpen;
    private bool _updatingAccountColumns;
    private bool _kindFiltersExpanded;
    private bool _tagFiltersExpanded;
    private int _detailRevision;

    public bool AllowClose { get; set; }

    public event EventHandler? LockRequested;

    public event Action<VaultSession>? Unlocked;

    public MainWindow()
    {
        InitializeComponent();
        _detailTranslation.X = DetailHost.Width;
        DetailHost.RenderTransform = _detailTranslation;
        ModernDetail.SaveRequested += () => OnSave(this, new RoutedEventArgs());
        ModernDetail.CloseRequested += () => OnCloseDetail(this, new RoutedEventArgs());
        ModernDetail.DeleteRequested += () => OnDelete(this, new RoutedEventArgs());
        ModernDetail.FavoriteRequested += () => OnToggleFavorite(this, new RoutedEventArgs());
        ModernDetail.ScanRequested += field => OnScanTotpField(new Button { Tag = field }, new RoutedEventArgs());
        ModernDetail.RemoveAccountRequested += account => OnRemoveAccount(new Button { Tag = account }, new RoutedEventArgs());
        ModernDetail.RemoveCustomFieldRequested += field => OnRemoveCustomField(new Button { Tag = field }, new RoutedEventArgs());
        UnlockRoot.Unlocked += session => Unlocked?.Invoke(session);
        SettingsRoot.VaultChanged += (_, _) => View?.Reload();
        SettingsRoot.LockRequested += (_, _) => LockRequested?.Invoke(this, EventArgs.Empty);
        SpacePicker.Toggled += name => View?.ChooseSpace(name);
        SpacePicker.Created += async name => await AddLabelAsync(name, isSpace: true);
        TagPicker.Toggled += name => View?.ToggleTag(name);
        TagPicker.Created += async name => await AddLabelAsync(name, isSpace: false);
        AuditRoot.Rerun = () =>
        {
            if (View is not null)
            {
                AuditRoot.Show(View.Audit());
            }
        };
        BreachRoot.Checker = token => View?.CheckBreachesAsync(token)
            ?? Task.FromResult<IReadOnlyList<BreachFinding>>([]);
        AccountScroller.SizeChanged += (_, _) => UpdateAccountColumns();
        UpdateThemeButtonState(ThemeManager.CurrentTheme);
        UpdateMotionPreference();
        MotionPreferences.Changed += UpdateMotionPreference;
        ThemeManager.ThemeChanged += UpdateThemeButtonState;
        AppUpdateService.Changed += OnAppUpdateChanged;
        UpdateSettingsBadge();
        Closed += (_, _) =>
        {
            ThemeManager.ThemeChanged -= UpdateThemeButtonState;
            MotionPreferences.Changed -= UpdateMotionPreference;
            AppUpdateService.Changed -= OnAppUpdateChanged;
            ObserveView(null);
            ModernDetail.SetEditor(null);
            _legacyTotpSubscription?.Dispose();
            _legacyTotpSubscription = null;
            CancelMotion();
        };
        Opened += (_, _) =>
        {
            UpdateThemeButtonState(ThemeManager.CurrentTheme);
            UpdateSettingsBadge();
            if (!VaultRoot.IsVisible)
            {
                UnlockRoot.FocusMaster();
            }
        };
    }

    public MainWindow(
        VaultStore store,
        VaultLocation location,
        bool platformAutofillSupported,
        Action<bool>? pauseWatcher = null,
        CloudSyncCoordinator? cloudSync = null)
        : this()
    {
        _store = store;
        _location = location;
        _platformAutofill = platformAutofillSupported;
        _pauseWatcher = pauseWatcher;
        _cloudSync = cloudSync;
        ShowUnlockScreen();
    }

    public MainViewModel? View => VaultRoot.DataContext as MainViewModel;

    private void UpdateMotionPreference()
    {
        if (Motion.IsEnabled) Classes.Remove("no-motion");
        else Classes.Add("no-motion");
    }

    public void ShowUnlockScreen()
    {
        if (_store is null || _location is null)
        {
            return;
        }

        ResetFilterExpansion();
        _legacyTotpSubscription?.Dispose();
        _legacyTotpSubscription = null;
        ModernDetail.SetEditor(null);
        // Never retain a password view for a closing animation when locking.
        VaultRoot.IsVisible = false;
        CancelMotion();
        UnlockRoot.Bind(_store, _location.VaultFile);
        UnlockRoot.IsVisible = true;
        View?.CloseDetail();
        GeneratorRoot.ClearSecret();
        ShowPage(ItemsPage, animate: false);
        AuditRoot.Clear();
        BreachRoot.Reset();
        SettingsRoot.Clear();
        VaultRoot.IsVisible = false;
        ObserveView(null);
        VaultRoot.DataContext = null;
        _session = null;
        if (IsVisible)
        {
            Activate();
            UnlockRoot.FocusMaster();
        }
        _ = Motion.EnterAsync(UnlockRoot, 4);
    }

    public void BringToFront()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (!VaultRoot.IsVisible)
        {
            UnlockRoot.FocusMaster();
        }
    }

    public void ShowVault(VaultSession session)
    {
        _session = session;
        ResetFilterExpansion();
        VaultRoot.DataContext = new MainViewModel(session, _platformAutofill);
        ObserveView(View);
        ShowPage(ItemsPage, animate: false);
        VaultRoot.IsVisible = true;
        UpdateAccountColumns();
        UnlockRoot.ClearSecrets();
        UnlockRoot.IsVisible = false;
        _ = Motion.EnterAsync(VaultRoot, 6);
    }

    public void PrepareClose() => AllowClose = true;

    private async void OnEntry(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Guid id } || View is not { } view)
        {
            return;
        }

        var revision = BeginDetailChange();
        if (!await ConfirmDiscardAsync() || View != view || revision != _detailRevision)
        {
            return;
        }

        view.Load(id);
        RevealDetail();
    }

    private async void OnCloseDetail(object? sender, RoutedEventArgs e) => await CloseDetailAsync();

    private async void OnListBlankPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.GetVisualAncestors().Prepend(source)
            .Any(visual => visual is Button or TextBox or ComboBox or ScrollBar))
        {
            return;
        }

        if (View is { HasDetail: true } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            await CloseDetailAsync();
        }
    }

    private async Task CloseDetailAsync()
    {
        if (View is not { HasDetail: true } view)
        {
            return;
        }

        var revision = BeginDetailChange();
        if (!await ConfirmDiscardAsync() || View != view || revision != _detailRevision)
        {
            return;
        }

        if (await AnimateDetailAsync(open: false) && View == view && revision == _detailRevision)
        {
            view.CloseDetail();
        }
    }

    private void UpdateAccountColumns()
    {
        var width = AccountScroller.Bounds.Width - AccountScroller.Padding.Left - AccountScroller.Padding.Right;
        if (width > 0)
        {
            // Resizing can change the column count, but must not fade the list on every breakpoint.
            _updatingAccountColumns = true;
            try
            {
                View?.SetColumns((int)(width / AccountCardMinWidth));
            }
            finally
            {
                _updatingAccountColumns = false;
            }
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

    private void OnToggleKindFilters(object? sender, RoutedEventArgs e)
    {
        _kindFiltersExpanded = !_kindFiltersExpanded;
        UpdateFilterExpansion(
            KindFiltersList,
            KindFiltersCollapsedIcon,
            KindFiltersExpandedIcon,
            sender as Button,
            _kindFiltersExpanded,
            "类型筛选");
    }

    private void OnToggleTagFilters(object? sender, RoutedEventArgs e)
    {
        _tagFiltersExpanded = !_tagFiltersExpanded;
        UpdateFilterExpansion(
            TagFiltersList,
            TagFiltersCollapsedIcon,
            TagFiltersExpandedIcon,
            sender as Button,
            _tagFiltersExpanded,
            "标签筛选");
    }

    private void UpdateFilterExpansion(
        ItemsControl list,
        PathIcon collapsedIcon,
        PathIcon expandedIcon,
        Button? toggleButton,
        bool expanded,
        string label)
    {
        if (expanded)
        {
            // Height is set in XAML for the compact default state. Clearing the
            // local value lets the WrapPanel measure all filter rows naturally.
            list.ClearValue(Layoutable.HeightProperty);
        }
        else
        {
            list.Height = 48;
        }

        collapsedIcon.IsVisible = !expanded;
        expandedIcon.IsVisible = expanded;
        if (toggleButton is not null)
        {
            ToolTip.SetTip(toggleButton, expanded ? $"收起{label}" : $"展开{label}");
        }

        // The filter rows sit above the account list. Force a layout pass so the
        // list moves immediately when the panel changes between one and many rows.
        ItemsPage.UpdateLayout();
    }

    private void ResetFilterExpansion()
    {
        _kindFiltersExpanded = false;
        _tagFiltersExpanded = false;
        KindFiltersList.Height = 48;
        TagFiltersList.Height = 48;
        KindFiltersCollapsedIcon.IsVisible = true;
        KindFiltersExpandedIcon.IsVisible = false;
        TagFiltersCollapsedIcon.IsVisible = true;
        TagFiltersExpandedIcon.IsVisible = false;
        ToolTip.SetTip(KindFiltersToggleButton, "展开类型筛选");
        ToolTip.SetTip(TagFiltersToggleButton, "展开标签筛选");
        ItemsPage.UpdateLayout();
    }

    private async void OnToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (View?.ToggleFavorite() is { } error)
        {
            await Dialogs.AlertAsync(this, "无法收藏", error);
        }
    }

    private void OnAccountAuthChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 只有用户切换造成与当前值不一致时才调用 SetKeyAuth；绑定回写同一索引时不重复触发。
        if (sender is SegmentedControl { Tag: AccountEditorView account, SelectedIndex: >= 0 and var index }
            && (index == 1) != account.UseKeyAuth)
        {
            account.SetKeyAuth(index == 1);
        }
    }

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

    private void OnClearSearch(object? sender, RoutedEventArgs e)
    {
        if (View is not null)
        {
            View.Search = "";
        }

        SearchBox.Focus();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && View is { HasSearch: true })
        {
            View.Search = "";
            e.Handled = true;
        }
    }

    private async void OnScanTotpField(object? sender, RoutedEventArgs e)
    {
        if (_scanning || sender is not Button { Tag: FieldEditorView field } || View is not { } view)
        {
            return;
        }

        QrScanResult result;
        _scanning = true;
        try
        {
            view.Status = "正在扫描屏幕上的二维码…";
            result = await QrScreenScanner.ScanAsync(this);
        }
        catch (Exception ex)
        {
            view.Status = "扫描失败。";
            await Dialogs.AlertAsync(this, "扫描失败", ex.Message);
            return;
        }
        finally
        {
            _scanning = false;
        }

        if (result.Codes.Count == 0)
        {
            view.Status = "未识别到验证码二维码。";
            var message = result.HasMigration
                ? "这是 Google Authenticator 的批量导出二维码，暂不支持。请在原网站重新显示单个账号的二维码，或手动输入 Base32 密钥。"
                : result.IgnoredCount > 0
                    ? "屏幕上的二维码不是验证码二维码（需要 otpauth://totp 链接）。"
                    : "没有在屏幕上找到二维码。请让二维码完整显示在任意显示器上，不要被其它窗口遮挡，然后再扫描一次。";
            await Dialogs.AlertAsync(this, "未识别到验证码", message);
            return;
        }

        var overwrite = field.Value.Trim().Length > 0 ? "\n\n当前已填写的密钥会被替换。" : "";
        ScannedTotp chosen;
        if (result.Codes.Count == 1)
        {
            chosen = result.Codes[0];
            var message = $"{chosen.Settings.DisplayName}\n{DescribeTotp(chosen.Settings)}{overwrite}";
            if (!await Dialogs.ConfirmAsync(this, "识别到验证码", message, "填入"))
            {
                view.Status = "已取消填入。";
                return;
            }
        }
        else
        {
            var options = result.Codes.Select(code => $"{code.Settings.DisplayName}（{DescribeTotp(code.Settings)}）").ToList();
            var index = await Dialogs.ChooseAsync(this, "识别到多个验证码", "选择要填入的验证码：" + overwrite, options, "填入");
            if (index < 0)
            {
                view.Status = "已取消填入。";
                return;
            }

            chosen = result.Codes[index];
        }

        field.Value = chosen.StoredSecret;
        var settings = chosen.Settings;
        var account = view.Accounts.FirstOrDefault(item => item.TotpField == field);
        if (account is not null && settings.Account.Length > 0 && string.IsNullOrWhiteSpace(account.UsernameField.Value))
        {
            account.UsernameField.Value = settings.Account;
        }

        if (string.IsNullOrWhiteSpace(view.Name))
        {
            view.Name = settings.Issuer.Length > 0 ? settings.Issuer : settings.Account;
        }

        view.Status = $"已填入「{settings.DisplayName}」的验证码密钥，保存后生效。";
    }

    private static string DescribeTotp(TotpSettings settings) =>
        $"{settings.Digits} 位 · {settings.PeriodSeconds} 秒 · {settings.Algorithm.ToString().ToUpperInvariant()}";

    private void OnRevealField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SecretFieldView field })
        {
            field.Revealed = !field.Revealed;
        }
    }

    private async void OnCopyField(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is SecretFieldView field)
        {
            if (await CopyAsync(field.Value, field.Label, field.Sensitive))
            {
                _ = CopyFeedback.ShowAsync(button);
            }
        }
    }

    private async void OnCopyFingerprint(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button
            && View is { SshFingerprint: var fingerprint }
            && fingerprint.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            if (await CopyAsync(fingerprint, "指纹", sensitive: false))
            {
                _ = CopyFeedback.ShowAsync(button);
            }
        }
    }

    private void OnAddNode(object? sender, RoutedEventArgs e) => View?.AddNode();

    private void OnAddAccount(object? sender, RoutedEventArgs e) => View?.AddAccount();

    private async void OnRemoveAccount(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AccountEditorView account } || View is null)
        {
            return;
        }

        if (await Dialogs.ConfirmDangerAsync(this, "删除账号", $"删除账号「{account.DisplayName}」？保存后生效。"))
        {
            View.RemoveAccount(account);
            ModernDetail.Refresh();
        }
    }

    private async void OnCopyNode(object? sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is NodeEditorView node)
        {
            if (await CopyAsync(node.Address, "节点地址", sensitive: false))
            {
                _ = CopyFeedback.ShowAsync(button);
            }
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

        if (await Dialogs.ConfirmDangerAsync(this, "删除字段", $"删除字段「{field.Name}」？保存后生效。"))
        {
            View.RemoveCustomField(field);
        }
    }

    private async Task<bool> CopyAsync(string value, string label, bool sensitive)
    {
        if (View is null)
        {
            return false;
        }

        if (string.IsNullOrEmpty(value) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            View.Status = $"{label}为空，没有复制。";
            return false;
        }

        if (!sensitive)
        {
            if (!await ClipboardService.CopyAsync(this, value, sensitive: false))
            {
                return false;
            }
            View.Status = $"已复制{label}。";
            return true;
        }

        if (!await ClipboardService.CopyAsync(clipboard, value, sensitive: true))
        {
            return false;
        }
        if (View is not null)
        {
            View.Status = $"已复制{label}。若 30 秒后剪贴板仍是该内容，会自动清除。";
        }
        return true;
    }

    private async Task AddLabelAsync(string name, bool isSpace)
    {
        if (View is null)
        {
            return;
        }

        var error = isSpace ? View.AddSpaceChoice(name) : View.AddTagChoice(name);
        if (error is not null)
        {
            await Dialogs.AlertAsync(this, "无法新增", error);
        }
    }

    private async void OnNewItem(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string code } || View is not { } view)
        {
            return;
        }

        var revision = BeginDetailChange();
        if (!await ConfirmDiscardAsync() || View != view || revision != _detailRevision)
        {
            return;
        }

        view.NewItem(VaultItemKinds.Parse(code));
        RevealDetail();
    }

    private async void OnNewTotp(object? sender, RoutedEventArgs e)
    {
        if (View is not { } view)
        {
            return;
        }

        var revision = BeginDetailChange();
        if (!await ConfirmDiscardAsync() || View != view || revision != _detailRevision)
        {
            return;
        }

        view.NewTotp();
        RevealDetail();
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
            return;
        }

        _cloudSync?.NotifyLocalChanged();
        SynchronizeDetailView();
    }

    private async void OnDelete(object? sender, RoutedEventArgs e)
    {
        if (View is not { HasDetail: true } view)
        {
            return;
        }

        var revision = BeginDetailChange();
        var references = view.ReferencingNames();
        var message = references.Count == 0
            ? "删除后无法恢复。确定删除这条记录吗？"
            : $"以下 {references.Count} 个条目正在使用这把 SSH 密钥：{string.Join("、", references)}。\n删除后会同时解除它们的关联（改回密码认证），且无法恢复。确定删除吗？";
        if (!await Dialogs.ConfirmDangerAsync(this, "删除条目", message)
            || View != view || revision != _detailRevision)
        {
            return;
        }

        if (await AnimateDetailAsync(open: false) && View == view && revision == _detailRevision)
        {
            try
            {
                if (view.Delete())
                {
                    _cloudSync?.NotifyLocalChanged();
                }
            }
            finally
            {
                if (View == view && view.HasDetail && revision == _detailRevision)
                {
                    RevealDetail();
                }
            }
        }
    }

    private void OnNavItems(object? sender, RoutedEventArgs e) => ShowPage(ItemsPage);

    private void OnNavGenerator(object? sender, RoutedEventArgs e)
    {
        ShowPage(GeneratorPage);
        GeneratorRoot.EnsureGenerated();
    }

    private void OnTotp(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        if (_activePage != TotpPage)
        {
            TotpRoot.Start(_session);
        }

        ShowPage(TotpPage);
    }

    private void OnAudit(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        AuditRoot.Show(View.Audit());
        ShowPage(AuditPage);
    }

    private void OnBreach(object? sender, RoutedEventArgs e)
    {
        if (View is null)
        {
            return;
        }

        ShowPage(BreachPage);
    }

    private void OnUsers(object? sender, RoutedEventArgs e)
    {
        if (_session is null || _location is null)
        {
            return;
        }

        if (_activePage != SettingsPage)
        {
            SettingsRoot.Show(_session, _location, _pauseWatcher, _cloudSync, View);
        }

        ShowPage(SettingsPage);
    }

    private void OnAppUpdateChanged() => Dispatcher.UIThread.Post(UpdateSettingsBadge);

    private void UpdateSettingsBadge()
    {
        var available = AppUpdateService.UpdateAvailable
            || AppUpdateService.Phase is AppUpdatePhase.Available or AppUpdatePhase.Ready or AppUpdatePhase.Downloading;
        SettingsUpdateBadge.IsVisible = available;
        NavSettings.SetValue(
            ToolTip.TipProperty,
            available
                ? "有新版本可用，打开设置查看更新"
                : "账户与偏好设置");
    }

    private void ShowPage(Control page, bool animate = true)
    {
        (Control Page, Button Nav)[] pages =
        [
            (ItemsPage, NavItems),
            (GeneratorPage, NavGenerator),
            (TotpPage, NavTotp),
            (AuditPage, NavAudit),
            (BreachPage, NavBreach),
            (SettingsPage, NavSettings)
        ];
        var previous = _activePage;
        var changing = previous != page;
        var play = animate && changing && VaultRoot.IsVisible;
        _activePage = page;
        // The independent drawer transition may finish off-page; page fades do not reset its position.
        foreach (var (candidate, nav) in pages)
        {
            var active = candidate == page;
            if (changing || !animate)
            {
                Motion.ResetTree(candidate);
                candidate.IsVisible = active || (play && candidate == previous);
                candidate.IsHitTestVisible = active;
                candidate.ZIndex = active ? 1 : 0;
            }
            if (active)
            {
                nav.Classes.Add("active");
            }
            else
            {
                nav.Classes.Remove("active");
            }
        }

        if (page != TotpPage)
        {
            if (play && previous == TotpPage)
            {
                TotpRoot.Pause();
            }
            else
            {
                TotpRoot.Stop();
            }
        }

        // 页面隐藏后不保留已揭示的密码；验证码时钟只服务可见内容。
        if (page != ItemsPage) ModernDetail.ResetTransientState();
        UpdateLegacyTotpSubscription();

        if (play)
        {
            if (previous is not null)
            {
                _ = LeavePageAsync(previous);
            }
            _ = Motion.EnterAsync(page, 10);
        }
    }

    private async Task LeavePageAsync(Control page)
    {
        await Motion.ExitAsync(page);
        if (page == TotpPage && _activePage != TotpPage)
        {
            TotpRoot.Stop();
        }
    }

    private void ObserveView(MainViewModel? view)
    {
        if (_observedView is not null)
        {
            _observedView.PropertyChanged -= OnViewChanged;
        }
        _observedView = view;
        if (view is not null)
        {
            view.PropertyChanged += OnViewChanged;
        }
        if (view is null)
        {
            ModernDetail.SetEditor(null);
        }
    }

    private void OnViewChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasDetail) && View?.HasDetail != true)
        {
            _detailRevision++;
            ResetDetail();
        }
        else if (e.PropertyName == nameof(MainViewModel.EntryRows) && !_updatingAccountColumns)
        {
            _ = RefreshListAsync();
        }
        else if (e.PropertyName == nameof(MainViewModel.Status))
        {
            if (!ModernDetail.IsVisible) _ = Motion.FadeAsync(DetailStatus);
        }
    }

    private int BeginDetailChange()
    {
        var revision = ++_detailRevision;
        // A cancelled discard prompt should leave the current entry fully open, even after a reversal.
        if (View?.HasDetail == true)
        {
            _ = AnimateDetailAsync(open: true);
        }
        return revision;
    }

    private void SynchronizeDetailView()
    {
        var view = View;
        var modern = view is { HasDetail: true }
            && view.Kind is VaultItemKind.Login or VaultItemKind.Server or VaultItemKind.Database;
        var reset = _presentedDetailId != view?.DetailId;
        ModernDetail.IsVisible = modern;
        DetailContent.IsVisible = !modern;
        // 隐藏的旧模板解除绑定，避免同一个字段同时维护两份输入框。
        DetailContent.DataContext = modern ? null : view;
        ModernDetail.SetEditor(modern ? view : null);
        if (modern) ModernDetail.Refresh(resetState: reset);
        _presentedDetailId = view?.DetailId;
        UpdateLegacyTotpSubscription();
    }

    private void UpdateLegacyTotpSubscription()
    {
        var needed = View is { HasDetail: true } && !ModernDetail.IsVisible
            && _activePage == ItemsPage && VaultRoot.IsVisible;
        if (!needed)
        {
            _legacyTotpSubscription?.Dispose();
            _legacyTotpSubscription = null;
        }
        else
        {
            _legacyTotpSubscription ??= TotpTicker.Subscribe(_ => View?.RefreshTotp());
        }
    }

    private void RevealDetail()
    {
        if (View?.HasDetail != true)
        {
            return;
        }

        // Keep the list header compact while the detail drawer is open. The
        // user can still expand filters again after closing the drawer.
        if (_kindFiltersExpanded || _tagFiltersExpanded)
        {
            ResetFilterExpansion();
        }

        SynchronizeDetailView();
        var alreadyVisible = DetailHost.IsVisible;
        _ = AnimateDetailAsync(open: true);
        if (alreadyVisible && !ModernDetail.IsVisible)
        {
            // Switching entries should never displace the header, fields or action buttons.
            _ = Motion.FadeAsync(DetailScroller);
        }
    }

    private async Task<bool> AnimateDetailAsync(bool open)
    {
        CancelDetailAnimation();
        _filterAnimation?.Cancel();
        Motion.Reset(AccountScroller);

        var target = open ? 0 : DetailHost.Width;
        var play = Motion.IsEnabled && ItemsPage.IsEffectivelyVisible;
        // A visible closing drawer must still shield the cards behind it from pointer input.
        DetailHost.IsHitTestVisible = true;
        DetailPanel.IsHitTestVisible = open;
        if (open)
        {
            DetailHost.IsVisible = true;
        }

        using var cancellation = new CancellationTokenSource();
        _detailAnimation = cancellation;
        try
        {
            if (_detailLayoutOpen != open)
            {
                // Hide the single reflow. Never animate the width of WrapPanel or the card list.
                await FadeItemsAsync(0, play ? 60 : 0, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                ApplyDetailLayout(open);
            }

            // Start the clock after the one-time template/layout work, so it cannot consume the slide.
            var start = _detailTranslation.X;
            var distance = Math.Abs(target - start);
            var duration = play && distance > 0.5
                ? TimeSpan.FromMilliseconds(Math.Clamp(260 * distance / DetailHost.Width, 90, 260))
                : TimeSpan.Zero;
            await Task.WhenAll(
                Tween.RunAsync(duration, t => _detailTranslation.X = start + (target - start) * t,
                    Tween.CubicOut, cancellation.Token),
                FadeItemsAsync(1, play ? 140 : 0, cancellation.Token));
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_detailAnimation, cancellation))
            {
                return false;
            }

            _detailTranslation.X = target;
            DetailHost.IsVisible = open;
            DetailHost.IsHitTestVisible = open;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_detailAnimation, cancellation))
            {
                _detailAnimation = null;
            }
        }
    }

    private Task FadeItemsAsync(double target, int milliseconds, CancellationToken token)
    {
        var start = ItemsListPane.Opacity;
        return Tween.RunAsync(TimeSpan.FromMilliseconds(milliseconds * Math.Abs(target - start)),
            t => ItemsListPane.Opacity = start + (target - start) * t, Tween.CubicOut, token);
    }

    private void ApplyDetailLayout(bool open)
    {
        var anchor = CaptureListAnchor();
        _detailLayoutOpen = open;
        ItemsListPane.Margin = new Thickness(20, 44, 20 + (open ? DetailHost.Width : 0), 12);
        ItemsPage.UpdateLayout();

        if (anchor is not { } position || View is not { } view)
        {
            return;
        }

        var rowIndex = -1;
        for (var index = 0; index < view.EntryRows.Count; index++)
        {
            if (view.EntryRows[index].Items.Any(item => item.Id == position.Id))
            {
                rowIndex = index;
                break;
            }
        }
        if (rowIndex < 0)
        {
            return;
        }

        // A pixel offset represents different records after a column change. Keep the visible ID.
        AccountItems.ScrollIntoView(rowIndex);
        ItemsPage.UpdateLayout();
        var card = AccountScroller.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("account-card") && button.Tag is Guid id && id == position.Id);
        if (card?.TranslatePoint(default, AccountScroller) is { } point)
        {
            var maximum = Math.Max(0, AccountScroller.Extent.Height - AccountScroller.Viewport.Height);
            var offset = Math.Clamp(AccountScroller.Offset.Y + point.Y - position.Top, 0, maximum);
            AccountScroller.Offset = new Vector(AccountScroller.Offset.X, offset);
            ItemsPage.UpdateLayout();
        }
    }

    private (Guid Id, double Top)? CaptureListAnchor()
    {
        (Guid Id, double Top)? anchor = null;
        foreach (var card in AccountScroller.GetVisualDescendants().OfType<Button>())
        {
            if (!card.Classes.Contains("account-card") || card.Tag is not Guid id
                || card.TranslatePoint(default, AccountScroller) is not { } point
                || point.Y + card.Bounds.Height <= 0 || point.Y >= AccountScroller.Viewport.Height)
            {
                continue;
            }
            if (anchor is null || point.Y < anchor.Value.Top)
            {
                anchor = (id, point.Y);
            }
        }
        return anchor;
    }

    private void CancelDetailAnimation()
    {
        var cancellation = _detailAnimation;
        _detailAnimation = null;
        cancellation?.Cancel();
    }

    private void ResetDetail()
    {
        CancelDetailAnimation();
        ModernDetail.SetEditor(null);
        ModernDetail.IsVisible = false;
        DetailContent.DataContext = null;
        _presentedDetailId = null;
        _legacyTotpSubscription?.Dispose();
        _legacyTotpSubscription = null;
        DetailHost.IsVisible = false;
        _detailTranslation.X = DetailHost.Width;
        DetailHost.IsHitTestVisible = false;
        DetailPanel.IsHitTestVisible = true;
        _detailLayoutOpen = false;
        ItemsListPane.Margin = new Thickness(20, 44, 20, 12);
        ItemsListPane.Opacity = 1;
        Motion.ResetTree(DetailPanel);
    }

    private async Task RefreshListAsync()
    {
        _filterAnimation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _filterAnimation = cancellation;
        try
        {
            // Coalesce search typing and resize updates; never blank the result list.
            await Task.Delay(70, cancellation.Token);
            if (VaultRoot.IsVisible && _activePage == ItemsPage && _detailAnimation is null)
            {
                await Motion.RefreshAsync(AccountScroller);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_filterAnimation, cancellation))
            {
                _filterAnimation = null;
            }
        }
    }

    private void CancelMotion()
    {
        _detailRevision++;
        ResetDetail();
        _filterAnimation?.Cancel();
        Motion.ResetTree(VaultRoot);
        Motion.Reset(UnlockRoot);
    }

    private void OnLock(object? sender, RoutedEventArgs e) => LockRequested?.Invoke(this, EventArgs.Empty);

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }
        else
        {
            BeginMoveDrag(e);
        }
    }

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

        return await Dialogs.ConfirmDangerAsync(this, "放弃修改", "当前条目还有未保存的修改。要放弃这些修改吗？", "放弃修改");
    }
}
