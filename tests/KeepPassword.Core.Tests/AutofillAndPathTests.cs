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
            Username = "ada",
            Password = "pw"
        };
        var totpOnly = new VaultEntry
        {
            Id = Guid.NewGuid(),
            Name = "令牌",
            TotpSecret = "GEZDGNBVGY3TQOJQ"
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

    [Fact]
    public async Task Coordinator_RequiresShortKeyBeforeFill()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateUnlocked(path);
        var entry = new VaultEntry
        {
            Name = "GitHub",
            Url = "https://github.com",
            Username = "ada",
            Password = "s3cret"
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

    private static AutofillRequest Request(string url) => new()
    {
        Origin = AutofillOrigin.Browser,
        Url = url
    };

    private sealed class FakePrompter : IAutofillPrompter
    {
        private readonly Guid? _id;
        private readonly string? _shortKey;

        public FakePrompter(Guid? id, string? shortKey)
        {
            _id = id;
            _shortKey = shortKey;
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

            return Task.FromResult<AutofillDecision?>(new AutofillDecision
            {
                EntryId = _id.Value,
                ShortKey = _shortKey
            });
        }
    }
}
