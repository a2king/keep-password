using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class CloudSyncContractTests
{
    private const string Password = "website-password-12";
    private const string Token = "device-token-test";
    private const string Code = "ABCD2345";

    [Fact]
    public void DeviceBindCode_RejectsSixDigits_AndAcceptsEight()
    {
        var six = Assert.Throws<ArgumentException>(() => DeviceBindCode.Normalize("123456"));
        Assert.Contains("8 位", six.Message);
        Assert.DoesNotContain("123456", six.Message);
        Assert.Equal("ABCD2345", DeviceBindCode.Normalize(" abcd2345 "));
        Assert.DoesNotContain("已发送", SyncMessages.CodeAccepted);
        Assert.DoesNotContain("未注册", SyncMessages.CodeAccepted);
        Assert.DoesNotContain("不存在", SyncMessages.CodeAccepted);
    }

    [Fact]
    public async Task RequestCode_DoesNotTreatHttp200AsProofOfDelivery()
    {
        var handler = new Script();
        handler.On = (_, _) => Json(200, """{"ok":true}""");
        using var api = new SyncApiClient("http://sync.test", handler);
        await api.RequestDeviceCodeAsync("person@example.com");
        Assert.Equal("POST", handler.Calls[0].Method);
        Assert.EndsWith("/api/sync/device/request-code", handler.Calls[0].Path);
        Assert.Contains("若该邮箱已完成验证", SyncMessages.CodeAccepted);
    }

    [Fact]
    public async Task Bind_EmptyCloud_UploadsWrapThenVault_AndDoesNotSaveSyncFile()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/api/sync/device/bind"))
            {
                return Json(200, BindBody(vaultVersion: 0, hasWrappedKey: false));
            }

            if (method == "PUT" && path.EndsWith("/api/sync/commit"))
            {
                return Json(200, """{"ok":true,"keyVersion":1,"vaultVersion":1}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast);
        Assert.True(result.VaultSynced);
        Assert.False(result.AdoptedCloudKey);
        Assert.Equal(1, result.State.VaultVersion);
        Assert.Equal(1, result.State.KeyVersion);
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        Assert.Equal(
            ["POST /api/sync/device/bind", "PUT /api/sync/commit"],
            handler.Calls.Select(call => call.Method + " " + call.Path).ToArray());
        Assert.Contains("\"purpose\":\"establish\"", handler.Calls[1].Body);
        AssertNoSecrets(handler, session);
    }

    [Fact]
    public async Task Bind_ExistingWrapSameKey_DoesNotOverwriteWrap()
    {
        using var session = Local("only-local");
        var key = session.CopyDataKey();
        var wrap = AccountKeyWrap.Wrap(Password, key, KdfProfile.Fast);
        var vault = await File.ReadAllBytesAsync(session.VaultPath);
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(1, true));
            }

            if (method == "GET" && path.EndsWith("/wrapped-key"))
            {
                return Json(200, Wrapped(wrap));
            }

            if (method == "GET" && path.EndsWith("/vault"))
            {
                return Json(200, Vault(1, vault));
            }

            if (method == "PUT" && path.EndsWith("/vault"))
            {
                return Json(200, """{"ok":true,"vaultVersion":2}""");
            }

            return Json(500, """{"error":"不应覆盖密钥包"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast);
        Assert.True(result.VaultSynced);
        Assert.False(result.AdoptedCloudKey);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT" && call.Path.Contains("wrapped-key"));
        Assert.Contains(handler.Calls, call => call.Method == "GET" && call.Path.Contains("wrapped-key"));
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(wrap);
    }

    [Fact]
    public async Task Bind_ExistingWrapDifferentKey_MergesWithoutOverwritingWrapOrDroppingLocal()
    {
        var path = TestVault.NewPath();
        string shortKey = "short-key";
        byte[] oldKey;
        using (var session = TestVault.CreateLocalUnlocked(path, shortKey))
        {
            session.Upsert(Entry("only-local"));
            session.Save();
            oldKey = session.CopyDataKey();
            var remotePath = TestVault.NewPath();
            byte[] remoteKey;
            byte[] remoteVault;
            using (var remote = TestVault.CreateLocalUnlocked(remotePath))
            {
                remote.Upsert(Entry("only-remote"));
                remote.Save();
                remoteKey = remote.CopyDataKey();
                remoteVault = await File.ReadAllBytesAsync(remotePath);
            }

            var wrap = AccountKeyWrap.Wrap(Password, remoteKey, KdfProfile.Fast);
            byte[]? uploaded = null;
            var handler = new Script();
            handler.On = (method, pathName) =>
            {
                if (method == "POST" && pathName.EndsWith("/device/bind"))
                {
                    return Json(200, BindBody(4, true));
                }

                if (method == "GET" && pathName.EndsWith("/wrapped-key"))
                {
                    return Json(200, Wrapped(wrap));
                }

                if (method == "GET" && pathName.EndsWith("/vault"))
                {
                    return Json(200, Vault(4, remoteVault));
                }

                if (method == "PUT" && pathName.EndsWith("/vault"))
                {
                    uploaded = Convert.FromBase64String(ReadField(handler.Calls[^1].Body, "dataBase64"));
                    return Json(200, """{"ok":true,"vaultVersion":5}""");
                }

                return Json(500, """{"error":"不应覆盖密钥包"}""");
            };

            using var service = Service(handler);
            var result = await service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast);
            Assert.True(result.AdoptedCloudKey);
            Assert.True(result.VaultSynced);
            Assert.False(File.Exists(path + ".sync"));
            Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT" && call.Path.Contains("wrapped-key"));
            Assert.NotNull(uploaded);
            var names = VaultStore.ReadEntriesWithDataKey(uploaded!, remoteKey).Select(entry => entry.Name).OrderBy(name => name).ToArray();
            Assert.Equal(["only-local", "only-remote"], names);
            Assert.Throws<CredentialRejectedException>(() => VaultStore.ReadEntriesWithDataKey(uploaded!, oldKey));
            AssertNoSecrets(handler, session);
            CryptographicOperations.ZeroMemory(remoteKey);
            CryptographicOperations.ZeroMemory(wrap);
        }

        using var unlocked = TestVault.Store().UnlockWithShortKey(path, shortKey);
        Assert.Equal(["only-local", "only-remote"], unlocked.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
        CryptographicOperations.ZeroMemory(oldKey);
    }

    [Fact]
    public async Task Bind_VaultWithoutWrap_RefusesWhenLocalKeyDoesNotFit()
    {
        using var session = Local("only-local");
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var remotePath = TestVault.NewPath();
        byte[] remoteVault;
        using (var remote = TestVault.CreateLocalUnlocked(remotePath))
        {
            remote.Upsert(Entry("cloud"));
            remote.Save();
            remoteVault = await File.ReadAllBytesAsync(remotePath);
        }

        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(3, false, hasVault: true));
            }

            if (method == "GET" && path.EndsWith("/vault"))
            {
                return Json(200, Vault(3, remoteVault));
            }

            if (method == "POST" && path.EndsWith("/unbind-self"))
            {
                return Json(200, """{"ok":true}""");
            }

            return Json(500, """{"error":"不应上传"}""");
        };

        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() =>
            service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast));
        Assert.Contains("无法解密", error.Message);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        Assert.Contains(handler.Calls, call => call.Path.Contains("unbind-self"));
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
        Assert.Equal("only-local", Assert.Single(session.Entries).Name);
    }

    [Fact]
    public async Task Bind_WrapUploadFailure_UnbindsAndDoesNotSave()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(0, false));
            }

            if (method == "PUT" && path.EndsWith("/commit"))
            {
                return Json(400, """{"error":"密钥包无效"}""");
            }

            if (method == "POST" && path.EndsWith("/unbind-self"))
            {
                return Json(200, """{"ok":true}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() =>
            service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("密钥包无效", error.Message);
        Assert.DoesNotContain("SMTP_PASSWORD", error.Message);
        Assert.Contains(handler.Calls, call => call.Path.Contains("unbind-self"));
        Assert.False(File.Exists(session.VaultPath + ".sync"));
    }

    [Fact]
    public async Task Bind_VaultUploadFailure_KeepsDeviceButDoesNotClaimSyncedOrWriteFileEarly()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(0, false));
            }

            if (method == "PUT" && path.EndsWith("/commit"))
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("<html>" + Token + "</html>", Encoding.UTF8, "text/html")
                };
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast);
        Assert.False(result.VaultSynced);
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("unbind-self"));
        Assert.Equal("only-local", Assert.Single(session.Entries).Name);
    }

    [Fact]
    public async Task Bind_WrongPasswordCannotUnwrap_StopsBeforeVaultWrite()
    {
        using var session = Local("only-local");
        var other = RandomNumberGenerator.GetBytes(32);
        var wrap = AccountKeyWrap.Wrap("other-password-12", other, KdfProfile.Fast);
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(2, true));
            }

            if (method == "GET" && path.EndsWith("/wrapped-key"))
            {
                return Json(200, Wrapped(wrap));
            }

            if (method == "POST" && path.EndsWith("/unbind-self"))
            {
                return Json(200, """{"ok":true}""");
            }

            return Json(500, """{"error":"不应继续"}""");
        };

        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() =>
            service.CompleteBindAsync(session, Request(), Password, wrapProfile: KdfProfile.Fast));
        Assert.Contains("无法解开", error.Message);
        Assert.DoesNotContain(Password, error.Message);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        CryptographicOperations.ZeroMemory(other);
        CryptographicOperations.ZeroMemory(wrap);
    }

    [Fact]
    public async Task Conflict_MergesAndKeepsNewerLocal_SecondConflictDoesNotClaimSuccess()
    {
        using var session = Local("local-newer");
        var shared = session.Entries.Single().Id;
        session.Upsert(Entry("local-newer", shared, DateTimeOffset.Parse("2026-05-01T00:00:00Z")));
        session.Save();
        var key = session.CopyDataKey();
        var remotePath = TestVault.NewPath();
        byte[] remoteVault;
        using (var remote = TestVault.CreateLocalUnlocked(remotePath))
        {
            remote.ReplaceWithDataKey(key, [Entry("remote-older", shared, DateTimeOffset.Parse("2026-01-01T00:00:00Z"))], [], []);
            remoteVault = await File.ReadAllBytesAsync(remotePath);
        }

        var conflicts = 0;
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "PUT" && path.EndsWith("/vault"))
            {
                conflicts++;
                return Json(409, """{"error":"版本冲突","conflict":true,"vaultVersion":8}""");
            }

            if (method == "GET" && path.EndsWith("/vault"))
            {
                return Json(200, Vault(8, remoteVault));
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var state = new SyncSessionState { DeviceToken = Token, VaultVersion = 3, BaseUrl = "http://sync.test" };
        var error = await Assert.ThrowsAsync<SyncApiException>(() => service.PushAsync(session, state, key));
        Assert.Equal(SyncFailureKind.Conflict, error.Kind);
        Assert.Equal(409, error.StatusCode);
        Assert.Contains("本地数据已保留", error.Message);
        Assert.Equal("local-newer", session.Entries.Single(entry => entry.Id == shared).Name);
        Assert.True(conflicts >= 2);
        CryptographicOperations.ZeroMemory(key);
    }

    [Theory]
    [InlineData("账号不可用")]
    [InlineData("邮箱或密码不正确")]
    [InlineData("绑定密钥不正确或已失效")]
    public async Task Bind401_PreservesDistinctMessages(string message)
    {
        var handler = new Script();
        handler.On = (_, _) => Json(401, JsonSerializer.Serialize(new { error = message }));
        using var api = new SyncApiClient("http://sync.test", handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => api.BindDeviceAsync(Request()));
        Assert.Equal(401, error.StatusCode);
        Assert.False(error.SessionInvalid);
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public async Task Device401_IsSessionExpired_AndDoesNotEchoToken()
    {
        var handler = new Script();
        handler.On = (_, _) => Json(401, "{\"error\":\"设备令牌无效 " + Token + "\"}");
        using var api = new SyncApiClient("http://sync.test", handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => api.GetStatusAsync(Token));
        Assert.True(error.SessionInvalid);
        Assert.Equal(SyncMessages.SessionExpired, error.Message);
        Assert.DoesNotContain(Token, error.Message);
    }

    [Fact]
    public async Task TransportAndStatusMessages_DoNotLeakBodies()
    {
        await AssertTransport(
            new HttpRequestException("dns secret"),
            SyncFailureKind.Network,
            "无法连接同步服务器");
        await AssertTransport(
            new TaskCanceledException("timeout secret"),
            SyncFailureKind.Timeout,
            "连接超时");
        await AssertTransport(
            new HttpRequestException("tls secret", new System.Security.Authentication.AuthenticationException("tls secret")),
            SyncFailureKind.Tls,
            "安全连接失败");

        var html = new Script();
        html.On = (_, _) => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("<html>" + Token + "</html>")
        };
        using var api = new SyncApiClient("http://sync.test", html);
        var server = await Assert.ThrowsAsync<SyncApiException>(() => api.GetStatusAsync(Token));
        Assert.Equal(SyncFailureKind.Server, server.Kind);
        Assert.Equal(500, server.StatusCode);
        Assert.Contains("500", server.Message);
        Assert.DoesNotContain(Token, server.Message);
        Assert.DoesNotContain("<html>", server.Message);

        var invalid = new Script();
        invalid.On = (_, _) => Json(422, "{\"error\":\"<script>" + Password + "</script>\"}");
        using var validation = new SyncApiClient("http://sync.test", invalid);
        var bad = await Assert.ThrowsAsync<SyncApiException>(() => validation.GetStatusAsync(Token));
        Assert.Equal(422, bad.StatusCode);
        Assert.Equal("提交内容未通过校验，请检查后重试。", bad.Message);

        var limited = new Script();
        limited.On = (_, _) => Json(429, """{"error":"失败次数过多，请稍后再试"}""");
        using var rate = new SyncApiClient("http://sync.test", limited);
        var tooMany = await Assert.ThrowsAsync<SyncApiException>(() => rate.BindDeviceAsync(Request()));
        Assert.Equal(429, tooMany.StatusCode);
        Assert.Equal("失败次数过多，请稍后再试", tooMany.Message);

        var forbidden = new Script();
        forbidden.On = (_, _) => Json(403, """{"error":"账号已禁用"}""");
        using var denied = new SyncApiClient("http://sync.test", forbidden);
        var blocked = await Assert.ThrowsAsync<SyncApiException>(() => denied.BindDeviceAsync(Request()));
        Assert.Equal("账号已禁用", blocked.Message);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await Assert.ThrowsAsync<SyncApiException>(() => api.GetStatusAsync(Token, cts.Token));
        Assert.Equal(SyncFailureKind.Cancelled, cancelled.Kind);
        Assert.Equal("已取消。", cancelled.Message);
    }

    [Fact]
    public async Task RefreshAndPush_AreMutuallyExclusive()
    {
        using var session = Local("row");
        var key = session.CopyDataKey();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusSeen = 0;
        var handler = new Script();
        handler.OnAsync = async (method, path, _) =>
        {
            if (method == "PUT" && path.EndsWith("/vault"))
            {
                entered.TrySetResult();
                await release.Task;
                return Json(200, """{"ok":true,"vaultVersion":2}""");
            }

            if (method == "GET" && path.EndsWith("/status"))
            {
                Interlocked.Increment(ref statusSeen);
                return Json(200, """{"vaultVersion":1}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var state = new SyncSessionState { DeviceToken = Token, VaultVersion = 1, BaseUrl = "http://sync.test" };
        var push = service.PushAsync(session, state, key);
        await entered.Task;
        var refresh = service.RefreshAsync(session, state, key);
        await Task.Delay(150);
        Assert.Equal(0, statusSeen);
        release.TrySetResult();
        await push;
        await refresh;
        Assert.Equal(1, statusSeen);
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public void LegacySyncFile_StillLoads()
    {
        var path = TestVault.NewPath();
        var json = """
        {
          "email": "person@example.com",
          "deviceToken": "legacy-token",
          "deviceId": 7,
          "vaultVersion": 4,
          "bindingKeyFingerprint": "abcd",
          "baseUrl": "https://api.keep-password.work",
          "pollMinutes": 5
        }
        """;
        File.WriteAllBytes(path + ".sync", Encoding.UTF8.GetBytes(json));
        var state = SyncSessionState.TryLoad(path);
        Assert.NotNull(state);
        Assert.Equal("person@example.com", state!.Email);
        Assert.Equal(4, state.VaultVersion);
        Assert.Equal("legacy-token", state.DeviceToken);
    }

    [Fact]
    public async Task Refresh_KeyVersionChange_BlocksWithoutUpload()
    {
        using var session = Local("only-local");
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var handler = new Script();
        handler.On = (_, path) => path.EndsWith("/status")
            ? Json(200, """{"vaultVersion":4,"keyVersion":2,"hasWrappedKey":true,"serverTime":"2026-11-11T00:00:00Z"}""")
            : Json(500, """{"error":"不应继续"}""");
        var state = new SyncSessionState { DeviceToken = Token, VaultVersion = 1, KeyVersion = 1, BaseUrl = "http://sync.test" };
        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncKeyChangedException>(() => service.RefreshAsync(session, state, session.CopyDataKey()));
        Assert.Contains("若非本人操作，请修改账号密码，并到用户管理后台更新密钥文件", error.Message);
        Assert.DoesNotContain(Password, error.Message);
        Assert.True(state.SyncBlocked);
        Assert.Equal("key", state.SyncBlockReason);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
    }

    [Fact]
    public async Task Push_WhileBlocked_DoesNotUpload()
    {
        using var session = Local("only-local");
        var handler = new Script { On = (_, _) => Json(500, """{"error":"不应请求"}""") };
        var state = new SyncSessionState { DeviceToken = Token, VaultVersion = 1, KeyVersion = 1, SyncBlocked = true, SyncBlockReason = "key", BaseUrl = "http://sync.test" };
        using var service = Service(handler);
        await Assert.ThrowsAsync<SyncKeyChangedException>(() => service.PushAsync(session, state, session.CopyDataKey()));
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task Rotate_WrongKey_IsNotAnEmptyVault()
    {
        using var session = Local("only-local");
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var remotePath = TestVault.NewPath();
        byte[] remoteVault;
        using (var remote = TestVault.CreateLocalUnlocked(remotePath))
        {
            remote.Upsert(Entry("cloud"));
            remote.Save();
            remoteVault = await File.ReadAllBytesAsync(remotePath);
        }

        var handler = new Script();
        handler.On = (method, path) => method == "GET" && path.EndsWith("/vault")
            ? Json(200, Vault(2, remoteVault))
            : Json(500, """{"error":"不应提交"}""");
        var state = BoundState();
        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => service.RotateKeyAsync(
            session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), Password, confirmConflicts: true, wrapProfile: KdfProfile.Fast));
        Assert.Contains("不能当成空库", error.Message);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
        Assert.False(state.SyncBlocked);
    }

    [Fact]
    public async Task Rotate_ConflictNeedsConfirm_AndFailedCommitKeepsLocal()
    {
        using var session = Local("local-name");
        var shared = session.Entries.Single().Id;
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var current = session.CopyDataKey();
        var remoteEntry = Entry("remote-name", shared, DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        remoteEntry.Accounts[0].Password = "remote-secret";
        var remoteVault = session.EncryptSnapshot(current, [remoteEntry], session.Spaces, session.Tags);
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "GET" && path.EndsWith("/vault")) return Json(200, Vault(1, remoteVault));
            if (method == "PUT" && path.EndsWith("/commit")) return Json(409, """{"error":"版本冲突","conflict":true,"vaultVersion":3}""");
            return Json(500, """{"error":"意外"}""");
        };
        var state = BoundState();
        using var service = Service(handler);
        var preview = await service.RotateKeyAsync(session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), Password, confirmConflicts: false, wrapProfile: KdfProfile.Fast);
        Assert.True(preview.NeedsConfirmation);
        Assert.False(preview.Committed);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        var error = await Assert.ThrowsAsync<SyncConflictException>(() => service.RotateKeyAsync(
            session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), Password, confirmConflicts: true, wrapProfile: KdfProfile.Fast));
        Assert.DoesNotContain("remote-secret", error.Message);
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
        CryptographicOperations.ZeroMemory(current);
    }

    [Fact]
    public async Task Rotate_RetriesAfterServerFailure_ThenCommits()
    {
        using var session = Local("only-local");
        var current = session.CopyDataKey();
        var remoteVault = session.EncryptSnapshot(current, session.AllRecords, session.Spaces, session.Tags);
        var attempts = 0;
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "GET" && path.EndsWith("/vault")) return Json(200, Vault(1, remoteVault));
            if (method == "PUT" && path.EndsWith("/commit"))
            {
                attempts++;
                return attempts == 1
                    ? Json(503, """{"error":"同步服务器暂时无法处理"}""")
                    : Json(200, """{"ok":true,"keyVersion":2,"vaultVersion":2}""");
            }

            return Json(500, """{"error":"意外"}""");
        };
        var state = BoundState();
        using var service = Service(handler);
        var result = await service.RotateKeyAsync(session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), Password, confirmConflicts: true, wrapProfile: KdfProfile.Fast);
        Assert.True(result.Committed);
        Assert.Equal(2, attempts);
        Assert.Equal(2, state.KeyVersion);
        Assert.False(state.SyncBlocked);
        CryptographicOperations.ZeroMemory(current);
    }

    [Fact]
    public async Task Rotate_Cancel_LeavesVaultUntouched()
    {
        using var session = Local("only-local");
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var handler = new Script { On = (_, _) => Json(500, """{"error":"不应请求"}""") };
        using var service = Service(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RotateKeyAsync(
            session, BoundState(), VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), Password, true, cts.Token, KdfProfile.Fast));
        Assert.Empty(handler.Calls);
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
    }

    [Fact]
    public async Task Adopt_KeepsUnsyncedLocalEntry_AndWrongKeyStaysBlocked()
    {
        using var session = Local("local-only");
        var before = await File.ReadAllBytesAsync(session.VaultPath);
        var next = RandomNumberGenerator.GetBytes(32);
        var remoteEntry = Entry("cloud-only");
        var remoteVault = session.EncryptSnapshot(next, [remoteEntry], session.Spaces, session.Tags);
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "GET" && path.EndsWith("/vault")) return Json(200, Vault(2, remoteVault));
            if (method == "GET" && path.EndsWith("/status")) return Json(200, """{"vaultVersion":2,"keyVersion":2,"hasWrappedKey":true}""");
            if (method == "PUT" && path.EndsWith("/vault")) return Json(200, """{"ok":true,"vaultVersion":3}""");
            return Json(500, """{"error":"意外"}""");
        };
        var state = BoundState();
        state.SyncBlocked = true;
        state.SyncBlockReason = "key";
        using var service = Service(handler);
        var wrong = await Assert.ThrowsAsync<SyncApiException>(() => service.AdoptKeyFileAsync(
            session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), confirmConflicts: true));
        Assert.Contains("无法解开", wrong.Message);
        Assert.True(state.SyncBlocked);
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));

        var adopted = await service.AdoptKeyFileAsync(session, state, VaultKeyFile.Encode(next), confirmConflicts: true);
        Assert.True(adopted.Committed);
        Assert.False(state.SyncBlocked);
        Assert.Equal(2, state.KeyVersion);
        Assert.Equal(["cloud-only", "local-only"], session.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
        CryptographicOperations.ZeroMemory(next);
    }

    [Fact]
    public void Client_DoesNotReferenceMailboxProtocols()
    {
        var root = FindRepoRoot();
        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            if (name is not ("Directory.Build.props" or "KeepPassword.sln")
                && Path.GetExtension(file) is not (".cs" or ".csproj" or ".json" or ".props" or ".yml" or ".yaml"))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            string[] needles = ["Smtp" + "Client", "Mail" + "Kit", "Imap" + "Client", "Pop3" + "Client", "System.Net." + "Mail", "SMTP_" + "PASSWORD="];
            if (needles.Any(text.Contains))
            {
                hits.Add(Path.GetRelativePath(root, file));
            }
        }

        Assert.Empty(hits);
        Assert.Contains("Password", typeof(DeviceBindRequest).GetProperty(nameof(DeviceBindRequest.Password))!.Name);
    }

    private static SyncSessionState BoundState() => new()
    {
        DeviceToken = Token,
        VaultVersion = 1,
        KeyVersion = 1,
        BaseUrl = "http://sync.test"
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KeepPassword.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("找不到客户端仓库根目录。");
    }

    private static async Task AssertTransport(Exception thrown, SyncFailureKind kind, string snippet)
    {
        var handler = new Script { Throw = thrown };
        using var api = new SyncApiClient("http://sync.test", handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => api.PingAsync());
        Assert.Equal(kind, error.Kind);
        Assert.Contains(snippet, error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static void AssertNoSecrets(Script handler, VaultSession session)
    {
        var key = session.CopyDataKey();
        try
        {
            var raw = Convert.ToBase64String(key);
            foreach (var call in handler.Calls.Where(call => call.Path.Contains("vault") || call.Path.Contains("wrapped-key") || call.Path.Contains("commit")))
            {
                Assert.DoesNotContain(raw, call.Body);
                Assert.DoesNotContain(Password, call.Body);
                Assert.DoesNotContain(Code, call.Body);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static VaultSession Local(string name)
    {
        var session = TestVault.CreateLocalUnlocked(TestVault.NewPath());
        session.Upsert(Entry(name));
        session.Save();
        return session;
    }

    private static VaultEntry Entry(string name, Guid? id = null, DateTimeOffset? updated = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        UpdatedAt = updated ?? DateTimeOffset.Parse("2026-04-01T00:00:00Z"),
        Accounts = [new VaultAccount { Username = "user", Password = "local-secret" }]
    };

    private static DeviceBindRequest Request() => new()
    {
        Email = "person@example.com",
        Password = Password,
        BindingKey = "KPB-test-binding-key",
        Code = Code,
        DeviceName = "test",
        Platform = "Linux",
        ClientVersion = "0.9.1"
    };

    private static VaultSyncService Service(Script handler) => new("http://sync.test", handler);

    private static string BindBody(long vaultVersion, bool hasWrappedKey, bool? hasVault = null)
    {
        var vault = hasVault ?? vaultVersion > 0;
        return JsonSerializer.Serialize(new
        {
            deviceToken = Token,
            deviceId = 9,
            vaultVersion,
            bindingKeyFingerprint = "abcd",
            hasVault = vault,
            hasWrappedKey,
            serverTime = "2026-11-11T00:00:00Z"
        });
    }

    private static string Wrapped(byte[] wrap) =>
        JsonSerializer.Serialize(new { dataBase64 = Convert.ToBase64String(wrap) });

    private static string Vault(long version, byte[] data) =>
        JsonSerializer.Serialize(new
        {
            vaultVersion = version,
            dataBase64 = Convert.ToBase64String(data),
            serverTime = "2026-11-11T00:00:00Z"
        });

    private static string ReadField(string json, string name) =>
        JsonDocument.Parse(json).RootElement.GetProperty(name).GetString() ?? "";

    private static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class Script : HttpMessageHandler
    {
        public List<(string Method, string Path, string Body)> Calls { get; } = [];
        public Func<string, string, HttpResponseMessage>? On { get; set; }
        public Func<string, string, CancellationToken, Task<HttpResponseMessage>>? OnAsync { get; set; }
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = request.RequestUri?.AbsolutePath ?? "";
            Calls.Add((request.Method.Method, path, body));
            if (OnAsync is not null)
            {
                return await OnAsync(request.Method.Method, path, cancellationToken);
            }

            return On!(request.Method.Method, path);
        }
    }
}
