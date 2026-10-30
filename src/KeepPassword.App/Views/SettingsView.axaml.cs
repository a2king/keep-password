using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Import;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Browser;
using KeepPassword.Core.Update;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class SettingsView : UserControl
{
    private VaultSession? _session;
    private VaultLocation? _location;
    private Action<bool>? _pauseWatcher;
    private CloudSyncCoordinator? _sync;

    public SettingsView()
    {
        InitializeComponent();
        AnimateStatus(LabelStatus);
        AnimateStatus(DirectoryStatus);
        AnimateStatus(KeyStatus);
        AnimateStatus(UpdateStatus);
        AnimateStatus(SyncStatusText);
        ThemeManager.ThemeChanged += UpdateThemeButtons;
        AppUpdateService.Changed += OnAppUpdateChanged;
    }

    private static void AnimateStatus(TextBlock status)
    {
        status.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty && !string.IsNullOrWhiteSpace(status.Text))
            {
                _ = Motion.EnterAsync(status, 3);
            }
        };
    }

    public event EventHandler? VaultChanged;
    public event EventHandler? LockRequested;

    private Window Host => TopLevel.GetTopLevel(this) as Window
        ?? throw new InvalidOperationException("设置页尚未挂到窗口上。");

    public void Show(VaultSession session, VaultLocation location, Action<bool>? pauseWatcher, CloudSyncCoordinator? sync = null)
    {
        if (_sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }

        _session = session;
        _location = location;
        _pauseWatcher = pauseWatcher;
        _sync = sync;
        if (_sync is not null)
        {
            _sync.Changed += OnSyncChanged;
        }

        AccountText.Text = string.IsNullOrWhiteSpace(session.Account)
            ? "本地保险库"
            : "当前账号：" + session.Account;
        DirectoryBox.Text = location.Directory;
        LabelStatus.Text = "";
        DirectoryStatus.Text = "";
        KeyStatus.Text = "";
        ErrorBox.IsVisible = false;
        UpdateThemeButtons(ThemeManager.CurrentTheme);
        UpdateMotionButtons();
        UpdateStartupSwitches();
        UpdateHotkey(null);
        RebuildLabels();
        UpdateEncryptionKeyStatus();
        RefreshUpdateUi();
        RefreshSyncUi();
        Scroller.Offset = default;
    }

    private void OnAppUpdateChanged() => Dispatcher.UIThread.Post(RefreshUpdateUi);

    private void RefreshUpdateUi()
    {
        var current = AppUpdateService.CurrentVersion;
        var latest = AppUpdateService.Latest?.Version;
        UpdateVersionText.Text = latest is null
            ? $"当前版本 v{current}"
            : $"当前版本 v{current}，最新版本 v{latest}";

        if (!AppUpdateService.CanAutoInstall)
        {
            UpdateVersionText.Text += "。当前为便携运行，自动安装不可用，请到 GitHub 下载安装包。";
        }

        UpdateStatus.Text = AppUpdateService.StatusMessage
            ?? (AppUpdateService.CanAutoInstall
                ? "可检查 GitHub 上是否有新版本。"
                : $"发布页：{UpdatePaths.ReleasesPageUrl}");

        var phase = AppUpdateService.Phase;
        UpdateProgress.IsVisible = phase == AppUpdatePhase.Downloading;
        UpdateProgress.Value = AppUpdateService.DownloadProgress;
        UpdateCheckBtn.IsEnabled = phase is not AppUpdatePhase.Checking and not AppUpdatePhase.Downloading;
        UpdateDownloadBtn.IsVisible = phase == AppUpdatePhase.Available && AppUpdateService.CanAutoInstall;
        UpdateCancelBtn.IsVisible = phase == AppUpdatePhase.Downloading;
        UpdateInstallBtn.IsVisible = phase == AppUpdatePhase.Ready && AppUpdateService.CanAutoInstall;
    }

    private async void OnCheckUpdate(object? sender, RoutedEventArgs e)
    {
        UpdateCheckBtn.IsEnabled = false;
        UpdateStatus.Text = "正在检查更新…";
        await AppUpdateService.CheckAsync(silent: false);
        RefreshUpdateUi();
    }

    private async void OnDownloadUpdate(object? sender, RoutedEventArgs e)
    {
        await AppUpdateService.DownloadAsync();
        RefreshUpdateUi();
    }

    private void OnCancelDownload(object? sender, RoutedEventArgs e) => AppUpdateService.CancelDownload();

    private void OnInstallUpdate(object? sender, RoutedEventArgs e)
    {
        var error = AppUpdateService.TryStartInstaller();
        if (error is not null)
        {
            UpdateStatus.Text = error;
        }
    }

    private void OnOpenGitHubReleases(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdatePaths.ReleasesPageUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "无法打开浏览器：" + ex.Message;
        }
    }

    private void OnOpenEdgeExtensionStore(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = BrowserExtensionLinks.EdgeStoreUrl,
                UseShellExecute = true
            });
            BrowserExtensionStatus.Text =
                BrowserExtensionLinks.EdgeStoreIsPrivate
                    ? $"已打开商店页。私有扩展需连接后安装；商店标识 {BrowserExtensionLinks.EdgeStoreId}。"
                    : "已打开 Edge 扩展商店页。";
        }
        catch (Exception ex)
        {
            BrowserExtensionStatus.Text = "无法打开浏览器：" + ex.Message;
        }
    }

    private async void OnCopyEdgeStoreId(object? sender, RoutedEventArgs e)
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top?.Clipboard is null)
            {
                BrowserExtensionStatus.Text = "无法访问剪贴板。";
                return;
            }

            await top.Clipboard.SetTextAsync(BrowserExtensionLinks.EdgeStoreId);
            BrowserExtensionStatus.Text = $"已复制商店标识：{BrowserExtensionLinks.EdgeStoreId}";
        }
        catch (Exception ex)
        {
            BrowserExtensionStatus.Text = "复制失败：" + ex.Message;
        }
    }

    public void Clear()
    {
        if (_sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }

        _session = null;
        _location = null;
        _pauseWatcher = null;
        _sync = null;
        ClearSecrets();
        ClearSyncSecrets();
        NewSpaceBox.Text = "";
        NewTagBox.Text = "";
        SpaceList.Children.Clear();
        TagList.Children.Clear();
        AccountText.Text = "";
        LabelStatus.Text = "";
        DirectoryStatus.Text = "";
        KeyStatus.Text = "";
        ErrorBox.IsVisible = false;
    }

    private void ClearSecrets()
    {
        NewKeyBox.Text = "";
        ConfirmBox.Text = "";
        MasterBox.Text = "";
        CurrentMasterBox.Text = "";
        NewMasterBox.Text = "";
        ConfirmMasterBox.Text = "";
    }

    private void RebuildLabels()
    {
        if (_session is null)
        {
            return;
        }

        var entries = _session.Entries;
        FillLabelList(
            SpaceList,
            _session.Spaces,
            name => entries.Count(entry => LabelName.Comparer.Equals(entry.Space, name)),
            isSpace: true);
        FillLabelList(
            TagList,
            _session.Tags,
            name => entries.Count(entry => entry.Tags.Contains(name, LabelName.Comparer)),
            isSpace: false);
        _ = Motion.EnterAsync(SpaceList, 4);
        _ = Motion.EnterAsync(TagList, 4);
    }

    private void FillLabelList(StackPanel panel, IReadOnlyList<string> names, Func<string, int> count, bool isSpace)
    {
        panel.Children.Clear();
        if (names.Count == 0)
        {
            var empty = new TextBlock { Text = isSpace ? "还没有空间分类。" : "还没有账号标签。" };
            empty.Classes.Add("muted");
            panel.Children.Add(empty);
            return;
        }

        foreach (var name in names)
        {
            var box = new TextBox { Text = name };
            var usage = new TextBlock
            {
                Text = $"{count(name)} 个账号",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Avalonia.Thickness(10, 0, 4, 0)
            };
            usage.Classes.Add("muted");
            var rename = new Button { Content = "修改名称", Margin = new Avalonia.Thickness(4, 0, 0, 0) };
            rename.Classes.Add("soft");
            rename.Click += (_, _) => RenameLabel(name, box.Text ?? "", isSpace);
            var delete = new Button { Content = "删除", Margin = new Avalonia.Thickness(4, 0, 0, 0) };
            delete.Classes.Add("danger");
            delete.Click += async (_, _) => await DeleteLabelAsync(name, count(name), isSpace);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
            Grid.SetColumn(usage, 1);
            Grid.SetColumn(rename, 2);
            Grid.SetColumn(delete, 3);
            row.Children.Add(box);
            row.Children.Add(usage);
            row.Children.Add(rename);
            row.Children.Add(delete);
            panel.Children.Add(row);
        }
    }

    private void OnAddSpace(object? sender, RoutedEventArgs e) => AddLabel(NewSpaceBox, isSpace: true);

    private void OnAddTag(object? sender, RoutedEventArgs e) => AddLabel(NewTagBox, isSpace: false);

    private void AddLabel(TextBox box, bool isSpace)
    {
        if (_session is null)
        {
            return;
        }

        ApplyLabelChange(() =>
        {
            var name = isSpace ? _session.AddSpace(box.Text ?? "") : _session.AddTag(box.Text ?? "");
            box.Text = "";
            return $"已新增「{name}」。";
        });
    }

    private void RenameLabel(string oldName, string newName, bool isSpace)
    {
        if (_session is null)
        {
            return;
        }

        ApplyLabelChange(() =>
        {
            if (isSpace)
            {
                _session.RenameSpace(oldName, newName);
            }
            else
            {
                _session.RenameTag(oldName, newName);
            }

            return $"已把「{oldName}」改为「{LabelName.Normalize(newName)}」，相关账号已同步。";
        });
    }

    private async Task DeleteLabelAsync(string name, int usage, bool isSpace)
    {
        if (_session is null)
        {
            return;
        }

        var effect = isSpace ? "这些账号会变为未分类。" : "这些账号会去掉该标签。";
        var message = usage == 0 ? $"确定删除「{name}」吗？" : $"「{name}」正被 {usage} 个账号使用，{effect}确定删除吗？";
        if (!await Dialogs.ConfirmAsync(Host, isSpace ? "删除空间分类" : "删除账号标签", message))
        {
            return;
        }

        ApplyLabelChange(() =>
        {
            if (isSpace)
            {
                _session.DeleteSpace(name);
            }
            else
            {
                _session.DeleteTag(name);
            }

            return $"已删除「{name}」。";
        });
    }

    private void ApplyLabelChange(Func<string> change)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var message = change();
            _session.Save();
            VaultChanged?.Invoke(this, EventArgs.Empty);
            LabelStatus.Text = message;
            RebuildLabels();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            LabelStatus.Text = ex.Message;
        }
    }

    private void OnThemeLight(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeManager.LightTheme);
    private void OnThemeDark(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeManager.DarkTheme);
    private void OnThemeSystem(object? sender, RoutedEventArgs e) => ApplyTheme(ThemeManager.SystemTheme);

    private void OnMotionFull(object? sender, RoutedEventArgs e) => ApplyMotion(MotionPreferences.FullMode);
    private void OnMotionSystem(object? sender, RoutedEventArgs e) => ApplyMotion(MotionPreferences.SystemMode);
    private void OnMotionReduced(object? sender, RoutedEventArgs e) => ApplyMotion(MotionPreferences.ReducedMode);

    private void OnLaunchAtStartupChanged(object? sender, RoutedEventArgs e)
    {
        if (_suspendStartupUi)
        {
            return;
        }

        var enabled = LaunchAtStartupSwitch.IsChecked == true;
        var error = StartupPreferences.SetLaunchAtStartup(enabled);
        StartMinimizedSwitch.IsEnabled = enabled;
        StartupStatus.Text = error ?? (enabled
            ? "已开启开机启动" + (StartupPreferences.StartMinimized ? "（最小化到托盘）。" : "。")
            : "已关闭开机启动。");
    }

    private void OnStartMinimizedChanged(object? sender, RoutedEventArgs e)
    {
        if (_suspendStartupUi)
        {
            return;
        }

        var enabled = StartMinimizedSwitch.IsChecked == true;
        var error = StartupPreferences.SetStartMinimized(enabled);
        StartupStatus.Text = error ?? (enabled
            ? "开机时将最小化到托盘。"
            : "开机时将显示主窗口。");
    }

    private bool _suspendStartupUi;

    private void UpdateStartupSwitches()
    {
        _suspendStartupUi = true;
        try
        {
            LaunchAtStartupSwitch.IsChecked = StartupPreferences.LaunchAtStartup;
            StartMinimizedSwitch.IsChecked = StartupPreferences.StartMinimized;
            StartMinimizedSwitch.IsEnabled = StartupPreferences.LaunchAtStartup;
            StartupStatus.Text = "";
        }
        finally
        {
            _suspendStartupUi = false;
        }
    }

    private void ApplyMotion(string mode)
    {
        try
        {
            MotionPreferences.SetMode(mode);
            UpdateMotionButtons();
            _ = Motion.RefreshAsync(MotionStatus);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MotionStatus.Text = "动画设置未能保存：" + ex.Message;
        }
    }

    private void UpdateMotionButtons()
    {
        var mode = MotionPreferences.CurrentMode;
        SetSegment(MotionFullBtn, mode == MotionPreferences.FullMode);
        SetSegment(MotionSystemBtn, mode == MotionPreferences.SystemMode);
        SetSegment(MotionReducedBtn, mode == MotionPreferences.ReducedMode);
        MotionStatus.Text = mode switch
        {
            MotionPreferences.ReducedMode => "立即切换页面和面板，保留基础操作反馈。",
            MotionPreferences.SystemMode => MotionPreferences.IsEnabled
                ? "当前系统允许动画，过渡效果已开启。"
                : "当前系统已关闭动画，过渡效果也会关闭。",
            _ => "已开启页面、面板与弹窗的过渡动画。"
        };
    }

    private void OnHotkeyFocus(object? sender, GotFocusEventArgs e)
    {
        AutofillHotkeyPreferences.Suspend();
        HotkeyBox.Text = "";
        HotkeyStatus.Text = "按下新的组合键，例如 Ctrl+Alt+K；按 Esc 取消。";
    }

    private void OnHotkeyBlur(object? sender, RoutedEventArgs e)
    {
        AutofillHotkeyPreferences.Resume();
        HotkeyBox.Text = AutofillHotkeyPreferences.Current.ToString();
    }

    private void OnHotkeyKeyDown(object? sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            EndHotkeyCapture();
            UpdateHotkey(null);
            return;
        }

        var key = e.Key switch
        {
            >= Key.A and <= Key.Z or >= Key.F1 and <= Key.F12 => e.Key.ToString(),
            >= Key.D0 and <= Key.D9 => ((char)('0' + (e.Key - Key.D0))).ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (e.Key - Key.NumPad0))).ToString(),
            Key.Space => "Space",
            _ => null
        };
        if (key is null)
        {
            return;
        }

        var modifiers = HotkeyModifiers.None;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            modifiers |= HotkeyModifiers.Ctrl;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            modifiers |= HotkeyModifiers.Win;
        }

        ApplyHotkey(new AutofillHotkey(modifiers, key));
    }

    private void OnHotkeyReset(object? sender, RoutedEventArgs e) => ApplyHotkey(AutofillHotkey.Default);

    private void ApplyHotkey(AutofillHotkey hotkey)
    {
        string? error;
        try
        {
            error = AutofillHotkeyPreferences.TryApply(hotkey);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = "快捷键设置未能保存：" + ex.Message;
        }

        UpdateHotkey(error);
        if (error is null)
        {
            EndHotkeyCapture();
        }
        else
        {
            AutofillHotkeyPreferences.Suspend();
        }
    }

    private void EndHotkeyCapture() => TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();

    private void UpdateHotkey(string? error)
    {
        HotkeySection.IsVisible = AutofillHotkeyPreferences.IsSupported;
        HotkeyBox.Text = AutofillHotkeyPreferences.Current.ToString();
        HotkeyStatus.Text = error
            ?? (AutofillHotkeyPreferences.IsActive
                ? $"已启用：在目标程序的密码框里按 {AutofillHotkeyPreferences.Current}。"
                : $"{AutofillHotkeyPreferences.Current} 已被其它程序占用，快捷键暂不可用，请换一个组合。");
    }

    private void ApplyTheme(string theme)
    {
        ThemeManager.SetTheme(theme);
        UpdateThemeButtons(theme);
    }

    private void UpdateThemeButtons(string theme)
    {
        SetSegment(ThemeLightBtn, theme == ThemeManager.LightTheme);
        SetSegment(ThemeDarkBtn, theme == ThemeManager.DarkTheme);
        SetSegment(ThemeSystemBtn, theme == ThemeManager.SystemTheme);
    }

    private static void SetSegment(Button button, bool selected)
    {
        if (selected)
        {
            button.Classes.Add("selected");
        }
        else
        {
            button.Classes.Remove("selected");
        }
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        NewKeyBox.Text = "";
        ConfirmBox.Text = "";
        MasterBox.Text = "";
        KeyStatus.Text = "";
        ErrorBox.IsVisible = false;
    }

    private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await SafeStoragePickers.PickFolderAsync(Host, "选择缓存目录", _pauseWatcher);
            if (!string.IsNullOrWhiteSpace(path))
            {
                DirectoryBox.Text = path;
            }
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
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
            ErrorBox.IsVisible = false;
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
        ErrorBox.IsVisible = false;
        KeyStatus.Text = "";
        try
        {
            var session = _session;
            await Task.Run(() =>
            {
                if (session.HasMasterPassword)
                {
                    session.ChangeShortKey(master, next);
                }
                else
                {
                    session.ChangeShortKeyWithCurrent(master, next);
                }
            });
            NewKeyBox.Text = "";
            ConfirmBox.Text = "";
            KeyStatus.Text = "短密钥已更新。";
        }
        catch (Exception ex) when (ex is CredentialRejectedException or ArgumentException or InvalidOperationException)
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

    private async void OnUpgradeLocalKey(object? sender, RoutedEventArgs e)
    {
        if (_session is not { IsUnlocked: true } session)
        {
            return;
        }

        try
        {
            if (session.NeedsKeyFileSave)
            {
                DirectoryStatus.Text = "请先永久保存加密密钥文件，再升级格式。";
                await SavePendingKeyAsync(session);
                if (_session != session || !session.IsUnlocked || session.NeedsKeyFileSave)
                {
                    return;
                }
            }

            var ok = await Dialogs.ConfirmAsync(Host,
                "升级到本地密钥格式",
                "升级后将去掉主密码保护层，本机用短密钥解锁，换电脑或忘记短密钥时使用加密密钥文件。此操作不可逆，确认已妥善保管 .kpkey 后再继续。");
            if (!ok || _session != session || !session.IsUnlocked)
            {
                return;
            }

            await Task.Run(session.UpgradeToLocalKeyFormat);
            if (_session != session || !session.IsUnlocked)
            {
                return;
            }

            DirectoryStatus.Text = "已升级为本地密钥格式。";
            UpdateEncryptionKeyStatus();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnChangeMaster(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var next = NewMasterBox.Text ?? "";
        if (next.Length == 0 || next != (ConfirmMasterBox.Text ?? ""))
        {
            ShowError("请填写一致的新主密码。");
            return;
        }

        try
        {
            var current = CurrentMasterBox.Text ?? "";
            var session = _session;
            await Task.Run(() => session.ChangeMasterPassword(current, next));
            if (_session != session || !session.IsUnlocked) return;
            CurrentMasterBox.Text = "";
            NewMasterBox.Text = "";
            ConfirmMasterBox.Text = "";
            DirectoryStatus.Text = session.ShortUnlockReady
                ? "主密码已更换。短密钥仍可解锁，加密密钥保持不变。"
                : "主密码已更换。下次使用新主密码解锁，加密密钥保持不变。";
            ErrorBox.IsVisible = false;
            UpdateEncryptionKeyStatus();
            if (session.NeedsKeyFileSave)
            {
                DirectoryStatus.Text = "已升级为独立加密密钥，请永久保存生成的密钥文件。";
                await SavePendingKeyAsync(session);
            }
        }
        catch (Exception ex) when (ex is CredentialRejectedException or ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_session is not { IsUnlocked: true } session)
        {
            return;
        }

        try
        {
            if (!session.UsesIndependentKey)
                throw new InvalidOperationException("请先更换主密码并保存加密密钥，再导出新版加密备份。");
            var path = await SafeStoragePickers.PickSaveFileAsync(
                Host,
                "导出加密备份",
                "keep-password-backup.kpvault",
                [new FilePickerFileType("Keep Password 保险库") { Patterns = ["*.kpvault"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path) || _session != session || !session.IsUnlocked)
            {
                return;
            }

            session.EnsureExternalPath(path);
            EncryptedBackup.Export(session.VaultPath, path);
            DirectoryStatus.Text = "已导出加密备份。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        var targetSession = _session;
        if (!await Dialogs.ConfirmAsync(Host, "还原加密备份", "还原会替换当前保险库中的全部条目。请准备该备份对应的加密密钥文件；还原后使用当前保险库的加密密钥保存。"))
        {
            return;
        }

        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(
                Host,
                "选择加密备份",
                [new FilePickerFileType("Keep Password 保险库") { Patterns = ["*.kpvault"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            if (_session != targetSession || !targetSession.IsUnlocked) return;
            var encryptionKey = await EncryptionKeyDialog.AskAsync(Host, "验证备份的加密密钥", targetSession, _pauseWatcher, verifyCurrent: false);
            if (encryptionKey is null || _session != targetSession || !targetSession.IsUnlocked)
            {
                return;
            }

            var count = await Task.Run(() => targetSession.RestoreBackup(path, encryptionKey));
            if (_session != targetSession || !targetSession.IsUnlocked)
            {
                return;
            }

            VaultChanged?.Invoke(this, EventArgs.Empty);
            RebuildLabels();
            DirectoryStatus.Text = $"已还原 {count} 条，并使用当前保险库的加密密钥保存。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is CredentialRejectedException or UnlockFailedException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnExportCsv(object? sender, RoutedEventArgs e)
    {
        if (_session is not { IsUnlocked: true } exportSession)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Host, "导出 CSV", "CSV 包含全部明文密码、验证码密钥和私钥。文件不会加密，导出后请自行保管。确定导出吗？"))
        {
            return;
        }

        if (_session != exportSession || !exportSession.IsUnlocked)
        {
            return;
        }

        try
        {
            var encryptionKey = await EncryptionKeyDialog.AskAsync(Host, "验证加密密钥后导出明文", exportSession, _pauseWatcher);
            if (encryptionKey is null || _session != exportSession || !exportSession.IsUnlocked) return;
            var path = await SafeStoragePickers.PickSaveFileAsync(
                Host,
                "导出 CSV",
                "KeepPassword-导出.csv",
                [new FilePickerFileType("CSV 文件") { Patterns = ["*.csv"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path) || _session != exportSession || !exportSession.IsUnlocked)
            {
                return;
            }

            exportSession.EnsureExternalPath(path);
            var csv = exportSession.ExportCsv(encryptionKey);
            await Task.Run(() => File.WriteAllText(path, csv, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
            DirectoryStatus.Text = "已导出 CSV。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or CredentialRejectedException or InvalidOperationException)
        {
            ShowError(ex.Message);
        }
    }

    private void UpdateEncryptionKeyStatus()
    {
        if (_session is not { IsUnlocked: true } session) return;
        var localKey = !session.HasMasterPassword && session.FormatVersion >= VaultStore.FormatVersion;
        MasterPasswordSection.IsVisible = session.HasMasterPassword || session.NeedsLocalKeyUpgrade;
        ShortKeyHint.Text = localKey
            ? "提交前必须验证当前短密钥。自动填入时仍会再确认一次。"
            : "提交前必须验证当前主密码。短密钥也可以解锁保险库，自动填入时仍会再确认一次。";
        VerifyLabel.Text = localKey ? "当前短密钥" : "当前主密码";
        BusyText.Text = localKey ? "正在验证短密钥…" : "正在验证主密码…";
        MasterChangeHint.Text = session.UsesIndependentKey
            ? (session.NeedsLocalKeyUpgrade
                ? "可先继续使用主密码。建议升级到本地密钥格式：去掉主密码保护层，仅保留短密钥与加密密钥文件。"
                : "更换主密码不会改变加密密钥，之前保存的密钥文件仍然有效。")
            : "此保险库仍使用旧版加密。更换一次主密码将升级为独立随机密钥，并提示保存密钥文件。";
        EncryptionKeyStatus.Text = !session.UsesIndependentKey
            ? "尚未升级。请先更换一次主密码，生成并永久保存独立加密密钥。"
            : session.NeedsKeyFileSave ? "密钥文件尚未保存，请选择文件夹完成保存。"
            : localKey
                ? "密钥文件已保存。换电脑、忘记短密钥、还原备份和明文导出时，请提供你永久保管的密钥。"
                : "密钥文件已保存。还原备份、明文导出和重置主密码时，请提供你永久保管的密钥。";
        SavePendingKeyButton.IsVisible = session.NeedsKeyFileSave;
        UpgradeLocalKeyButton.IsVisible = session.NeedsLocalKeyUpgrade && !session.NeedsKeyFileSave;
    }

    private async void OnSavePendingKey(object? sender, RoutedEventArgs e)
    {
        if (_session is { IsUnlocked: true } session) await SavePendingKeyAsync(session);
    }

    private async Task SavePendingKeyAsync(VaultSession session)
    {
        if (!await EncryptionKeyDialog.SaveNewAsync(Host, session, _pauseWatcher))
        {
            if (_session == session) LockRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        if (_session == session) UpdateEncryptionKeyStatus();
    }

    private void OnSyncChanged() => Dispatcher.UIThread.Post(RefreshSyncUi);

    private void RefreshSyncUi()
    {
        var sync = _sync;
        if (sync is null)
        {
            SyncStatusText.Text = "云同步服务未就绪。";
            SyncBoundPanel.IsVisible = false;
            SyncBindPanel.IsVisible = true;
            return;
        }

        SyncStatusText.Text = sync.StatusMessage ?? "";
        SyncBoundPanel.IsVisible = sync.IsBound;
        SyncBindPanel.IsVisible = !sync.IsBound;
        if (sync.IsBound)
        {
            SyncAccountText.Text = sync.Email ?? "";
            SyncMetaText.Text =
                $"云端版本 {sync.VaultVersion}"
                + (string.IsNullOrEmpty(sync.BindingKeyFingerprint)
                    ? ""
                    : $" · 绑定密钥指纹 {sync.BindingKeyFingerprint}");
        }
    }

    private async void OnSyncSendCode(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        try
        {
            await _sync.RequestBindCodeAsync(SyncEmailBox.Text ?? "");
        }
        catch (Exception ex) when (ex is SyncApiException or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnPickBindingKey(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(Host, "选择绑定密钥文件",
            [
                new FilePickerFileType("Keep Password 绑定密钥") { Patterns = ["*.kpbind"] }
            ], _pauseWatcher);
            if (!string.IsNullOrWhiteSpace(path))
            {
                SyncBindingKeyBox.Text = File.ReadAllText(path);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnSyncBind(object? sender, RoutedEventArgs e)
    {
        if (_sync is null || _session is null)
        {
            return;
        }

        if (_session.NeedsLocalKeyUpgrade)
        {
            ShowError("请先升级到本地密钥格式，再开启云同步。");
            return;
        }

        try
        {
            await _sync.BindAsync(
                SyncEmailBox.Text ?? "",
                SyncPasswordBox.Text ?? "",
                SyncBindingKeyBox.Text ?? "",
                SyncCodeBox.Text ?? "");
            ClearSyncSecrets();
            VaultChanged?.Invoke(this, EventArgs.Empty);
            RefreshSyncUi();
        }
        catch (Exception ex) when (ex is SyncApiException or InvalidOperationException or InvalidDataException
                                       or ArgumentException or HttpRequestException or TaskCanceledException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnSyncRefresh(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        try
        {
            await _sync.RefreshAsync();
            VaultChanged?.Invoke(this, EventArgs.Empty);
            RefreshSyncUi();
        }
        catch (Exception ex) when (ex is SyncApiException or InvalidDataException or HttpRequestException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnSyncDisconnect(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmAsync(Host, "断开云同步", "断开后本机不再上传/下载。本地保险库保留，可随时重新绑定。"))
        {
            return;
        }

        await _sync.DisconnectAsync();
        RefreshSyncUi();
    }

    private void ClearSyncSecrets()
    {
        SyncPasswordBox.Text = "";
        SyncBindingKeyBox.Text = "";
        SyncCodeBox.Text = "";
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }
}
