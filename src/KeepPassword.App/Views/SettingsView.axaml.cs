using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using KeepPassword.App.Services;
using KeepPassword.Core.Import;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class SettingsView : UserControl
{
    private VaultSession? _session;
    private VaultLocation? _location;
    private Action<bool>? _pauseWatcher;

    public SettingsView()
    {
        InitializeComponent();
        AnimateStatus(LabelStatus);
        AnimateStatus(DirectoryStatus);
        AnimateStatus(KeyStatus);
        ThemeManager.ThemeChanged += UpdateThemeButtons;
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

    public void Show(VaultSession session, VaultLocation location, Action<bool>? pauseWatcher)
    {
        _session = session;
        _location = location;
        _pauseWatcher = pauseWatcher;
        AccountText.Text = "当前账号：" + session.Account;
        DirectoryBox.Text = location.Directory;
        LabelStatus.Text = "";
        DirectoryStatus.Text = "";
        KeyStatus.Text = "";
        ErrorBox.IsVisible = false;
        UpdateThemeButtons(ThemeManager.CurrentTheme);
        UpdateMotionButtons();
        RebuildLabels();
        UpdateEncryptionKeyStatus();
        Scroller.Offset = default;
    }

    public void Clear()
    {
        _session = null;
        _location = null;
        _pauseWatcher = null;
        ClearSecrets();
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
            await Task.Run(() => session.ChangeShortKey(master, next));
            NewKeyBox.Text = "";
            ConfirmBox.Text = "";
            KeyStatus.Text = "短密钥已更新。";
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
        MasterChangeHint.Text = session.UsesIndependentKey
            ? "更换主密码不会改变加密密钥，之前保存的密钥文件仍然有效。"
            : "此保险库仍使用旧版加密。更换一次主密码将升级为独立随机密钥，并提示保存密钥文件。";
        EncryptionKeyStatus.Text = !session.UsesIndependentKey
            ? "尚未升级。请先更换一次主密码，生成并永久保存独立加密密钥。"
            : session.NeedsKeyFileSave ? "密钥文件尚未保存，请选择文件夹完成保存。"
            : "密钥文件已保存。还原备份、明文导出和重置主密码时，请提供你永久保管的密钥。";
        SavePendingKeyButton.IsVisible = session.NeedsKeyFileSave;
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

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }
}
