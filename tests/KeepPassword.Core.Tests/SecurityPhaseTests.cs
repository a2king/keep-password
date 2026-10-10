using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using KeepPassword.Core.Autofill;
using KeepPassword.Core.Import;
using KeepPassword.Core.Install;
using KeepPassword.Core.Messaging;
using KeepPassword.Core.Security;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class SecurityPhaseTests
{
    [Fact]
    public void HostsMatch_RejectsPublicSuffixAndSingleLabel()
    {
        Assert.False(DomainGrouping.HostsMatch("com", "github.com"));
        Assert.False(DomainGrouping.HostsMatch("co.uk", "example.co.uk"));
        Assert.False(DomainGrouping.HostsMatch("notevil.com", "evil.com"));
        Assert.False(DomainGrouping.HostsMatch("github.com.evil.com", "github.com"));
        Assert.True(DomainGrouping.HostsMatch("github.com", "login.github.com"));
        Assert.True(DomainGrouping.HostsMatch("example.co.uk", "www.example.co.uk"));
        Assert.False(DomainGrouping.TitleContainsHost("notgithub.com - login", "github.com"));
        Assert.True(DomainGrouping.TitleContainsHost("github.com - 登录", "github.com"));
    }

    [Fact]
    public void AutoType_RefusesElevation_AndTypesUsernameTabPassword()
    {
        Assert.False(AutofillPolicy.AllowAutoType(targetElevated: true));
        Assert.True(AutofillPolicy.IsExtensionBrowser("chrome.exe"));
        Assert.False(AutofillPolicy.IsExtensionBrowser("notepad.exe"));
        var steps = AutofillPolicy.PlanAutoType("ada", "secret");
        Assert.Equal(AutoTypeAction.Text, steps[0].Action);
        Assert.Equal("ada", steps[0].Text);
        Assert.Equal(AutoTypeAction.Tab, steps[1].Action);
        Assert.Equal("secret", steps[2].Text);
        Assert.True(SessionLockPolicy.ShouldLock(TimeSpan.FromMinutes(5), false, false));
        Assert.True(SessionLockPolicy.ShouldLock(TimeSpan.Zero, true, false));
        Assert.True(SessionLockPolicy.ShouldLock(TimeSpan.Zero, false, true));
        Assert.False(SessionLockPolicy.ShouldLock(TimeSpan.FromMinutes(4), false, false));
    }

    [Fact]
    public void Gate_AllowsOnlyOnePrompt()
    {
        var gate = new AutofillGate(TimeSpan.FromMinutes(1), maxAttempts: 3);
        Assert.True(gate.TryEnter("github.com", out var first));
        Assert.False(gate.TryEnter("github.com", out _));
        first!.Dispose();
        Assert.True(gate.TryEnter("other", out var second));
        second!.Dispose();
    }

    [Fact]
    public async Task Listener_RequiresToken()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n") + ".json");
        using var listener = AutofillListener.Start((_, _) => Task.FromResult(AutofillProtocol.Serialize(AutofillResponse.NoMatch())));
        listener.Publish(path);
        var denied = JsonSerializer.Deserialize<JsonElement>(Send(listener.Channel.Port, "{\"type\":\"discover\",\"url\":\"https://github.com\"}"));
        Assert.Equal("error", denied.GetProperty("type").GetString());
        Assert.Contains("未授权", denied.GetProperty("message").GetString());

        var accepted = NativeHostBridge.Forward("{\"type\":\"discover\",\"url\":\"https://github.com\"}", path);
        Assert.Contains("noMatch", accepted);
        await Task.CompletedTask;
    }

    [Fact]
    public void Vault_RejectsExcessiveKdf_AndRotatesMasterPassword()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        store.Create(path, "ada", "correct horse", "short-key");
        var json = File.ReadAllText(path).Replace("\"memoryKb\":4096", "\"memoryKb\":99999999", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        Assert.Throws<InvalidDataException>(() => store.Unlock(path, "correct horse"));

        var fresh = TestVault.NewPath();
        using (var session = TestVault.CreateUnlocked(fresh))
        {
            session.Upsert(new VaultEntry { Name = "保留", Url = "https://example.com", Username = "ada", Password = "pw" });
            session.Save();
            session.ChangeMasterPassword("correct horse", "new horse");
        }

        Assert.Throws<UnlockFailedException>(() => TestVault.Store().Unlock(fresh, "correct horse"));
        using var unlocked = TestVault.Store().Unlock(fresh, "new horse");
        Assert.Equal("pw", Assert.Single(unlocked.Entries).Password);
    }

    [Fact]
    public void Recovery_RequiresRiskAcknowledgement_AndDoesNotStoreAnswers()
    {
        var path = TestVault.NewPath();
        var store = TestVault.Store();
        var setup = new RecoverySetup
        {
            RiskAcknowledged = false,
            Questions = ["一", "二", "三"],
            Answers = ["alpha-answer", "beta-answer", "gamma-answer"]
        };
        var denied = Assert.Throws<ArgumentException>(() => store.Create(path, "ada", "correct horse", "short-key", setup));
        Assert.Contains("远弱于主密码", denied.Message);

        store.Create(path, "ada", "correct horse", "short-key", new RecoverySetup
        {
            RiskAcknowledged = true,
            Questions = setup.Questions,
            Answers = setup.Answers
        });
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("alpha-answer", text);
        Assert.Contains("远弱于主密码", RecoveryRisk.Warning);
        Assert.True(store.HasRecovery(path));
        Assert.Throws<RecoveryFailedException>(() => store.Recover(path, ["no", "no", "no"], "other horse"));

        using var recovered = store.Recover(path, ["alpha-answer", "beta-answer", "gamma-answer"], "other horse");
        recovered.Upsert(new VaultEntry { Name = "恢复后", Username = "ada", Password = "kept", Url = "https://example.com" });
        recovered.Save();
        recovered.Lock();
        using var unlocked = store.Unlock(path, "other horse");
        Assert.Contains(unlocked.Entries, entry => entry.Name == "恢复后");
        Assert.True(unlocked.HasRecovery);
    }

    [Fact]
    public void ExtractZip_RejectsTraversal()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("../outside.txt").Open());
            writer.Write("nope");
        }

        stream.Position = 0;
        var destination = Path.Combine(Path.GetTempPath(), "kp-zip-" + Guid.NewGuid().ToString("n"));
        Assert.Throws<InvalidDataException>(() => InstallOperations.ExtractZip(stream, destination));
    }

    [Fact]
    public void PayloadHash_RejectsMismatch()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("payload"));
        var hash = PayloadIntegrity.Sha256Hex(stream);
        PayloadIntegrity.Verify(stream, hash);
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => PayloadIntegrity.Verify(stream, new string('a', 64)));
    }

    [Fact]
    public async Task Clipboard_ClearsOnlyItsOwnValue()
    {
        var clipboard = new MemoryClipboard();
        var service = new ExpiringClipboard();
        await service.CopyAsync(clipboard, "secret", TimeSpan.Zero, (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.Equal("", clipboard.Value);

        await service.CopyAsync(clipboard, "secret", TimeSpan.Zero, (_, _) =>
        {
            clipboard.Value = "user";
            return Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal("user", clipboard.Value);
    }

    [Fact]
    public void AuditAndBreach_DoNotExposePasswords()
    {
        var reused = new VaultEntry { Name = "A", Url = "https://a.example", Username = "ada", Password = "password" };
        var other = new VaultEntry { Name = "B", Url = "https://b.example", Username = "ada", Password = "password" };
        var empty = new VaultEntry { Name = "C", Url = "https://c.example", Username = "ada", Password = "" };
        var findings = VaultAudit.Analyze([reused, other, empty]);
        var rendered = string.Join('\n', findings.Select(finding => finding.Name + finding.Advice));
        Assert.DoesNotContain("password", rendered);
        Assert.Contains(findings, finding => finding.Kind == AuditKind.Reused);
        Assert.Contains(findings, finding => finding.Kind == AuditKind.Empty);

        const string body = "0018A45C4D1DEF81644B54AB7F969B88D65:1\nFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF:0";
        var hash = BreachChecker.Sha1Hex("password");
        Assert.Equal(5, hash[..5].Length);
        Assert.Equal(0, BreachChecker.MatchCount("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF:0", hash));
        Assert.Equal(1, BreachChecker.MatchCount(hash[5..] + ":1\n" + body, hash));
        Assert.Contains("前 5 位", BreachChecker.PrivacyNotice);
    }

    [Fact]
    public void Import_ReadsOnePasswordAndChrome_AndBackupRoundTrips()
    {
        var imported = CsvImporter.Import("""
            Title,Website,Username,Password,Notes,OTPAuth
            GitHub,https://github.com,ada,s3cret,工作,otpauth://totp/GitHub?secret=GEZDGNBVGY3TQOJQ&issuer=GitHub
            """);
        var entry = Assert.Single(imported);
        Assert.Equal("GitHub", entry.Name);
        Assert.Equal("https://github.com", entry.Url);
        Assert.Equal("GEZDGNBVGY3TQOJQ", entry.TotpSecret);

        var path = TestVault.NewPath();
        var backup = TestVault.NewPath();
        using (var session = TestVault.CreateUnlocked(path))
        {
            session.Upsert(entry);
            session.Save();
            EncryptedBackup.Export(path, backup);
        }

        var restored = EncryptedBackup.ReadEntries(backup, "correct horse");
        Assert.Equal("ada", Assert.Single(restored).Username);
        Assert.DoesNotContain("s3cret", File.ReadAllText(backup));
    }

    [Fact]
    public void Extension_RequiresExplicitFillRequest()
    {
        var root = RepoRoot();
        var content = File.ReadAllText(Path.Combine(root, "extension", "content.js"));
        var manifest = File.ReadAllText(Path.Combine(root, "extension", "manifest.json"));
        Assert.DoesNotContain("MutationObserver", content);
        Assert.Contains("fill-request", content);
        Assert.DoesNotContain("host_permissions", manifest);
        Assert.Contains("fill-password", manifest);
        Assert.Contains("\"version\": \"0.3.1\"", manifest);
    }

    [Fact]
    public void Generator_UsesRequestedClasses_AndExcludesAmbiguousCharacters()
    {
        var password = PasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Length = 24,
            Lowercase = true,
            Uppercase = true,
            Digits = true,
            Symbols = false,
            ExcludeAmbiguous = true
        });
        Assert.Equal(24, password.Length);
        Assert.DoesNotContain('0', password);
        Assert.DoesNotContain('O', password);
        Assert.DoesNotContain('1', password);
        Assert.DoesNotContain('l', password);
        Assert.DoesNotContain('I', password);
        Assert.Equal(PasswordStrength.Strong, PasswordStrengthEvaluator.Evaluate(password));
    }

    [Fact]
    public void Generator_DefaultLogin_IsSixteenLettersAndDigits()
    {
        var password = PasswordGenerator.Generate(PasswordGeneratorOptions.DefaultLogin);
        Assert.Equal(16, password.Length);
        Assert.Contains(password, char.IsLetter);
        Assert.Contains(password, char.IsDigit);
        Assert.DoesNotContain(password, character => !char.IsLetterOrDigit(character));
    }

    [Fact]
    public void Generator_Pin_IsOnlyDigits()
    {
        var pin = PasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Kind = PasswordGeneratorKind.Pin,
            Length = 6
        });
        Assert.Equal(6, pin.Length);
        Assert.Matches("^[0-9]{6}$", pin);
    }

    [Fact]
    public void Generator_Memorable_JoinsWords()
    {
        var password = PasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Kind = PasswordGeneratorKind.Memorable,
            Length = 4,
            FullWords = true,
            Capitalize = true
        });
        var parts = password.Split('-');
        Assert.Equal(4, parts.Length);
        Assert.All(parts, part => Assert.Matches("^[A-Z][a-z]+$", part));
    }

    private static string Send(int port, string json)
    {
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        using var stream = client.GetStream();
        NativeMessageFraming.Write(stream, json);
        return NativeMessageFraming.Read(stream) ?? "";
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "KeepPassword.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException("找不到仓库根目录。");
    }

    private sealed class MemoryClipboard : ITextClipboard
    {
        public string? Value { get; set; }

        public Task<string?> ReadAsync() => Task.FromResult(Value);

        public Task WriteAsync(string? value)
        {
            Value = value;
            return Task.CompletedTask;
        }
    }
}
