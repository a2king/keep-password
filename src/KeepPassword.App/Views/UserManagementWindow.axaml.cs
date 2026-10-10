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

    public bool VaultChanged { get; private set; }

    public UserManagementWindow() => InitializeComponent();

    public UserManagementWindow(VaultSession session, VaultLocation location, Action<bool>? pauseWatcher = null)
        : this()
    {
        _session = session;
        _location = location;
        _pauseWatcher = pauseWatcher;
        AccountText.Text = "当前账号：" + session.Account;
        DirectoryBox.Text = location.Directory;
        UpdateThemeButtons(ThemeManager.CurrentTheme);
        RebuildLabels();
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
        if (!await Dialogs.ConfirmAsync(this, isSpace ? "删除空间分类" : "删除账号标签", message))
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
            VaultChanged = true;
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

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnBrowseDirectory(object? sender, RoutedEventArgs e)
    {
        try
        {
            var path = await SafeStoragePickers.PickFolderAsync(this, "选择缓存目录", _pauseWatcher);
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
            await Task.Run(() => _session.ChangeMasterPassword(current, next));
            CurrentMasterBox.Text = "";
            NewMasterBox.Text = "";
            ConfirmMasterBox.Text = "";
            DirectoryStatus.Text = "主密码已更换，保险库已用新密钥重新加密。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is CredentialRejectedException or ArgumentException or IOException)
        {
            ShowError(ex.Message);
        }
    }

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var path = await SafeStoragePickers.PickSaveFileAsync(
                this,
                "导出加密备份",
                "keep-password-backup.kpvault",
                [new FilePickerFileType("Keep Password 保险库") { Patterns = ["*.kpvault"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            EncryptedBackup.Export(_session.VaultPath, path);
            DirectoryStatus.Text = "已导出加密备份。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
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

        if (!await Dialogs.ConfirmAsync(this, "还原加密备份", "还原会替换当前保险库中的全部条目，并使用当前主密码重新加密。此操作需要备份文件自己的主密码。"))
        {
            return;
        }

        try
        {
            var path = await SafeStoragePickers.PickOpenFileAsync(
                this,
                "选择加密备份",
                [new FilePickerFileType("Keep Password 保险库") { Patterns = ["*.kpvault"] }],
                _pauseWatcher);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var password = await AskSecretAsync("请输入备份文件的主密码");
            if (password is null)
            {
                return;
            }

            var entries = await Task.Run(() => EncryptedBackup.ReadEntries(path, password));
            foreach (var existing in _session.Entries.ToList())
            {
                _session.Remove(existing.Id);
            }

            foreach (var entry in entries)
            {
                _session.Upsert(entry);
            }

            _session.Save();
            VaultChanged = true;
            DirectoryStatus.Text = $"已还原 {entries.Count} 条，并重新加密。";
            ErrorBox.IsVisible = false;
        }
        catch (Exception ex) when (ex is UnlockFailedException or IOException or InvalidDataException or ArgumentException)
        {
            ShowError(ex.Message);
        }
    }

    private async Task<string?> AskSecretAsync(string title)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var box = new TextBox { PasswordChar = '•' };
        string? value = null;
        var ok = new Button { Content = "确定", MinWidth = 72 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            value = box.Text ?? "";
            window.Close();
        };
        var cancel = new Button { Content = "取消", MinWidth = 72 };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                box,
                new StackPanel
                {
                    Orientation = Avalonia.Layout.Orientation.Horizontal,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { cancel, ok }
                }
            }
        };
        await window.ShowDialog(this);
        return value;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBox.IsVisible = true;
    }
}
