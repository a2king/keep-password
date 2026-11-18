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
    public async Task Bind_EmptyCloud_EstablishesWithKeyIdOnly_AndDoesNotSaveSyncFile()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/api/sync/device/bind"))
            {
                return Json(200, BindBody(vaultVersion: 0));
            }

            if (method == "PUT" && path.EndsWith("/api/sync/commit"))
            {
                return Json(200, """{"ok":true,"keyVersion":1,"vaultVersion":1}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request());
        Assert.True(result.VaultSynced);
        Assert.False(result.NeedsKeyFile);
        Assert.Equal(1, result.State.VaultVersion);
        Assert.Equal(1, result.State.KeyVersion);
        Assert.Equal(session.EncryptionKeyId, result.State.CloudKeyId);
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        Assert.Equal(
            ["POST /api/sync/device/bind", "PUT /api/sync/commit"],
            handler.Calls.Select(call => call.Method + " " + call.Path).ToArray());
        Assert.DoesNotContain("bindingKey", handler.Calls[0].Body);
        Assert.Contains("\"clientVersion\":\"1.0.1\"", handler.Calls[0].Body);
        Assert.Contains("\"purpose\":\"establish\"", handler.Calls[1].Body);
        Assert.Equal(session.EncryptionKeyId, ReadField(handler.Calls[1].Body, "keyId"));
        Assert.DoesNotContain("wrappedKey", handler.Calls[1].Body);
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("wrapped-key"));
        AssertNoSecrets(handler, session);
    }

    [Fact]
    public async Task Bind_CloudWithSameKey_MergesWithoutAskingForKeyFile()
    {
        var path = TestVault.NewPath();
        using var session = TestVault.CreateLocalUnlocked(path);
        session.Upsert(Entry("only-local"));
        session.Save();
        var key = session.CopyDataKey();
        var remoteVault = session.EncryptSnapshot(key, [Entry("only-remote")], session.Spaces, session.Tags);
        byte[]? uploaded = null;
        var handler = new Script();
        handler.On = (method, pathName) =>
        {
            if (method == "POST" && pathName.EndsWith("/device/bind")) return Json(200, BindBody(4, vaultKeyId: session.EncryptionKeyId));
            if (method == "GET" && pathName.EndsWith("/vault")) return Json(200, Vault(4, remoteVault));
            if (method == "PUT" && pathName.EndsWith("/vault"))
            {
                uploaded = Convert.FromBase64String(ReadField(handler.Calls[^1].Body, "dataBase64"));
                return Json(200, """{"ok":true,"vaultVersion":5}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request());
        Assert.True(result.VaultSynced);
        Assert.False(result.NeedsKeyFile);
        Assert.Equal(5, result.State.VaultVersion);
        Assert.Equal(session.EncryptionKeyId, ReadField(handler.Calls[^1].Body, "keyId"));
        Assert.Equal(["only-local", "only-remote"], VaultStore.ReadEntriesWithDataKey(uploaded!, key).Select(entry => entry.Name).OrderBy(name => name).ToArray());
        Assert.Equal(["only-local", "only-remote"], session.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bind_CloudWithOtherKey_KeepsDevicePaused_ThenKeyFileMergesBoth(bool serverKnowsKeyId)
    {
        var path = TestVault.NewPath();
        const string shortKey = "short-key";
        byte[] cloudKey;
        string cloudId;
        byte[] cloudVault;
        var cloudPath = TestVault.NewPath();
        using (var cloud = TestVault.CreateLocalUnlocked(cloudPath))
        {
            cloud.Upsert(Entry("only-remote"));
            cloud.Save();
            cloudKey = cloud.CopyDataKey();
            cloudId = cloud.EncryptionKeyId;
            cloudVault = await File.ReadAllBytesAsync(cloudPath);
        }

        using (var session = TestVault.CreateLocalUnlocked(path, shortKey))
        {
            session.Upsert(Entry("only-local"));
            session.Save();
            var before = await File.ReadAllBytesAsync(path);
            byte[]? uploaded = null;
            var handler = new Script();
            handler.On = (method, pathName) =>
            {
                if (method == "POST" && pathName.EndsWith("/device/bind"))
                    return Json(200, BindBody(4, keyVersion: 1, vaultKeyId: serverKnowsKeyId ? cloudId : null));
                if (method == "GET" && pathName.EndsWith("/vault")) return Json(200, Vault(4, cloudVault));
                if (method == "GET" && pathName.EndsWith("/status")) return Json(200, $$"""{"vaultVersion":4,"keyVersion":1,"vaultKeyId":"{{cloudId}}"}""");
                if (method == "PUT" && pathName.EndsWith("/vault"))
                {
                    uploaded = Convert.FromBase64String(ReadField(handler.Calls[^1].Body, "dataBase64"));
                    return Json(200, """{"ok":true,"vaultVersion":5}""");
                }

                return Json(500, """{"error":"不应请求"}""");
            };

            using var service = Service(handler);
            var result = await service.CompleteBindAsync(session, Request());
            Assert.True(result.NeedsKeyFile);
            Assert.False(result.VaultSynced);
            Assert.True(result.State.SyncBlocked);
            Assert.Equal(SyncMessages.NeedsKeyReason, result.State.SyncBlockReason);
            Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
            Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("unbind-self"));
            Assert.Equal(before, await File.ReadAllBytesAsync(path));
            Assert.Equal(serverKnowsKeyId, handler.Calls.All(call => !(call.Method == "GET" && call.Path.EndsWith("/vault"))));
            if (serverKnowsKeyId)
            {
                Assert.Equal(cloudId, result.State.CloudKeyId);
                Assert.Contains(cloudId, SyncMessages.NeedsCloudKey(result.State.CloudKeyId));
            }

            var state = result.State;
            var wrongKey = VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32));
            var wrong = await Assert.ThrowsAsync<SyncApiException>(() => service.AdoptKeyFileAsync(session, state, wrongKey, confirmConflicts: true));
            if (serverKnowsKeyId)
            {
                Assert.Contains(cloudId, wrong.Message);
            }

            Assert.True(state.SyncBlocked);
            Assert.Equal(before, await File.ReadAllBytesAsync(path));

            var adopted = await service.AdoptKeyFileAsync(session, state, VaultKeyFile.Encode(cloudKey), confirmConflicts: true);
            Assert.True(adopted.Committed);
            Assert.False(state.SyncBlocked);
            Assert.Equal(cloudId, state.CloudKeyId);
            Assert.Equal(cloudId, session.EncryptionKeyId);
            Assert.Equal(cloudId, ReadField(handler.Calls[^1].Body, "keyId"));
            Assert.Equal(["only-local", "only-remote"], VaultStore.ReadEntriesWithDataKey(uploaded!, cloudKey).Select(entry => entry.Name).OrderBy(name => name).ToArray());
            AssertNoSecrets(handler, session);
        }

        using var reopened = TestVault.Store().UnlockWithShortKey(path, shortKey);
        Assert.Equal(cloudId, reopened.EncryptionKeyId);
        Assert.Equal(["only-local", "only-remote"], reopened.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
        CryptographicOperations.ZeroMemory(cloudKey);
    }

    [Fact]
    public async Task NewComputer_CreatedFromKeyFile_BindsAndDownloadsWithoutAnyExtraFile()
    {
        // 第一台电脑：本地已有数据，云端为空，绑定后上传。
        var firstPath = TestVault.NewPath();
        byte[]? cloudVault = null;
        string? cloudKeyId = null;
        long cloudVersion = 0;
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
                return Json(200, BindBody(cloudVersion, keyVersion: cloudVersion > 0 ? 1 : 0, vaultKeyId: cloudKeyId));
            if (method == "PUT" && path.EndsWith("/commit"))
            {
                cloudVault = Convert.FromBase64String(ReadField(handler.Calls[^1].Body, "dataBase64"));
                cloudKeyId = ReadField(handler.Calls[^1].Body, "keyId");
                cloudVersion = 1;
                return Json(200, """{"ok":true,"keyVersion":1,"vaultVersion":1}""");
            }

            if (method == "GET" && path.EndsWith("/vault")) return Json(200, Vault(cloudVersion, cloudVault!));
            if (method == "PUT" && path.EndsWith("/vault"))
            {
                cloudVault = Convert.FromBase64String(ReadField(handler.Calls[^1].Body, "dataBase64"));
                Assert.Equal(cloudKeyId, ReadField(handler.Calls[^1].Body, "keyId"));
                cloudVersion++;
                return Json(200, $$"""{"ok":true,"vaultVersion":{{cloudVersion}}}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        string keyText;
        using (var first = TestVault.CreateLocalUnlocked(firstPath))
        {
            first.Upsert(Entry("github"));
            first.Save();
            keyText = VaultKeyFile.Read(firstPath + ".kpkey");
            using var service = Service(handler);
            Assert.True((await service.CompleteBindAsync(first, Request())).VaultSynced);
        }

        // 第二台电脑：用 .kpkey 创建保险库，再绑定。不需要 .kpbind，也不需要再选密钥文件。
        var secondPath = TestVault.NewPath();
        using var second = TestVault.Store().CreateLocalSession(secondPath, "laptop-short", encryptionKey: keyText);
        using (var service = Service(handler))
        {
            var result = await service.CompleteBindAsync(second, Request());
            Assert.True(result.VaultSynced);
            Assert.False(result.NeedsKeyFile);
        }

        Assert.Equal("github", Assert.Single(second.Entries).Name);
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("wrapped-key") || call.Body.Contains("bindingKey"));
    }

    [Fact]
    public async Task Bind_LegacyCloudWithoutKeyGeneration_ClaimsAndMergesWithLocalKey()
    {
        using var session = Local("only-local");
        var key = session.CopyDataKey();
        var remoteVault = session.EncryptSnapshot(key, [Entry("from-0.8")], session.Spaces, session.Tags);
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind")) return Json(200, BindBody(3, keyVersion: 0));
            if (method == "GET" && path.EndsWith("/vault")) return Json(200, Vault(3, remoteVault));
            if (method == "PUT" && path.EndsWith("/commit")) return Json(200, """{"ok":true,"keyVersion":1,"vaultVersion":4}""");
            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request());
        Assert.True(result.VaultSynced);
        var commit = handler.Calls.Single(call => call.Path.EndsWith("/commit"));
        Assert.Equal("establish", ReadField(commit.Body, "purpose"));
        Assert.Equal(session.EncryptionKeyId, ReadField(commit.Body, "keyId"));
        Assert.Equal(["from-0.8", "only-local"], session.Entries.Select(entry => entry.Name).OrderBy(name => name).ToArray());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task Bind_LostEstablishRace_ToOtherKey_PausesForKeyFile()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind")) return Json(200, BindBody(0));
            if (method == "PUT" && path.EndsWith("/commit")) return Json(409, """{"error":"密钥版本已变化，已拒绝写入。","conflict":true,"keyChanged":true,"keyVersion":1,"vaultVersion":1,"vaultKeyId":"0123456789AB"}""");
            if (method == "GET" && path.EndsWith("/status")) return Json(200, """{"vaultVersion":1,"keyVersion":1,"vaultKeyId":"0123456789AB"}""");
            return Json(500, """{"error":"不应请求"}""");
        };

        using var service = Service(handler);
        var result = await service.CompleteBindAsync(session, Request());
        Assert.True(result.NeedsKeyFile);
        Assert.Equal("0123456789AB", result.State.CloudKeyId);
        Assert.Equal(1, result.State.KeyVersion);
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("unbind-self"));
    }

    [Fact]
    public async Task Bind_EstablishRejected_UnbindsAndDoesNotSave()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) =>
        {
            if (method == "POST" && path.EndsWith("/device/bind"))
            {
                return Json(200, BindBody(0));
            }

            if (method == "PUT" && path.EndsWith("/commit"))
            {
                return Json(400, """{"error":"保险库数据无效"}""");
            }

            if (method == "POST" && path.EndsWith("/unbind-self"))
            {
                return Json(200, """{"ok":true}""");
            }

            return Json(500, """{"error":"意外"}""");
        };

        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => service.CompleteBindAsync(session, Request()));
        Assert.Equal(400, error.StatusCode);
        Assert.Contains("保险库数据无效", error.Message);
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
                return Json(200, BindBody(0));
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
        var result = await service.CompleteBindAsync(session, Request());
        Assert.False(result.VaultSynced);
        Assert.False(result.NeedsKeyFile);
        Assert.False(File.Exists(session.VaultPath + ".sync"));
        Assert.DoesNotContain(handler.Calls, call => call.Path.Contains("unbind-self"));
        Assert.Equal("only-local", Assert.Single(session.Entries).Name);
    }

    [Fact]
    public async Task Bind_OldServerOrOldClientMessage_IsShownAsIs()
    {
        var handler = new Script();
        handler.On = (_, _) => Json(400, """{"error":"请把客户端升级到 1.0.1 或更高版本后再开启云同步。","upgradeRequired":true}""");
        using var api = new SyncApiClient("http://sync.test", handler);
        var error = await Assert.ThrowsAsync<SyncApiException>(() => api.BindDeviceAsync(Request()));
        Assert.Equal(400, error.StatusCode);
        Assert.IsNotType<SyncKeyChangedException>(error);
        Assert.Contains("1.0.1", error.Message);
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
        await AssertTransport(
            new HttpRequestException(HttpRequestError.NameResolutionError, "dns secret"),
            SyncFailureKind.Network,
            "域名 sync.test 解析失败");

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
            ? Json(200, """{"vaultVersion":4,"keyVersion":2,"vaultKeyId":"0123456789AB","serverTime":"2026-11-11T00:00:00Z"}""")
            : Json(500, """{"error":"不应继续"}""");
        var state = new SyncSessionState { DeviceToken = Token, VaultVersion = 1, KeyVersion = 1, BaseUrl = "http://sync.test" };
        using var service = Service(handler);
        var error = await Assert.ThrowsAsync<SyncKeyChangedException>(() => service.RefreshAsync(session, state, session.CopyDataKey()));
        Assert.Contains("若非本人操作，请修改网站密码", error.Message);
        Assert.Contains("用新的密钥文件恢复同步", error.Message);
        Assert.DoesNotContain(Password, error.Message);
        Assert.True(state.SyncBlocked);
        Assert.Equal("key", state.SyncBlockReason);
        Assert.Equal("0123456789AB", state.CloudKeyId);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        Assert.Equal(before, await File.ReadAllBytesAsync(session.VaultPath));
    }

    [Fact]
    public async Task Refresh_WhileWaitingForKeyFile_KeepsReasonAndDoesNotUpload()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (_, path) => path.EndsWith("/status")
            ? Json(200, """{"vaultVersion":4,"keyVersion":1,"vaultKeyId":"0123456789AB"}""")
            : Json(500, """{"error":"不应继续"}""");
        var state = BoundState();
        state.SyncBlocked = true;
        state.SyncBlockReason = SyncMessages.NeedsKeyReason;
        state.CloudKeyId = "0123456789AB";
        using var service = Service(handler);
        await Assert.ThrowsAsync<SyncKeyChangedException>(() => service.RefreshAsync(session, state, session.CopyDataKey()));
        Assert.Equal(SyncMessages.NeedsKeyReason, state.SyncBlockReason);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
    }

    [Fact]
    public async Task Push_SendsKeyId_AndServerKeyMismatchPausesForKeyFile()
    {
        using var session = Local("only-local");
        var handler = new Script();
        handler.On = (method, path) => method == "PUT" && path.EndsWith("/vault")
            ? Json(409, """{"error":"这台设备使用的加密密钥与云端不同，已拒绝写入。","keyChanged":true,"conflict":true,"keyVersion":1,"vaultVersion":1,"vaultKeyId":"0123456789AB"}""")
            : Json(500, """{"error":"意外"}""");
        var state = BoundState();
        using var service = Service(handler);
        await Assert.ThrowsAsync<SyncKeyChangedException>(() => service.PushAsync(session, state, session.CopyDataKey()));
        Assert.Equal(session.EncryptionKeyId, ReadField(handler.Calls.Single().Body, "keyId"));
        Assert.True(state.SyncBlocked);
        Assert.Equal(SyncMessages.NeedsKeyReason, state.SyncBlockReason);
        Assert.Equal("0123456789AB", state.CloudKeyId);
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
            session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), confirmConflicts: true));
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
        var preview = await service.RotateKeyAsync(session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), confirmConflicts: false);
        Assert.True(preview.NeedsConfirmation);
        Assert.False(preview.Committed);
        Assert.DoesNotContain(handler.Calls, call => call.Method == "PUT");
        var error = await Assert.ThrowsAsync<SyncConflictException>(() => service.RotateKeyAsync(
            session, state, VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), confirmConflicts: true));
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
        var next = RandomNumberGenerator.GetBytes(32);
        var result = await service.RotateKeyAsync(session, state, VaultKeyFile.Encode(next), confirmConflicts: true);
        Assert.True(result.Committed);
        Assert.Equal(2, attempts);
        Assert.Equal(2, state.KeyVersion);
        Assert.False(state.SyncBlocked);
        var commit = handler.Calls.Last(call => call.Path.EndsWith("/commit"));
        Assert.Equal("rotate", ReadField(commit.Body, "purpose"));
        Assert.Equal(VaultKeyFile.Id(next), ReadField(commit.Body, "keyId"));
        Assert.DoesNotContain("wrappedKey", commit.Body);
        Assert.Equal(VaultKeyFile.Id(next), state.CloudKeyId);
        Assert.Equal(VaultKeyFile.Id(next), session.EncryptionKeyId);
        CryptographicOperations.ZeroMemory(next);
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
            session, BoundState(), VaultKeyFile.Encode(RandomNumberGenerator.GetBytes(32)), true, cts.Token));
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
            if (method == "GET" && path.EndsWith("/status")) return Json(200, """{"vaultVersion":2,"keyVersion":2}""");
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
        Assert.Contains("sync.test", error.Message);
        Assert.DoesNotContain("secret", error.Message);
    }

    private static void AssertNoSecrets(Script handler, VaultSession session)
    {
        var key = session.CopyDataKey();
        try
        {
            var raw = Convert.ToBase64String(key);
            foreach (var call in handler.Calls.Where(call => call.Path.Contains("vault") || call.Path.Contains("commit")))
            {
                Assert.DoesNotContain(raw, call.Body);
                Assert.DoesNotContain("KP2-", call.Body);
                Assert.DoesNotContain(Password, call.Body);
                Assert.DoesNotContain(Code, call.Body);
                Assert.DoesNotContain("wrappedKey", call.Body);
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
        Code = Code,
        DeviceName = "test",
        Platform = "Linux",
        ClientVersion = "1.0.1"
    };

    private static VaultSyncService Service(Script handler) => new("http://sync.test", handler);

    private static string BindBody(long vaultVersion, long? keyVersion = null, string? vaultKeyId = null)
    {
        return JsonSerializer.Serialize(new
        {
            deviceToken = Token,
            deviceId = 9,
            vaultVersion,
            vaultKeyId,
            hasVault = vaultVersion > 0,
            keyVersion = keyVersion ?? (vaultVersion > 0 ? 1 : 0),
            serverTime = "2026-11-11T00:00:00Z"
        });
    }

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
