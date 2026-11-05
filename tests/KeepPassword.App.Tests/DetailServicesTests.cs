using System.Data.Common;
using Avalonia.Headless.XUnit;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Views.Details;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Security;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public sealed class DetailServicesTests
{
    [Fact]
    public async Task SensitiveCopy_ReturnsBeforeExpiryAndClearsOnlyTheMatchingValue()
    {
        var delay = new ControlledDelay();
        var service = NewClipboardService(delay);
        var clipboard = new MemoryClipboard();

        var copying = service.CopyAsync(clipboard, "secret", sensitive: true);

        Assert.True(copying.IsCompletedSuccessfully);
        Assert.True(await copying);
        Assert.Equal("secret", clipboard.Text);
        Assert.False(service.ExpirationTask.IsCompleted);
        Assert.Equal(ExpiringClipboard.DefaultLifetime, Assert.Single(delay.Lifetimes));
        delay.Complete(0);
        await service.ExpirationTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("", clipboard.Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterCopy_InvalidatesTheOldExpiryEvenWhenTheTextIsIdentical(bool sensitive)
    {
        var delay = new ControlledDelay();
        var service = NewClipboardService(delay);
        var clipboard = new MemoryClipboard();
        Assert.True(await service.CopyAsync(clipboard, "same", sensitive: true));
        var oldExpiry = service.ExpirationTask;

        Assert.True(await service.CopyAsync(clipboard, "same", sensitive));
        Assert.True(delay.Tokens[0].IsCancellationRequested);
        delay.Complete(0); // 故意忽略取消信号，验证任务身份检查仍能保护新值。
        await oldExpiry.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("same", clipboard.Text);

        if (sensitive)
        {
            delay.Complete(1);
            await service.ExpirationTask.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal("", clipboard.Text);
        }
    }

    [Fact]
    public async Task ExternalClipboardChanges_ArePreservedAtExpiry()
    {
        var delay = new ControlledDelay();
        var service = NewClipboardService(delay);
        var clipboard = new MemoryClipboard();
        await service.CopyAsync(clipboard, "secret", sensitive: true);
        clipboard.Text = "copied by another application";

        delay.Complete(0);
        await service.ExpirationTask.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("copied by another application", clipboard.Text);
        Assert.Equal(["secret"], clipboard.Writes);
    }

    [Fact]
    public async Task ExpiryReadAndLaterWrite_AreSerializedSoTheNewCopySurvives()
    {
        var delay = new ControlledDelay();
        var service = NewClipboardService(delay);
        var clipboard = new MemoryClipboard { BlockRead = true };
        await service.CopyAsync(clipboard, "old secret", sensitive: true);
        var expiry = service.ExpirationTask;
        delay.Complete(0);
        await clipboard.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var laterCopy = service.CopyAsync(clipboard, "new content", sensitive: false);
        Assert.False(laterCopy.IsCompleted);
        clipboard.ReadResult.TrySetResult("old secret");
        Assert.True(await laterCopy.WaitAsync(TimeSpan.FromSeconds(2)));
        await expiry.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal("new content", clipboard.Text);
    }

    [Fact]
    public async Task ClipboardFailures_ReportFailureAndLeaveTheServiceReusable()
    {
        var delay = new ControlledDelay();
        var service = NewClipboardService(delay);
        var clipboard = new MemoryClipboard { FailWrite = true };
        Assert.False(await service.CopyAsync(clipboard, "secret", sensitive: true));
        Assert.Empty(delay.Lifetimes);

        clipboard.FailWrite = false;
        clipboard.FailRead = true;
        Assert.True(await service.CopyAsync(clipboard, "secret", sensitive: true));
        delay.Complete(0);
        await service.ExpirationTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(await service.CopyAsync(clipboard, "later copy", sensitive: false));
        Assert.Equal("later copy", clipboard.Text);
    }

    [AvaloniaFact]
    public void TotpTicker_UsesOneClockAndStopsAfterItsLastSubscriber()
    {
        Assert.Equal(0, TotpTicker.SubscriberCount);
        var firstTicks = new List<DateTimeOffset>();
        var secondTicks = new List<DateTimeOffset>();
        using var first = TotpTicker.Subscribe(firstTicks.Add);
        using var second = TotpTicker.Subscribe(secondTicks.Add);
        Assert.Equal(2, TotpTicker.SubscriberCount);
        Assert.True(TotpTicker.IsRunning);
        Assert.Single(firstTicks);
        Assert.Single(secondTicks);
        var now = DateTimeOffset.FromUnixTimeSeconds(123456);
        TotpTicker.Publish(now);
        Assert.Equal(now, firstTicks[^1]);
        Assert.Equal(now, secondTicks[^1]);

        first.Dispose();
        first.Dispose();
        Assert.Equal(1, TotpTicker.SubscriberCount);
        Assert.True(TotpTicker.IsRunning);
        TotpTicker.Publish(now.AddSeconds(1));
        Assert.Equal(2, firstTicks.Count);
        Assert.Equal(3, secondTicks.Count);
        second.Dispose();
        Assert.Equal(0, TotpTicker.SubscriberCount);
        Assert.False(TotpTicker.IsRunning);
    }

    [Theory]
    [InlineData("mysql")]
    [InlineData("postgresql")]
    [InlineData("mongodb")]
    public void DatabaseUris_EncodeCredentialsAndDatabaseAndBracketIpv6(string driver)
    {
        var ordinary = DetailQuickActionFormatter.DatabaseConnection(driver, "2001:db8::10", "3306", "team/a b", "u@s:e r", "p@ss:/#?", false);
        var sensitive = DetailQuickActionFormatter.DatabaseConnection(driver, "[2001:db8::10]", "3306", "team/a b", "u@s:e r", "p@ss:/#?", true);

        Assert.Equal($"{driver}://u%40s%3Ae%20r@[2001:db8::10]:3306/team%2Fa%20b", ordinary);
        Assert.Equal($"{driver}://u%40s%3Ae%20r:p%40ss%3A%2F%23%3F@[2001:db8::10]:3306/team%2Fa%20b", sensitive);
    }

    [Fact]
    public void NativeDatabaseConnectionStrings_RoundTripSeparatorsAndQuotedValues()
    {
        const string database = "my;\"database\"";
        const string password = "pa;\"ss= word";
        var ordinaryText = DetailQuickActionFormatter.DatabaseConnection("sqlserver", "::1", "1433", database, "user;name", password, false);
        var sensitiveText = DetailQuickActionFormatter.DatabaseConnection("sqlserver", "::1", "1433", database, "user;name", password, true);
        var ordinary = new DbConnectionStringBuilder { ConnectionString = ordinaryText! };
        var sensitive = new DbConnectionStringBuilder { ConnectionString = sensitiveText! };

        Assert.Equal("tcp:[::1],1433", ordinary["Server"]);
        Assert.Equal(database, ordinary["Database"]);
        Assert.Equal("user;name", ordinary["User ID"]);
        Assert.False(ordinary.ContainsKey("Password"));
        Assert.Equal(password, sensitive["Password"]);

        const string path = "C:\\data\\my; \"vault\".db";
        var sqlite = new DbConnectionStringBuilder
        {
            ConnectionString = DetailQuickActionFormatter.DatabaseConnection("sqlite", "", "", path, "", password, false)!
        };
        Assert.Equal(path, sqlite["Data Source"]);
        Assert.False(sqlite.ContainsKey("Password"));
    }

    [Theory]
    [InlineData("oss", "host", "", "bucket")]
    [InlineData("elasticsearch", "host", "9200", "")]
    [InlineData("unknown", "host", "", "")]
    [InlineData("mysql", "", "3306", "db")]
    [InlineData("mysql", "host/path", "3306", "db")]
    [InlineData("mysql", "host", "3306;bad", "db")]
    [InlineData("mysql", "host", "65536", "db")]
    [InlineData("redis", "host", "6379", "not a database number")]
    [InlineData("sqlite", "", "", "")]
    public void UnsupportedOrIncompleteConnections_AreHidden(string driver, string host, string port, string database) =>
        Assert.Null(DetailQuickActionFormatter.DatabaseConnection(driver, host, port, database, "user", "secret", true));

    [Fact]
    public void Redis_AllowsPasswordOnlyCredentialsAndAnEmptyPort()
    {
        Assert.Equal("redis://:p%40ss@localhost/0", DetailQuickActionFormatter.DatabaseConnection("redis", "localhost", "", "0", "", "p@ss", true));
        Assert.Equal("redis://localhost/0", DetailQuickActionFormatter.DatabaseConnection("redis", "localhost", "", "0", "", "p@ss", false));
    }

    [Fact]
    public void ServerCommands_UseCorrectPortOptionsAndEscapeTheCurrentPlatformShell()
    {
        Assert.Equal("ssh -p 2222 -- 'alice@[2001:db8::1]'", DetailQuickActionFormatter.ServerCommand("ssh", "2001:db8::1", "2222", "alice"));
        Assert.Equal("sftp -P 22 -- 'alice@host'", DetailQuickActionFormatter.ServerCommand("sftp", "host", "22", "alice"));
        var escapedName = OperatingSystem.IsWindows() ? "a''b" : "a'\"'\"'b";
        Assert.Equal($"ssh -- '{escapedName}@host'", DetailQuickActionFormatter.ServerCommand("ssh", "host", "", "a'b"));
        Assert.Null(DetailQuickActionFormatter.ServerCommand("rdp", "host", "3389", "alice"));
        Assert.Null(DetailQuickActionFormatter.ServerCommand("ssh", "host; echo injected", "22", "alice"));
    }

    [Fact]
    public void Presentation_RestoresTheSelectedAccountByIdAfterSaveAndResetsForAnotherEntry()
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        using var presentation = new DetailPresentationViewModel(source);
        var originalAccount = source.Accounts[1];
        presentation.SelectAccount(originalAccount);
        originalAccount.Note = "work";
        Assert.True(presentation.IsDirty);
        Assert.Null(source.Save());

        Assert.Equal(originalAccount.Id, presentation.SelectedAccountId);
        Assert.Same(source.Accounts[1], presentation.CurrentAccount);
        Assert.NotSame(originalAccount, presentation.CurrentAccount);
        Assert.False(presentation.IsDirty);
        source.NewItem(VaultItemKind.Login);
        Assert.Same(source.Accounts[0], presentation.CurrentAccount);
        Assert.NotEqual(originalAccount.Id, presentation.SelectedAccountId);
    }

    [Fact]
    public void Presentation_UpdatesTabLabelsAndValidationWithoutRebuildingEditors()
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        source.NewItem(VaultItemKind.Login);
        using var presentation = new DetailPresentationViewModel(source);
        var tab = Assert.Single(presentation.AccountTabs);
        var displayChanges = 0;
        var labelChanges = 0;
        presentation.DisplayChanged += (_, _) => displayChanges++;
        tab.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DetailAccountTab.Label)) labelChanges++; };
        Assert.Equal("账号 1", tab.Label);
        Assert.False(presentation.CanSave);

        source.Name = "New login";
        source.Accounts[0].UsernameField.Value = "alice";
        source.Accounts[0].PasswordField.Value = "secret";
        Assert.True(presentation.CanSave);
        Assert.Equal("", presentation.ValidationMessage);
        Assert.Equal("alice", tab.Label);
        source.Accounts[0].Note = "Production";
        Assert.Equal("Production", tab.Label);
        Assert.True(labelChanges >= 2);
        Assert.Same(tab, Assert.Single(presentation.AccountTabs));
        Assert.Equal(0, displayChanges);
    }

    [Fact]
    public void Presentation_SelectionVisibilityAndTotpRefreshDoNotDirtyTheEntry()
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        using var presentation = new DetailPresentationViewModel(source);
        var dirtyChanges = 0;
        source.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsDirty)) dirtyChanges++; };

        presentation.SelectAccount(source.Accounts[1]);
        source.Accounts[1].PasswordField.Revealed = true;
        source.RefreshTotp();

        Assert.False(source.IsDirty);
        Assert.False(presentation.IsDirty);
        Assert.Equal(0, dirtyChanges);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("note")]
    [InlineData("url")]
    [InlineData("username")]
    [InlineData("password")]
    [InlineData("accountNote")]
    [InlineData("totp")]
    [InlineData("custom")]
    [InlineData("space")]
    [InlineData("tag")]
    [InlineData("addAccount")]
    [InlineData("removeAccount")]
    public void MainViewModel_NotifiesDirtyForPersistedEdits(string field)
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        var dirtyChanges = 0;
        source.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsDirty)) dirtyChanges++; };

        switch (field)
        {
            case "name": source.Name = "renamed"; break;
            case "note": source.Note = "changed"; break;
            case "url": source.PresetFields.Single(item => item.Key == FieldKeys.Url).Value = "https://changed.example"; break;
            case "username": source.Accounts[0].UsernameField.Value = "other user"; break;
            case "password": source.Accounts[0].PasswordField.Value = "other password"; break;
            case "accountNote": source.Accounts[0].Note = "other note"; break;
            case "totp": source.Accounts[0].TotpField.Value = "JBSWY3DPEHPK3PXP"; break;
            case "custom": source.CustomFields[0].Value = "changed"; break;
            case "space": source.ChooseSpace("Work"); break;
            case "tag": source.ToggleTag("Personal"); break;
            case "addAccount": source.AddAccount(); break;
            case "removeAccount": source.RemoveAccount(source.Accounts[1]); break;
        }

        Assert.True(source.IsDirty);
        Assert.True(dirtyChanges > 0);
    }

    [Fact]
    public void NodeEditsAndCollectionChanges_NotifyDirty()
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        source.NewItem(VaultItemKind.Cluster);
        var dirtyChanges = 0;
        source.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainViewModel.IsDirty)) dirtyChanges++; };
        source.Nodes[0].Host = "node.example";
        Assert.True(source.IsDirty);
        Assert.True(dirtyChanges > 0);
        dirtyChanges = 0;
        source.AddNode();
        Assert.True(dirtyChanges > 0);
        dirtyChanges = 0;
        source.RemoveNode(source.Nodes[1]);
        Assert.True(dirtyChanges > 0);
    }

    [Fact]
    public void DraftValidation_UsesCurrentFieldsWithoutSavingOrChangingTheSession()
    {
        using var fixture = new VaultFixture();
        var source = fixture.View;
        var originalFile = File.ReadAllBytes(fixture.Path);
        var storedCount = fixture.Session.Entries.Count;
        source.NewItem(VaultItemKind.Server);
        var draftId = source.DetailId;
        Assert.Equal("请填写名称。", source.GetDetailValidationMessage());
        source.Name = "server";
        source.Accounts[0].UsernameField.Value = "alice";
        source.Accounts[0].PasswordField.Value = "secret";
        Assert.Equal("请填写服务器主机或 IP。", source.GetDetailValidationMessage());
        source.PresetFields.Single(item => item.Key == FieldKeys.Host).Value = "server.example";
        source.PresetFields.Single(item => item.Key == FieldKeys.Port).Value = "70000";
        Assert.Equal("端口必须是 1-65535 之间的数字。", source.GetDetailValidationMessage());

        Assert.Equal(draftId, source.DetailId);
        Assert.Equal(storedCount, fixture.Session.Entries.Count);
        Assert.Equal(originalFile, File.ReadAllBytes(fixture.Path));
    }

    [Fact]
    public void Presentation_DisposeDetachesAndReleasesAccountAndSourceReferences()
    {
        using var fixture = new VaultFixture();
        var presentation = new DetailPresentationViewModel(fixture.View);
        var oldTab = presentation.AccountTabs[0];
        presentation.Dispose();
        fixture.View.Accounts[0].Note = "changed after disposal";

        Assert.Null(presentation.CurrentAccount);
        Assert.Empty(presentation.AccountTabs);
        Assert.False(presentation.IsDirty);
        Assert.False(presentation.CanSave);
        Assert.Throws<ObjectDisposedException>(() => presentation.Source);
        Assert.Throws<ObjectDisposedException>(() => oldTab.Account);
    }

    private static ClipboardCopyCoordinator NewClipboardService(ControlledDelay delay) => new(delay.WaitAsync, ExpiringClipboard.DefaultLifetime);

    private sealed class ControlledDelay
    {
        private readonly List<TaskCompletionSource> _pending = [];
        public List<TimeSpan> Lifetimes { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];

        public Task WaitAsync(TimeSpan lifetime, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending.Add(completion);
            Lifetimes.Add(lifetime);
            Tokens.Add(cancellationToken);
            return completion.Task;
        }

        public void Complete(int index) => _pending[index].TrySetResult();
    }

    private sealed class MemoryClipboard : ITextClipboard
    {
        public string? Text { get; set; }
        public List<string?> Writes { get; } = [];
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public bool BlockRead { get; init; }
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> ReadResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> ReadAsync()
        {
            if (FailRead) throw new InvalidOperationException("Clipboard is busy.");
            ReadStarted.TrySetResult();
            return BlockRead ? ReadResult.Task : Task.FromResult(Text);
        }

        public Task WriteAsync(string? value)
        {
            if (FailWrite) throw new InvalidOperationException("Clipboard is busy.");
            Text = value;
            Writes.Add(value);
            return Task.CompletedTask;
        }
    }

    private sealed class VaultFixture : IDisposable
    {
        private string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "keep-password-detail-services", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "test.kpvault");
        public VaultSession Session { get; }
        public MainViewModel View { get; }

        public VaultFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Session = new VaultStore(KdfProfile.Fast).CreateSession(Path, "test account", "test master", "test short");
            var entry = new VaultEntry
            {
                Name = "Test login", Url = "https://example.com", Space = "Work", Tags = ["Personal"],
                Accounts = [new VaultAccount { Username = "alice", Password = "password one" }, new VaultAccount { Username = "bob", Password = "password two" }],
                CustomFields = [new VaultCustomField { Name = "custom", Value = "original" }]
            };
            Session.Upsert(entry);
            Session.Save();
            View = new MainViewModel(Session, platformAutofillSupported: false);
            View.Load(entry.Id);
        }

        public void Dispose()
        {
            Session.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
