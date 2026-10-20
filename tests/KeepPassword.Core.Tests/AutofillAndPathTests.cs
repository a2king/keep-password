using KeepPassword.Core.Autofill;
using KeepPassword.Core.Messaging;
using KeepPassword.Core.Paths;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class AutofillAndPathTests
{
    [Fact]
    public void Match_UsesPageHostSubdomainAndWindowTitle()
    {
        var login = new VaultEntry
        {
            Id = Guid.NewGuid(),
            Name = "GitHub",
            Url = "https://github.com/login",
            Accounts = [new VaultAccount { Username = "ada", Password = "pw" }]
        };
        var totpOnly = new VaultEntry
        {
            Id = Guid.NewGuid(),
            Name = "令牌",
            Accounts = [new VaultAccount { TotpSecret = "GEZDGNBVGY3TQOJQ" }]
        };
        var entries = new[] { login, totpOnly };

        Assert.Equal(login.Id, Assert.Single(AutofillMatcher.Match(entries, Request("https://github.com/login"))).Id);
        Assert.Equal(login.Id, Assert.Single(AutofillMatcher.Match(entries, Request("https://www.github.com/"))).Id);
        Assert.Equal(login.Id, Assert.Single(AutofillMatcher.Match(entries, new AutofillRequest
        {
            Origin = AutofillOrigin.WindowsApplication,
            WindowTitle = "github.com - 登录"
        })).Id);
        Assert.Empty(AutofillMatcher.Match(entries, Request("https://example.com")));
    }

    [Theory]
    [InlineData(AutofillOrigin.WindowsApplication, "飞书")]
    [InlineData(AutofillOrigin.WindowsApplication, "新建连接 - DbX")]
    [InlineData(AutofillOrigin.MacOsApplication, "飞书")]
    [InlineData(AutofillOrigin.MacOsApplication, "Zoom")]
    public void Match_DesktopWithoutHostOffersPasswordAccountsAcrossSupportedKinds(AutofillOrigin origin, string windowTitle)
    {
        var entries = new[] { VaultItemKind.Login, VaultItemKind.Server, VaultItemKind.Database, VaultItemKind.Cluster }
            .Select(kind => new VaultEntry
            {
                Kind = kind,
                Name = VaultItemKinds.DisplayName(kind),
                Url = "https://unrelated.example.com",
                Accounts = [new VaultAccount { Username = "ada", Password = "pw" }]
            }).ToArray();

        var matches = AutofillMatcher.Match(entries, new AutofillRequest
        {
            Origin = origin,
            WindowTitle = windowTitle
        });

        Assert.True(AutofillMatcher.IsDesktopOrigin(origin));
        Assert.Equal(entries.Length, matches.Count);
        foreach (var entry in entries)
        {
            var match = Assert.Single(matches, candidate => candidate.Id == entry.Id);
            Assert.Equal(entry.Accounts[0].Id, match.AccountId);
            Assert.Equal(VaultItemKinds.DisplayName(entry.Kind), match.KindName);
        }
    }

    [Theory]
    [InlineData("https://accounts.example.com/login", true)]
    [InlineData("https://unrelated.example.net", false)]
    [InlineData("", false)]
    public void Match_BrowserRequiresMatchingLoginUrlEvenWhenTitleAndProcessLookRelevant(string url, bool expectedMatch)
    {
        var entries = new[] { VaultItemKind.Login, VaultItemKind.Server, VaultItemKind.Database, VaultItemKind.Cluster }
            .Select(kind => new VaultEntry
            {
                Kind = kind,
                Name = "accounts.example.com",
                Url = "https://accounts.example.com",
                Fields = new() { [FieldKeys.Host] = "accounts.example.com" },
                Accounts = [new VaultAccount { Username = "ada", Password = "pw" }]
            }).ToArray();

        var matches = AutofillMatcher.Match(entries, new AutofillRequest
        {
            Origin = AutofillOrigin.Browser,
            Url = url,
            WindowTitle = "accounts.example.com - 登录",
            TargetProcess = "DbX"
        });

        if (expectedMatch)
        {
            Assert.Equal(entries.Single(entry => entry.Kind == VaultItemKind.Login).Id, Assert.Single(matches).Id);
        }
        else
        {
            Assert.Empty(matches);
        }
    }

    [Fact]
    public void Match_DesktopExcludesKeyAuthTotpOnlyAndEmptyPasswords()
    {
        var passwordAccount = new VaultAccount { Username = "password-user", Password = "pw" };
        var keyAccount = new VaultAccount
        {
            Username = "key-user", Password = "stale-password", Auth = FieldKeys.AuthSshKey, SshKeyId = Guid.NewGuid()
        };
        var entries = new[]
        {
            new VaultEntry { Kind = VaultItemKind.Server, Accounts = [passwordAccount, keyAccount] },
            new VaultEntry { Kind = VaultItemKind.Cluster, Accounts = [keyAccount.Clone()] },
            new VaultEntry { Accounts = [new VaultAccount { TotpSecret = "GEZDGNBVGY3TQOJQ" }] },
            new VaultEntry { Accounts = [new VaultAccount { Username = "no-password" }] },
            new VaultEntry { Kind = VaultItemKind.SshKey, Accounts = [passwordAccount.Clone()] },
            new VaultEntry { Kind = VaultItemKind.ApiCredential, Accounts = [passwordAccount.Clone()] }
        };

        var match = Assert.Single(AutofillMatcher.Match(entries, new AutofillRequest
        {
            Origin = AutofillOrigin.WindowsApplication,
            WindowTitle = "新建连接"
        }));

        Assert.Equal(entries[0].Id, match.Id);
        Assert.Equal(passwordAccount.Id, match.AccountId);
    }

    [Fact]
    public async Task Coordinator_RequiresShortKeyBeforeFill()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var entry = new VaultEntry
        {
            Name = "GitHub",
            Url = "https://github.com",
            Accounts = [new VaultAccount { Username = "ada", Password = "s3cret" }]
        };
        session.Upsert(entry);
        var request = Request("https://github.com/login");

        var rejected = await AutofillCoordinator.HandleDiscoverAsync(
            session,
            request,
            new FakePrompter(entry.Id, "wrong-key"));
        Assert.Equal("error", rejected.Type);
        Assert.Null(rejected.Password);

        var cancelled = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(null, null));
        Assert.Equal("cancelled", cancelled.Type);

        var filled = await AutofillCoordinator.HandleDiscoverAsync(
            session,
            request,
            new FakePrompter(entry.Id, "short-key"));
        Assert.Equal("fill", filled.Type);
        Assert.Equal("ada", filled.Username);
        Assert.Equal("s3cret", filled.Password);

        session.Lock();
        var locked = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key"));
        Assert.Equal("locked", locked.Type);
    }

    [Fact]
    public async Task Coordinator_FillsTheChosenAccountOfMultiAccountLogin()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var readOnly = new VaultAccount { Username = "reader", Password = "read-pw", Note = "只读" };
        var writer = new VaultAccount { Username = "writer", Password = "write-pw" };
        var entry = new VaultEntry { Name = "后台", Url = "https://admin.example.com", Accounts = [readOnly, writer] };
        session.Upsert(entry);
        var request = Request("https://admin.example.com/login");

        var matches = AutofillMatcher.Match(session.Entries, request);
        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, match => match.AccountId == readOnly.Id && match.Username == "reader" && match.AccountNote == "只读");

        var filled = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key", writer.Id));
        Assert.Equal("fill", filled.Type);
        Assert.Equal("writer", filled.Username);
        Assert.Equal("write-pw", filled.Password);

        var forged = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key", Guid.NewGuid()));
        Assert.Equal("cancelled", forged.Type);
    }

    [Fact]
    public void Coordinator_BrowserCandidateFlowKeepsPasswordsOutOfDiscovery()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var account = new VaultAccount { Username = "ada", Password = "s3cret" };
        var entry = new VaultEntry { Name = "GitHub", Url = "https://github.com", Accounts = [account] };
        session.Upsert(entry);
        var request = Request("https://github.com/login");

        var discovered = AutofillCoordinator.DiscoverCandidates(session, request);
        var candidate = Assert.Single(discovered.Candidates!);
        Assert.Equal("candidates", discovered.Type);
        Assert.Null(discovered.Password);
        Assert.DoesNotContain("password", AutofillProtocol.Serialize(discovered), StringComparison.OrdinalIgnoreCase);

        var fillRequest = new AutofillRequest
        {
            Operation = "fill-candidate",
            Origin = AutofillOrigin.Browser,
            Url = request.Url,
            EntryId = candidate.Id,
            AccountId = candidate.AccountId,
            ShortKey = "short-key"
        };
        var filled = AutofillCoordinator.FillCandidate(session, fillRequest);
        Assert.Equal("fill", filled.Type);
        Assert.Equal("ada", filled.Username);
        Assert.Equal("s3cret", filled.Password);

        fillRequest = new AutofillRequest
        {
            Operation = "fill-candidate",
            Origin = AutofillOrigin.Browser,
            Url = "https://forged.example/login",
            EntryId = candidate.Id,
            AccountId = candidate.AccountId,
            ShortKey = "short-key"
        };
        Assert.Equal("cancelled", AutofillCoordinator.FillCandidate(session, fillRequest).Type);
    }

    [Fact]
    public void LockedSessionCanReadProtectedAutofillMetadataWithoutPasswords()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var entry = new VaultEntry
        {
            Name = "GitHub",
            Url = "https://github.com",
            Accounts = [new VaultAccount { Username = "ada", Password = "s3cret", Note = "private account note" }]
        };
        session.Upsert(entry);
        session.Save();
        session.Lock();

        var matches = AutofillIndexStore.Match(path, Request("https://github.com/login"), out var available);
        Assert.True(available);
        Assert.Equal("ada", Assert.Single(matches).Username);
        var stored = File.ReadAllBytes(path + ".autofill-index");
        var plaintext = System.Security.Cryptography.ProtectedData.Unprotect(stored,
            System.Text.Encoding.UTF8.GetBytes("keep-password/autofill-index/v1"),
            System.Security.Cryptography.DataProtectionScope.CurrentUser);
        try
        {
            var json = System.Text.Encoding.UTF8.GetString(plaintext);
            Assert.DoesNotContain("s3cret", json);
            Assert.DoesNotContain("private account note", json);
            Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    [Theory]
    [InlineData("short-key", "https://github.com/login", "fill")]
    [InlineData("wrong-key", "https://github.com/login", "error")]
    [InlineData("", "https://github.com/login", "error")]
    [InlineData("short-key", "https://github.com.attacker.example/login", "cancelled")]
    public void LockedBrowserFillRequiresShortKeyAndCurrentDomain(string shortKey, string url, string expected)
    {
        var path = TestVault.NewPath();
        using var desktopSession = TestVault.CreateUnlocked(path);
        var account = new VaultAccount { Username = "ada", Password = "s3cret" };
        var entry = new VaultEntry { Name = "GitHub", Url = "https://github.com", Accounts = [account] };
        desktopSession.Upsert(entry);
        desktopSession.Save();
        desktopSession.Lock();

        var response = AutofillCoordinator.FillCandidateFromLockedVault(TestVault.Store(), path, new AutofillRequest
        {
            Operation = "fill-candidate",
            Origin = AutofillOrigin.Browser,
            Url = url,
            EntryId = entry.Id,
            AccountId = account.Id,
            ShortKey = shortKey
        });

        Assert.Equal(expected, response.Type);
        Assert.Equal(expected == "fill" ? "s3cret" : null, response.Password);
        Assert.False(desktopSession.IsUnlocked);
    }

    [Fact]
    public void AutofillIndexReflectsDeletedAccountsAndHandlesCorruption()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var entry = new VaultEntry
        {
            Name = "GitHub", Url = "https://github.com",
            Accounts = [new VaultAccount { Username = "ada", Password = "s3cret" }]
        };
        session.Upsert(entry);
        session.Save();
        Assert.Single(AutofillIndexStore.Match(path, Request("https://github.com/login"), out _));
        Assert.Empty(AutofillIndexStore.Match(path, Request("https://unrelated.example/login"), out _));
        session.Remove(entry.Id);
        session.Save();
        session.Lock();
        Assert.Empty(AutofillIndexStore.Match(path, Request("https://github.com/login"), out var available));
        Assert.True(available);

        File.WriteAllText(path + ".autofill-index", "corrupted cache");
        Assert.False(AutofillIndexStore.TryRead(path, out _));
    }

    [Fact]
    public async Task Coordinator_DesktopDatabaseRequiresAuthorizedAccountAndShortKey()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var account = new VaultAccount { Username = "db-user", Password = "db-password" };
        var entry = new VaultEntry
        {
            Kind = VaultItemKind.Database,
            Name = "测试数据库",
            Fields = new() { [FieldKeys.Host] = "db.internal" },
            Accounts = [account]
        };
        session.Upsert(entry);
        var request = new AutofillRequest { Origin = AutofillOrigin.WindowsApplication, WindowTitle = "新建连接 - DbX" };

        var rejected = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "wrong-key", account.Id));
        Assert.Equal("error", rejected.Type);
        Assert.Null(rejected.Password);

        var forged = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key", Guid.NewGuid()));
        Assert.Equal("cancelled", forged.Type);
        Assert.Null(forged.Password);

        var filled = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key", account.Id));
        Assert.Equal("fill", filled.Type);
        Assert.Equal(account.Username, filled.Username);
        Assert.Equal(account.Password, filled.Password);

        var lockedDuringConfirmation = await AutofillCoordinator.HandleDiscoverAsync(session, request,
            new FakePrompter(entry.Id, "short-key", account.Id, beforeDecision: session.Lock));
        Assert.Equal("error", lockedDuringConfirmation.Type);
        Assert.Null(lockedDuringConfirmation.Password);

        var locked = await AutofillCoordinator.HandleDiscoverAsync(session, request, new FakePrompter(entry.Id, "short-key", account.Id));
        Assert.Equal("locked", locked.Type);
        Assert.Null(locked.Password);
    }

    [Fact]
    public async Task Coordinator_BrowserCancelsWhenEntryDomainChangesDuringConfirmation()
    {
        using var session = TestVault.CreateUnlocked(TestVault.NewPath());
        var account = new VaultAccount { Username = "ada", Password = "pw" };
        var entry = new VaultEntry { Name = "原网站", Url = "https://original.example.com", Accounts = [account] };
        session.Upsert(entry);

        var response = await AutofillCoordinator.HandleDiscoverAsync(session, Request("https://original.example.com/login"),
            new FakePrompter(entry.Id, "short-key", account.Id, beforeDecision: () =>
            {
                entry.Url = "https://different.example.net";
                session.Upsert(entry);
            }));

        Assert.Equal("cancelled", response.Type);
        Assert.Null(response.Password);
    }

    [Fact]
    public void Paths_DifferByPlatform_AndVaultNameIsStable()
    {
        Assert.Equal(
            @"C:\Users\ada\AppData\Local\KeepPassword\vault.kpvault",
            AppDataPaths.VaultFileFor(PlatformKind.Windows, @"C:\Users\ada", @"C:\Users\ada\AppData\Local"));
        Assert.Equal(
            "/Users/ada/Library/Application Support/KeepPassword/vault.kpvault",
            AppDataPaths.VaultFileFor(PlatformKind.MacOS, "/Users/ada", null));
        Assert.Equal(
            "/home/ada/.local/share/KeepPassword/vault.kpvault",
            AppDataPaths.VaultFileFor(PlatformKind.Linux, "/home/ada", null));
        Assert.Equal(
            @"C:\Users\ada\AppData\Roaming\KeepPassword\settings.json",
            CacheDirectory.SettingsFileFor(PlatformKind.Windows, @"C:\Users\ada", @"C:\Users\ada\AppData\Roaming"));
        Assert.Equal(
            "/Users/ada/Library/Preferences/KeepPassword/settings.json",
            CacheDirectory.SettingsFileFor(PlatformKind.MacOS, "/Users/ada", null));
        Assert.Equal(
            "/home/ada/.config/KeepPassword/settings.json",
            CacheDirectory.SettingsFileFor(PlatformKind.Linux, "/home/ada", null));
    }

    [Fact]
    public void NativeMessage_RoundTripsLengthPrefix()
    {
        using var stream = new MemoryStream();
        NativeMessageFraming.Write(stream, "{\"type\":\"discover\"}");
        stream.Position = 0;
        Assert.Equal("{\"type\":\"discover\"}", NativeMessageFraming.Read(stream));
    }

    [Fact]
    public void NativeHost_StaleSessionReturnsConnectionError()
    {
        var path = Path.Combine(Path.GetTempPath(), "kp-stale-" + Guid.NewGuid().ToString("n") + ".json");
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        AutofillChannel.Create(port).Publish(path);
        listener.Stop();
        try
        {
            var response = NativeHostBridge.Forward("{\"type\":\"discover\",\"url\":\"https://example.com\"}", path);
            Assert.Contains("\"type\":\"error\"", response);
            Assert.DoesNotContain("\"password\"", response);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static AutofillRequest Request(string url) => new()
    {
        Origin = AutofillOrigin.Browser,
        Url = url
    };

    private sealed class FakePrompter : IAutofillPrompter
    {
        private readonly Guid? _id;
        private readonly string? _shortKey;
        private readonly Guid? _accountId;
        private readonly Action? _beforeDecision;

        public FakePrompter(Guid? id, string? shortKey, Guid? accountId = null, Action? beforeDecision = null)
        {
            _id = id;
            _shortKey = shortKey;
            _accountId = accountId;
            _beforeDecision = beforeDecision;
        }

        public Task<AutofillDecision?> PromptAsync(
            AutofillRequest request,
            IReadOnlyList<AutofillCandidate> matches,
            Func<string, bool> verifyShortKey,
            CancellationToken cancellationToken)
        {
            if (_id is null || _shortKey is null)
            {
                return Task.FromResult<AutofillDecision?>(null);
            }

            _beforeDecision?.Invoke();
            return Task.FromResult<AutofillDecision?>(new AutofillDecision
            {
                EntryId = _id.Value,
                AccountId = _accountId ?? matches.FirstOrDefault(match => match.Id == _id.Value)?.AccountId ?? Guid.Empty,
                ShortKey = _shortKey
            });
        }
    }
}
