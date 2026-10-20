using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using KeepPassword.App.Services;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

internal static class EncryptionKeyDialog
{
    internal static readonly FilePickerFileType KeyFileType = new("Keep Password 加密密钥") { Patterns = ["*.kpkey"] };

    public static async Task<bool> SaveNewAsync(Window owner, VaultSession session, Action<bool>? pauseWatcher = null,
        Func<Task<string?>>? choosePath = null)
    {
        if (!session.IsUnlocked || !session.NeedsKeyFileSave) return session.IsUnlocked;
        var window = CreateWindow("永久保存加密密钥");
        var key = new TextBox { Text = session.PendingEncryptionKey, IsReadOnly = true, TextWrapping = TextWrapping.Wrap };
        key.Classes.Add("mono");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "选择文件夹并保存密钥文件", HorizontalAlignment = HorizontalAlignment.Stretch };
        save.Classes.Add("accent");
        var done = new Button { Content = "已保存，继续", IsEnabled = false };
        var cancel = new Button { Content = "退出并锁定" };
        var saved = false;
        var closed = false;
        window.Closed += (_, _) => { closed = true; key.Text = ""; };
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false;
            try
            {
                var path = choosePath is not null ? await choosePath()
                    : await SafeStoragePickers.PickSaveFileAsync(window, "保存加密密钥文件",
                        $"KeepPassword-密钥-{session.EncryptionKeyId}.kpkey", [KeyFileType], pauseWatcher);
                if (closed || string.IsNullOrWhiteSpace(path) || !session.IsUnlocked) return;
                if (!path.EndsWith(".kpkey", StringComparison.OrdinalIgnoreCase)) path += ".kpkey";
                session.SaveEncryptionKeyFile(path, key.Text ?? "");
                saved = true;
                status.Text = "密钥文件已保存。请永久保管，并与保险库备份分开存放。";
                done.IsEnabled = true;
                cancel.IsVisible = false;
                key.Text = "";
                save.IsVisible = false;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or CredentialRejectedException)
            {
                status.Text = ex.Message;
            }
            finally { if (!closed) save.IsEnabled = true; }
        };
        done.Click += (_, _) => window.Close();
        cancel.Click += (_, _) => window.Close();
        var body = session.HasMasterPassword
            ? "还原备份、导出明文和重置忘记的主密码时都需要它。更换主密码不会改变这把密钥。请先将密钥文件保存到你选择的文件夹。"
            : "换电脑、忘记短密钥、还原备份和导出明文时都需要它。请先将密钥文件保存到你选择的文件夹，并与保险库备份分开保管。";
        window.Content = Content("这是你的长期加密密钥", body, key, save, status, Buttons(cancel, done));
        await ShowAsync(window, owner, session);
        key.Text = "";
        return saved && session.IsUnlocked;
    }

    public static async Task<string?> AskAsync(Window owner, string title, VaultSession? session = null,
        Action<bool>? pauseWatcher = null, bool verifyCurrent = true)
    {
        var window = CreateWindow(title);
        var box = new TextBox { PasswordChar = '•', Watermark = "粘贴完整的 KP2- 加密密钥" };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var choose = new Button { Content = "选择密钥文件…" };
        var ok = new Button { Content = "验证并继续" };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = "取消" };
        string? result = null;
        var closed = false;
        window.Closed += (_, _) => { closed = true; box.Text = ""; };
        choose.Click += async (_, _) =>
        {
            try
            {
                var path = await SafeStoragePickers.PickOpenFileAsync(window, "选择加密密钥文件", [KeyFileType], pauseWatcher);
                if (!closed && !string.IsNullOrWhiteSpace(path))
                {
                    box.Text = VaultKeyFile.Read(path);
                    status.Text = "密钥文件已读取。";
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or CredentialRejectedException)
            {
                status.Text = ex.Message;
            }
        };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(box.Text)) { status.Text = "请选择密钥文件或输入加密密钥。"; return; }
            try
            {
                // Sensitive operations on the current session validate here and again in the core.
                if (verifyCurrent) session?.VerifyEncryptionKey(box.Text);
                result = box.Text;
                window.Close();
            }
            catch (Exception ex) when (ex is CredentialRejectedException or InvalidOperationException)
            {
                status.Text = ex.Message;
            }
        };
        cancel.Click += (_, _) => window.Close();
        window.Content = Content(title, "选择你永久保存的加密密钥文件，或粘贴文件中的完整密钥。", choose, box, status, Buttons(cancel, ok));
        await ShowAsync(window, owner, session);
        box.Text = "";
        return session is null || session.IsUnlocked ? result : null;
    }

    private static Window CreateWindow(string title) => new()
    {
        Title = title, Width = 540, SizeToContent = SizeToContent.Height, CanResize = false,
        ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner
    };

    private static StackPanel Content(string title, string description, params Control[] controls)
    {
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        foreach (var control in controls) panel.Children.Add(control);
        return panel;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 10 };
        foreach (var button in buttons) panel.Children.Add(button);
        return panel;
    }

    private static async Task ShowAsync(Window window, Window owner, VaultSession? session)
    {
        void Close()
        {
            if (Dispatcher.UIThread.CheckAccess()) window.Close();
            else Dispatcher.UIThread.Post(window.Close);
        }
        void OnLocked(object? sender, EventArgs args) => Close();
        void OnOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
        {
            if (args.Property == Visual.IsVisibleProperty && !owner.IsVisible) Close();
        }
        if (session is not null) session.Locked += OnLocked;
        owner.PropertyChanged += OnOwnerChanged;
        Motion.AttachWindow(window);
        try
        {
            if (session is { IsUnlocked: false } || !owner.IsVisible) return;
            await window.ShowDialog(owner);
        }
        finally
        {
            if (session is not null) session.Locked -= OnLocked;
            owner.PropertyChanged -= OnOwnerChanged;
        }
    }
}
