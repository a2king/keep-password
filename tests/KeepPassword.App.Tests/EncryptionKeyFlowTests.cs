using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Views;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class EncryptionKeyFlowTests
{
    [AvaloniaFact]
    public async Task KeySetup_CancelAndFailedSaveDoNotFinish_ChosenFileSurvivesRestart()
    {
        using var fixture = new Fixture();
        var key = fixture.Session.PendingEncryptionKey;
        var blockedPath = Path.Combine(fixture.Directory, "blocked.kpkey");
        System.IO.Directory.CreateDirectory(blockedPath);
        var selectedPath = Path.Combine(fixture.Directory, "my-folder", "permanent.kpkey");
        var paths = new Queue<string?>([null, blockedPath, selectedPath]);
        var completion = EncryptionKeyDialog.SaveNewAsync(fixture.Owner, fixture.Session,
            choosePath: () => Task.FromResult(paths.Dequeue()));
        var dialog = Assert.Single(fixture.Owner.OwnedWindows);
        var box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        Assert.Equal(key, box.Text);
        var save = FindButton(dialog, "选择文件夹并保存密钥文件");
        var done = FindButton(dialog, "已保存，继续");
        Click(save); // Cancel the location picker.
        Assert.False(done.IsEnabled);
        Assert.True(fixture.Session.NeedsKeyFileSave);
        Assert.False(completion.IsCompleted);
        Click(save); // The chosen location cannot be written.
        Assert.False(done.IsEnabled);
        Assert.True(fixture.Session.NeedsKeyFileSave);
        Click(save); // Save to a user-selected folder.
        Assert.True(done.IsEnabled);
        Assert.Equal("", box.Text);
        Assert.Equal(key, VaultKeyFile.Read(selectedPath));
        Click(done);
        Assert.True(await completion);
        using var reopened = fixture.Store.Unlock(fixture.Path, "test-master");
        Assert.False(reopened.NeedsKeyFileSave);
        reopened.VerifyEncryptionKey(VaultKeyFile.Read(selectedPath));
    }

    [AvaloniaFact]
    public async Task ClosingKeySetup_ClearsDisplayedKeyAndRemainsPending()
    {
        using var fixture = new Fixture();
        var key = fixture.Session.PendingEncryptionKey;
        var completion = EncryptionKeyDialog.SaveNewAsync(fixture.Owner, fixture.Session);
        var dialog = Assert.Single(fixture.Owner.OwnedWindows);
        var box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        dialog.Close();
        Assert.False(await completion);
        Assert.Equal("", box.Text);
        using var reopened = fixture.Store.Unlock(fixture.Path, "test-master");
        Assert.True(reopened.NeedsKeyFileSave);
        Assert.Equal(key, reopened.PendingEncryptionKey);
    }

    [AvaloniaFact]
    public async Task LockDuringKeySetup_ImmediatelyClosesAndClearsTheKey()
    {
        using var fixture = new Fixture();
        var completion = EncryptionKeyDialog.SaveNewAsync(fixture.Owner, fixture.Session);
        var dialog = Assert.Single(fixture.Owner.OwnedWindows);
        var box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        Assert.False(string.IsNullOrEmpty(box.Text));
        fixture.Session.Lock();
        Assert.False(dialog.IsVisible);
        Assert.Equal("", box.Text);
        Assert.False(await completion);
    }

    [AvaloniaFact]
    public async Task SensitiveOperation_RejectsPasswordAndWrongKey_ThenAcceptsMatchingKey()
    {
        using var fixture = new Fixture();
        var key = fixture.Session.PendingEncryptionKey;
        var completion = EncryptionKeyDialog.AskAsync(fixture.Owner, "导出明文", fixture.Session);
        var dialog = Assert.Single(fixture.Owner.OwnedWindows);
        var box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        var verify = FindButton(dialog, "验证并继续");
        box.Text = "test-master";
        Click(verify);
        Assert.False(completion.IsCompleted);
        using var other = fixture.Store.CreateSession(Path.Combine(fixture.Directory, "other.kpvault"), "other", "master", "short");
        box.Text = other.PendingEncryptionKey;
        Click(verify);
        Assert.False(completion.IsCompleted);
        box.Text = key;
        Click(verify);
        Assert.Equal(key, await completion);
        Assert.Equal("", box.Text);
    }

    [AvaloniaFact]
    public async Task CreationCancellationAndKeyRecovery_KeepDailyShortUnlock()
    {
        using var fixture = new Fixture();
        var newPath = Path.Combine(fixture.Directory, "created.kpvault");
        var view = new UnlockViewModel(fixture.Store, newPath)
        {
            ShortKey = "short", ConfirmShort = "short"
        };
        Assert.True(await view.SubmitAsync());
        var key = view.Session!.PendingEncryptionKey;
        view.CancelKeySetup();
        Assert.Null(view.Session);
        Assert.False(view.IsCreate);
        Assert.True(view.ShowShortUnlock);
        view.ShortKey = "short";
        Assert.True(await view.SubmitAsync());
        Assert.Equal(key, view.Session!.PendingEncryptionKey);
        view.Session.Dispose();

        var recovery = new UnlockViewModel(fixture.Store, newPath);
        recovery.BeginRecovery();
        Assert.True(recovery.ShowKeyRecovery);
        Assert.False(recovery.ShowLegacyRecovery);
        recovery.EncryptionKey = key;
        recovery.MasterPassword = recovery.ConfirmMaster = "reset-short";
        Assert.True(await recovery.SubmitAsync());
        Assert.Equal("", recovery.EncryptionKey);
        recovery.Session!.Dispose();
        using var reopened = fixture.Store.UnlockWithShortKey(newPath, "reset-short");
        reopened.VerifyEncryptionKey(key);
    }

    [AvaloniaFact]
    public async Task NewComputer_CreateWithExistingKey_ReusesKeyAndSkipsSavePrompt()
    {
        using var fixture = new Fixture();
        var key = fixture.Session.PendingEncryptionKey;
        var keyId = fixture.Session.EncryptionKeyId;
        var newPath = Path.Combine(fixture.Directory, "laptop.kpvault");
        var view = new UnlockViewModel(fixture.Store, newPath);
        Assert.True(view.ShowUseExistingKey);
        Assert.False(view.ShowKeyRecovery);

        view.UseExistingKeyForCreate();
        Assert.True(view.CreateWithExistingKey);
        Assert.True(view.ShowKeyRecovery);
        Assert.True(view.ShowCreateShort);
        Assert.Equal("导入密钥并创建", view.SubmitText);

        view.ShortKey = view.ConfirmShort = "laptop";
        Assert.False(await view.SubmitAsync());
        Assert.Contains("加密密钥", view.Error);
        Assert.False(File.Exists(newPath));

        view.ShortKey = view.ConfirmShort = "laptop";
        view.EncryptionKey = "KPB-this-is-the-old-binding-file";
        Assert.False(await view.SubmitAsync());
        Assert.False(File.Exists(newPath));

        view.ShortKey = view.ConfirmShort = "laptop";
        view.EncryptionKey = key;
        Assert.True(await view.SubmitAsync());
        Assert.Equal(keyId, view.Session!.EncryptionKeyId);
        Assert.False(view.Session.NeedsKeyFileSave);
        Assert.Equal("", view.EncryptionKey);
        view.Session.Dispose();
        using var reopened = fixture.Store.UnlockWithShortKey(newPath, "laptop");
        Assert.Equal(keyId, reopened.EncryptionKeyId);
    }

    [AvaloniaFact]
    public async Task CreateWithExistingKey_CanSwitchBackToANewKey()
    {
        using var fixture = new Fixture();
        var newPath = Path.Combine(fixture.Directory, "fresh.kpvault");
        var view = new UnlockViewModel(fixture.Store, newPath);
        view.UseExistingKeyForCreate();
        view.EncryptionKey = fixture.Session.PendingEncryptionKey;
        view.UseNewKeyForCreate();
        Assert.False(view.ShowKeyRecovery);
        Assert.Equal("", view.EncryptionKey);
        view.ShortKey = view.ConfirmShort = "fresh";
        Assert.True(await view.SubmitAsync());
        Assert.True(view.Session!.NeedsKeyFileSave);
        Assert.NotEqual(fixture.Session.EncryptionKeyId, view.Session.EncryptionKeyId);
        view.Session.Dispose();
    }

    [AvaloniaFact]
    public async Task ReExportKey_RejectsWrongShortKey_ThenSavesTheSameKey()
    {
        using var fixture = new Fixture();
        var localPath = Path.Combine(fixture.Directory, "local.kpvault");
        using var session = fixture.Store.CreateLocalSession(localPath, "local-short");
        var key = session.PendingEncryptionKey;
        session.SaveEncryptionKeyFile(Path.Combine(fixture.Directory, "original.kpkey"), key);
        var target = Path.Combine(fixture.Directory, "again", "copy");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var completion = EncryptionKeyDialog.ExportAsync(fixture.Owner, session, choosePath: () => Task.FromResult<string?>(target));
        var dialog = Assert.Single(fixture.Owner.OwnedWindows);
        var box = Assert.Single(dialog.GetVisualDescendants().OfType<TextBox>());
        Assert.DoesNotContain(dialog.GetVisualDescendants().OfType<TextBlock>(), block => block.Text?.Contains("KP2-") == true);
        var ok = FindButton(dialog, "验证并选择保存位置…");

        box.Text = "wrong-short";
        Click(ok);
        await WaitUntil(() => dialog.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == "短密钥不正确。"));
        Assert.False(completion.IsCompleted);
        Assert.False(File.Exists(target + ".kpkey"));

        box.Text = "local-short";
        Click(ok);
        Assert.True(await completion.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(key, VaultKeyFile.Read(target + ".kpkey"));
        Assert.Equal("", box.Text);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task InvalidVaultFormat_ShowsAnErrorWithoutOpeningASession()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, "{\"format\":\"keep-password-vault\",\"version\":99}");
        var view = new UnlockViewModel(fixture.Store, fixture.Path) { MasterPassword = "test-master" };
        Assert.False(await view.SubmitAsync());
        Assert.True(view.HasError);
        Assert.Null(view.Session);
    }

    private static Button FindButton(Window window, string content) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Content is string text && text == content);

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keep-password-key-ui", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "vault.kpvault");
        public VaultStore Store { get; } = new(KdfProfile.Fast);
        public VaultSession Session { get; }
        public Window Owner { get; } = new() { Width = 800, Height = 600 };

        public Fixture()
        {
            Session = Store.CreateSession(Path, "test-account", "test-master", "test-short");
            Owner.Show();
        }

        public void Dispose()
        {
            Session.Dispose();
            Owner.Close();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
