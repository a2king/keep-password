using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Controls;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Import;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Browser;
using KeepPassword.Core.Install;
using KeepPassword.Core.Update;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class SettingsView : UserControl
{
    private VaultSession? _session;
    private MainViewModel? _vault;
    private VaultLocation? _location;
    private Action<bool>? _pauseWatcher;
    private CloudSyncCoordinator? _sync;

    // 初始为 true：XAML 加载时分段控件会自动选中第 0 项，此时不能当作用户选择（否则会误切到"浅色"）。
    private bool _syncingSegments = true;

    public SettingsView()
    {
        InitializeComponent();
        SelectSegment(ThemeSegments, Array.IndexOf(ThemeOrder, ThemeManager.CurrentTheme));
        SelectSegment(MotionSegments, Array.IndexOf(MotionOrder, MotionPreferences.CurrentMode));
        AnimateStatus(LabelStatus);
        AnimateStatus(DirectoryStatus);
        AnimateStatus(CsvStatus);
        AnimateStatus(KeyStatus);
        AnimateStatus(UpdateStatus);
        AnimateStatus(SyncStatusText);
        ThemeManager.ThemeChanged += UpdateThemeButtons;
        AppUpdateService.Changed += OnAppUpdateChanged;
        _syncingSegments = false;
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

    public void Show(VaultSession session, VaultLocation location, Action<bool>? pauseWatcher, CloudSyncCoordinator? sync = null, MainViewModel? vault = null)
    {
        if (_sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }

        _session = session;
        _vault = vault;
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
        CsvStatus.Text = "";
        UpdateUndoImport();
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
            BrowserExtensionStatus.Text = "已打开 Edge 扩展商店。";
        }
        catch (Exception ex)
        {
            BrowserExtensionStatus.Text = "无法打开浏览器：" + ex.Message;
        }
    }

    private void OnRepairEdgeExtension(object? sender, RoutedEventArgs e)
    {
        BrowserExtensionStatus.Text = NativeHostRegistration.RepairInstalledHost();
    }

    public void Clear()
    {
        if (_sync is not null)
        {
            _sync.Changed -= OnSyncChanged;
        }

        _session = null;
        _vault = null;
        _location = null;
        _pauseWatcher = null;
        _sync = null;
        ClearSecrets();
        ClearSyncSecrets();
        NewSpaceBox.Text = "";
        NewTagBox.Text = "";
        ClearChips(SpaceList);
        ClearChips(TagList);
        AccountText.Text = "";
        LabelStatus.Text = "";
        DirectoryStatus.Text = "";
        CsvStatus.Text = "";
        KeyStatus.Text = "";
        ErrorBox.IsVisible = false;
        UpdateUndoImport();
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

    // 芯片式列表：每个名称是一个小芯片（名称 + 使用数 + 改名 / 删除图标），XAML 里的新增输入框固定在最后。
    private void FillLabelList(WrapPanel panel, IReadOnlyList<string> names, Func<string, int> count, bool isSpace)
    {
        ClearChips(panel);
        var index = 0;
        foreach (var name in names)
        {
            panel.Children.Insert(index++, LabelChip(name, count(name), isSpace));
        }
    }

    // 只移除芯片，保留末尾的新增输入框。
    private static void ClearChips(WrapPanel panel)
    {
        foreach (var chip in panel.Children.OfType<Border>().ToList())
        {
            panel.Children.Remove(chip);
        }
    }

    private Border LabelChip(string name, int usage, bool isSpace)
    {
        // 名称用 TextBlock 作为按钮内容，悬停时才能单独给文字加下划线。
        var title = new Button { Content = new TextBlock { Text = name }, Classes = { "label-chip-name" } };
        ToolTip.SetTip(title, "点击改名");
        var editor = new TextBox { Text = name, Classes = { "label-chip-input" }, IsVisible = false };
        Avalonia.Automation.AutomationProperties.SetName(editor, $"重命名「{name}」");
        Avalonia.Automation.AutomationProperties.SetName(title, name);
        var countText = new TextBlock { Text = usage.ToString(), Classes = { "label-chip-count" } };
        ToolTip.SetTip(countText, $"{usage} 个账号在用");
        var rename = ChipIcon("Detail.Icon.Edit", $"重命名「{name}」", danger: false);
        var delete = ChipIcon("Detail.Icon.Close", $"删除「{name}」", danger: true);
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { title, editor, countText, rename, delete } };
        var chip = new Border { Classes = { "label-chip" }, Child = row };
        // 按名称取同一色相的浅底 / 深字 / 描边；同名在任何地方都是同一种颜色，切换亮暗主题时自动换成对应的一套。
        chip.Bind(Border.BackgroundProperty, this.GetResourceObservable(LabelPalette.Background(name)));
        chip.Bind(Border.BorderBrushProperty, this.GetResourceObservable(LabelPalette.Border(name)));
        foreach (var button in new[] { title, rename, delete })
        {
            button.Bind(Button.ForegroundProperty, this.GetResourceObservable(LabelPalette.Foreground(name)));
        }
        countText.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(LabelPalette.Foreground(name)));
        var done = true;

        void ShowEditor(bool editing)
        {
            chip.Classes.Set("editing", editing);
            title.IsVisible = countText.IsVisible = rename.IsVisible = !editing;
            editor.IsVisible = editing;
        }
        void BeginRename()
        {
            done = false;
            editor.Text = name;
            ShowEditor(true);
            editor.Focus();
            editor.SelectAll();
        }
        // 回车与失去焦点都会走到这里，done 防止同一次改名提交两遍。
        void EndRename(bool save)
        {
            if (done) return;
            done = true;
            var value = editor.Text ?? "";
            ShowEditor(false);
            if (save && !string.IsNullOrWhiteSpace(value) && LabelName.Normalize(value) != name)
            {
                RenameLabel(name, value, isSpace);
            }
        }

        title.Click += (_, _) => BeginRename();
        rename.Click += (_, _) => BeginRename();
        delete.Click += async (_, _) => await DeleteLabelAsync(name, usage, isSpace);
        editor.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            EndRename(save: e.Key == Key.Enter);
        };
        editor.LostFocus += (_, _) => EndRename(save: true);
        return chip;
    }

    // 芯片上的小图标按钮：22px 圆形，图标 12px，常驻显示；删除图标悬停时变红。
    private Button ChipIcon(string resource, string hint, bool danger)
    {
        // × 按 16px 网格的原始尺寸绘制（视觉 7px），铅笔缩放到 11px，两者视觉重量一致、不抢名称。
        var icon = danger
            ? new Avalonia.Controls.Shapes.Path { Width = 16, Height = 16, Stretch = Avalonia.Media.Stretch.None }
            : new Avalonia.Controls.Shapes.Path { Width = 11, Height = 11, Stretch = Avalonia.Media.Stretch.Uniform };
        icon.StrokeThickness = 1.3;
        icon.StrokeLineCap = Avalonia.Media.PenLineCap.Round;
        icon.StrokeJoin = Avalonia.Media.PenLineJoin.Round;
        icon.Bind(Avalonia.Controls.Shapes.Path.DataProperty, this.GetResourceObservable(resource));
        icon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, new Avalonia.Data.Binding("Foreground")
            { RelativeSource = new Avalonia.Data.RelativeSource(Avalonia.Data.RelativeSourceMode.FindAncestor) { AncestorType = typeof(Avalonia.Controls.Presenters.ContentPresenter) } });
        var button = new Button { Content = icon, Classes = { "label-chip-icon" } };
        if (danger) button.Classes.Add("danger-icon");
        ToolTip.SetTip(button, hint);
        Avalonia.Automation.AutomationProperties.SetName(button, hint);
        return button;
    }

    private void OnAddSpaceKeyDown(object? sender, KeyEventArgs e) => OnAddKeyDown(e, NewSpaceBox, isSpace: true);

    private void OnAddTagKeyDown(object? sender, KeyEventArgs e) => OnAddKeyDown(e, NewTagBox, isSpace: false);

    // 在末尾输入框里回车即新增；Esc 清空输入。
    private void OnAddKeyDown(KeyEventArgs e, TextBox box, bool isSpace)
    {
        if (e.Key == Key.Escape) { box.Text = ""; e.Handled = true; return; }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!string.IsNullOrWhiteSpace(box.Text)) AddLabel(box, isSpace);
    }

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
        if (!await Dialogs.ConfirmDangerAsync(Host, isSpace ? "删除空间分类" : "删除账号标签", message))
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

    private static readonly string[] ThemeOrder = [ThemeManager.LightTheme, ThemeManager.DarkTheme, ThemeManager.SystemTheme];
    private static readonly string[] MotionOrder = [MotionPreferences.FullMode, MotionPreferences.SystemMode, MotionPreferences.ReducedMode];

    // 分段切换只把选中索引翻译成原来的 ApplyTheme / ApplyMotion；程序同步选中项时不重复保存设置。
    private void OnThemeSegmentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSegments || ThemeSegments is null || ThemeSegments.SelectedIndex is < 0 or >= 3) return;
        var theme = ThemeOrder[ThemeSegments.SelectedIndex];
        if (theme != ThemeManager.CurrentTheme) ApplyTheme(theme);
    }

    private void OnMotionSegmentChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingSegments || MotionSegments is null || MotionSegments.SelectedIndex is < 0 or >= 3) return;
        var mode = MotionOrder[MotionSegments.SelectedIndex];
        if (mode != MotionPreferences.CurrentMode) ApplyMotion(mode);
    }

    private void SelectSegment(SegmentedControl segments, int index)
    {
        var previous = _syncingSegments;
        _syncingSegments = true;
        try { segments.SelectedIndex = Math.Max(0, index); }
        finally { _syncingSegments = previous; }
    }

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
        SelectSegment(MotionSegments, Array.IndexOf(MotionOrder, mode));
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
        SelectSegment(ThemeSegments, Array.IndexOf(ThemeOrder, theme));
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

            var ok = await Dialogs.ConfirmDangerAsync(Host,
                "升级到本地密钥格式",
                "升级后将去掉主密码保护层，本机用短密钥解锁，换电脑或忘记短密钥时使用加密密钥文件。此操作不可逆，确认已妥善保管 .kpkey 后再继续。",
                "确认升级");
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
        if (!await Dialogs.ConfirmDangerAsync(Host, "还原加密备份", "还原会替换当前保险库中的全部条目。请准备该备份对应的加密密钥文件；还原后使用当前保险库的加密密钥保存。", "替换并还原"))
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

    private async void OnImportCsv(object? sender, RoutedEventArgs e)
    {
        if (_vault is null)
        {
            return;
        }

        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(
                Host,
                "导入 CSV",
                [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*.tsv", "*.txt"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path) || _vault is null)
            {
                return;
            }

            var text = await File.ReadAllTextAsync(path);
            var table = CsvImporter.ReadTable(text);
            if (table.Headers.Count == 0 || table.Rows.Count == 0)
            {
                await Dialogs.AlertAsync(Host, "无法导入", "CSV 文件为空，或只有表头没有数据行。");
                return;
            }

            var entries = await new ImportWizardWindow(table, Path.GetFileName(path)).ShowDialog<IReadOnlyList<VaultEntry>?>(Host);
            if (entries is { Count: > 0 } && _vault is not null)
            {
                var count = _vault.ApplyImport(entries);
                CsvStatus.Text = $"已导入 {count} 条。可在本次打开期间撤销。";
                ErrorBox.IsVisible = false;
                UpdateUndoImport();
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnUndoCsvImport(object? sender, RoutedEventArgs e)
    {
        if (_vault is not { CanUndoImport: true })
        {
            return;
        }

        if (!await Dialogs.ConfirmDangerAsync(Host, "撤销导入", "将删除上一次导入创建的全部条目（导入后修改过的也会删除）。确定撤销吗？", "撤销导入"))
        {
            return;
        }

        var removed = _vault.UndoImport();
        CsvStatus.Text = $"已撤销导入，移除 {removed} 条。";
        ErrorBox.IsVisible = false;
        UpdateUndoImport();
    }

    private void UpdateUndoImport()
    {
        var canUndo = _vault is { CanUndoImport: true };
        UndoImportButton.IsVisible = canUndo;
        UndoImportButton.Content = canUndo ? _vault!.UndoImportText : "撤销导入";
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
        var showBind = !sync.IsBound || sync.RequiresRebind;
        SyncBoundPanel.IsVisible = sync.IsBound && !sync.RequiresRebind;
        SyncBindPanel.IsVisible = showBind;
        if (sync.IsBound && !showBind)
        {
            SyncAccountText.Text = sync.Email ?? "";
            SyncMetaText.Text =
                $"云端版本 {sync.VaultVersion}"
                + (string.IsNullOrEmpty(sync.BindingKeyFingerprint)
                    ? ""
                    : $" · 绑定密钥指纹 {sync.BindingKeyFingerprint}");
            SyncAdoptButton.IsVisible = sync.CanAdoptKey;
            SyncRotateButton.IsEnabled = !sync.SyncBlocked;
        }
        else if (showBind && string.IsNullOrWhiteSpace(SyncEmailBox.Text) && !string.IsNullOrWhiteSpace(sync.Email))
        {
            SyncEmailBox.Text = sync.Email;
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
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex)
        {
            ShowError(SyncFailureText(ex));
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

        var button = sender as Button;
        if (button is not null)
        {
            button.IsEnabled = false;
        }

        try
        {
            await _sync.BindAsync(
                SyncEmailBox.Text ?? "",
                SyncPasswordBox.Text ?? "",
                SyncBindingKeyBox.Text ?? "",
                SyncCodeBox.Text ?? "");
            ClearSyncSecrets();
            if (_sync.HasError)
            {
                ShowError(_sync.StatusMessage ?? SyncMessages.VaultPending);
            }
            else
            {
                ErrorBox.IsVisible = false;
            }

            VaultChanged?.Invoke(this, EventArgs.Empty);
            RefreshSyncUi();
        }
        catch (Exception ex)
        {
            ShowError(SyncFailureText(ex));
        }
        finally
        {
            if (button is not null)
            {
                button.IsEnabled = true;
            }
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
        catch (Exception ex)
        {
            ShowError(SyncFailureText(ex));
        }
    }

    private static string SyncFailureText(Exception ex) => ex switch
    {
        SyncApiException or ArgumentException or InvalidDataException or InvalidOperationException
            or CredentialRejectedException => ex.Message,
        _ => "云同步失败，请稍后重试。本地数据仍可使用。"
    };

    private async void OnSyncRotateKey(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        var password = SyncRotatePasswordBox.Text ?? "";
        if (password.Length == 0)
        {
            ShowError("换钥需要网站密码，只用来在本机包裹新的数据密钥，不会保存。");
            return;
        }

        var path = await SafeStoragePickers.PickOpenFileAsync(Host, "选择新的加密密钥文件", [EncryptionKeyDialog.KeyFileType], _pauseWatcher);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var preview = await _sync.RotateKeyAsync(text, password, confirmConflicts: false);
            if (preview.NeedsConfirmation)
            {
                var agreed = await Dialogs.ConfirmAsync(Host, "确认合并后换钥",
                    $"有 {preview.ConflictCount} 条本地和云端内容不同。确认后才合并并提交新密钥。取消则云端和本机都保持原样。",
                    "确认合并并换钥");
                if (!agreed)
                {
                    return;
                }

                await _sync.RotateKeyAsync(text, password, confirmConflicts: true);
            }

            SyncRotatePasswordBox.Text = "";
            ErrorBox.IsVisible = false;
            VaultChanged?.Invoke(this, EventArgs.Empty);
            RefreshSyncUi();
        }
        catch (Exception ex)
        {
            ShowError(SyncFailureText(ex));
        }
    }

    private async void OnSyncAdoptKey(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        var path = await SafeStoragePickers.PickOpenFileAsync(Host, "选择新的加密密钥文件", [EncryptionKeyDialog.KeyFileType], _pauseWatcher);
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var preview = await _sync.AdoptKeyFileAsync(text, confirmConflicts: false);
            if (preview.NeedsConfirmation)
            {
                var agreed = await Dialogs.ConfirmAsync(Host, "确认合并后恢复同步",
                    $"有 {preview.ConflictCount} 条尚未同步的本地内容和云端不同。取消则继续暂停同步，本地库保持原样。",
                    "确认合并并恢复");
                if (!agreed)
                {
                    return;
                }

                await _sync.AdoptKeyFileAsync(text, confirmConflicts: true);
            }

            ErrorBox.IsVisible = false;
            VaultChanged?.Invoke(this, EventArgs.Empty);
            RefreshSyncUi();
        }
        catch (Exception ex)
        {
            ShowError(SyncFailureText(ex));
        }
    }

    private async void OnSyncDisconnect(object? sender, RoutedEventArgs e)
    {
        if (_sync is null)
        {
            return;
        }

        if (!await Dialogs.ConfirmDangerAsync(Host, "断开云同步", "断开后本机不再上传/下载。本地保险库保留，可随时重新绑定。", "断开"))
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
