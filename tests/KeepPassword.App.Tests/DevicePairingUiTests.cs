using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Views;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class DevicePairingUiTests
{
    [AvaloniaFact]
    public async Task BoundDevice_RespondsToRequest_ShowsCode_AndApprovalDeliversItsKey()
    {
        using var fixture = new Fixture();
        var server = new PairingServer();
        using var requesterApi = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(requesterApi, PairingServer.NewToken);

        var coordinator = fixture.BoundCoordinator(server);
        PairingApproval? shown = null;
        coordinator.PairingRequested += approval => shown = approval;
        coordinator.Attach(fixture.Session);

        // 老设备自动响应后，新设备公开公钥，两边算出同一个确认码。
        PairingProgress? onNew = null;
        await WaitUntil(async () =>
        {
            onNew = await request.PollAsync();
            return shown is not null;
        });
        Assert.Equal(onNew!.Code ?? (await request.PollAsync()).Code, shown!.Code);
        Assert.Equal("new-pc", shown.DeviceName);

        await coordinator.ApprovePairingAsync(shown);
        var done = await request.PollAsync();
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.Equal(fixture.Session.CopyDataKey(), done.DataKey);
        coordinator.Dispose();
    }

    [AvaloniaFact]
    public async Task BoundDevice_WithAStaleKey_DoesNotRespond()
    {
        using var fixture = new Fixture();
        var server = new PairingServer();
        using var requesterApi = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(requesterApi, PairingServer.NewToken);
        var coordinator = fixture.BoundCoordinator(server, cloudKeyId: "0123456789AB");
        var raised = false;
        coordinator.PairingRequested += _ => raised = true;
        coordinator.Attach(fixture.Session);
        await Task.Delay(300);
        Assert.False(raised);
        Assert.Equal("requested", server.Status);
        coordinator.Dispose();
    }

    [AvaloniaFact]
    public async Task NewDeviceWindow_WaitsForUserConfirmation_BeforeTakingTheKey()
    {
        using var fixture = new Fixture();
        var server = new PairingServer();
        var coordinator = fixture.BoundCoordinator(server, token: PairingServer.NewToken, blocked: true);
        coordinator.Attach(fixture.Session);
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var completion = PairingDialogs.RequestKeyAsync(owner, coordinator, TimeSpan.FromMilliseconds(20));
        var dialog = await WaitForDialog(owner);

        // 老设备响应并批准（确认码模式）。
        using var approverApi = new SyncApiClient("http://sync.test", server);
        await WaitUntil(() => Task.FromResult(server.Status == "requested"));
        var pending = Assert.Single(await approverApi.GetPendingPairingsAsync(PairingServer.OldToken));
        using var approval = await PairingApproval.OfferAsync(approverApi, PairingServer.OldToken, pending);
        await WaitUntil(() => approval.PollAsync());
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == approval.Code);
        Assert.True(dialog.GetVisualDescendants().OfType<QrView>().Single().Matrix is not null);
        var key = RandomNumberGenerator.GetBytes(32);
        await approval.ApproveAsync(key);
        await WaitUntil(() => Task.FromResult(server.Status == "completed"));
        await Task.Delay(100);
        Assert.False(completion.IsCompleted, "确认码模式必须由用户确认两边一致");

        Click(dialog, "两边一致，继续");
        Assert.Equal(key, await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        owner.Close();
        coordinator.Dispose();
    }

    [AvaloniaFact]
    public async Task NewDeviceWindow_Cancel_CancelsTheRequest()
    {
        using var fixture = new Fixture();
        var server = new PairingServer();
        var coordinator = fixture.BoundCoordinator(server, token: PairingServer.NewToken, blocked: true);
        coordinator.Attach(fixture.Session);
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var completion = PairingDialogs.RequestKeyAsync(owner, coordinator, TimeSpan.FromMilliseconds(20));
        var dialog = await WaitForDialog(owner);
        await WaitUntil(() => Task.FromResult(server.Status == "requested"));
        Click(dialog, "取消");
        Assert.Null(await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("cancelled", server.Status);
        owner.Close();
        coordinator.Dispose();
    }

    [AvaloniaFact]
    public async Task ConflictWindow_DefaultsToNewerSide_AndReturnsEditedChoices()
    {
        var owner = new Window { Width = 800, Height = 600 };
        owner.Show();
        var newerLocal = new MergeConflict(Guid.NewGuid(), "GitHub", "GitHub", VaultItemKind.Login,
            DateTimeOffset.Parse("2026-05-01T00:00:00Z"), DateTimeOffset.Parse("2026-01-01T00:00:00Z"), ["密码/验证码"]);
        var newerRemote = newerLocal with { Id = Guid.NewGuid(), LocalName = "DB", RemoteName = "DB-prod",
            LocalUpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), RemoteUpdatedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z") };
        var completion = PairingDialogs.ChooseConflictsAsync(owner, [newerLocal, newerRemote]);
        var dialog = await WaitForDialog(owner);
        var radios = dialog.GetVisualDescendants().OfType<RadioButton>().ToList();
        Assert.Equal([true, false, false, true], radios.Select(radio => radio.IsChecked == true).ToArray());
        Assert.Contains(dialog.GetVisualDescendants().OfType<TextBlock>(), text => text.Text?.Contains("DB / DB-prod") == true);
        radios[1].IsChecked = true; // 第一条改用云端
        Click(dialog, "按所选合并");
        var choices = await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(MergeSide.Remote, choices![newerLocal.Id]);
        Assert.Equal(MergeSide.Remote, choices[newerRemote.Id]);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task CreateFromCloud_SkipsKeyFileSetup_AndAsksMainWindowToOpenCloudSync()
    {
        using var fixture = new Fixture();
        var path = Path.Combine(fixture.Directory, "fresh.kpvault");
        var view = new UnlockViewModel(fixture.Store, path);
        Assert.True(view.ShowUseCloud);
        view.UseCloudForCreate();
        Assert.True(view.CreateFromCloud);
        Assert.False(view.ShowKeyRecovery);
        Assert.Equal("创建并前往云同步", view.SubmitText);
        view.ShortKey = view.ConfirmShort = "laptop";
        Assert.True(await view.SubmitAsync());
        Assert.True(view.CreateFromCloud);
        // 临时密钥仍算未保存；配对成功换成云端密钥后才算已保存。
        Assert.True(view.Session!.NeedsKeyFileSave);
        view.Session.Dispose();
        view.UseNewKeyForCreate();
        Assert.False(view.CreateFromCloud);
    }

    private static void Click(Window window, string text) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Content as string == text)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task<Window> WaitForDialog(Window owner)
    {
        for (var i = 0; i < 200 && owner.OwnedWindows.Count == 0; i++) await Task.Delay(10);
        return Assert.Single(owner.OwnedWindows);
    }

    private static async Task WaitUntil(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 300; i++)
        {
            if (await condition()) return;
            await Task.Delay(20);
        }

        Assert.Fail("等待超时");
    }

    private sealed class Fixture : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "kp-pair-ui", Guid.NewGuid().ToString("N"));
        public VaultStore Store { get; } = new(KdfProfile.Fast);
        public VaultSession Session { get; }

        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            var path = Path.Combine(Directory, "vault.kpvault");
            Session = Store.CreateLocalSession(path, "short-key");
            Session.SaveEncryptionKeyFile(path + ".kpkey", Session.PendingEncryptionKey);
        }

        public CloudSyncCoordinator BoundCoordinator(PairingServer server, string token = PairingServer.OldToken,
            string? cloudKeyId = null, bool blocked = false)
        {
            new SyncSessionState
            {
                Email = "me@example.com", DeviceToken = token, VaultVersion = 1, KeyVersion = 1,
                CloudKeyId = cloudKeyId ?? Session.EncryptionKeyId, BaseUrl = "http://sync.test",
                SyncBlocked = blocked, SyncBlockReason = blocked ? SyncMessages.NeedsKeyReason : null
            }.Save(Session.VaultPath);
            return new CloudSyncCoordinator
            {
                ServiceFactory = url => new VaultSyncService(url, server),
                PairingPollInterval = TimeSpan.FromMilliseconds(30),
                PairingFastInterval = TimeSpan.FromMilliseconds(20)
            };
        }

        public void Dispose()
        {
            Session.Dispose();
            try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>按服务端规则实现的内存配对服务器（单个会话），其余同步接口给出无变化的应答。</summary>
    private sealed class PairingServer : HttpMessageHandler
    {
        public const string NewToken = "token-new";
        public const string OldToken = "token-old";
        private readonly object _gate = new();
        private string? _commitment, _requesterKey, _approverKey, _envelope;
        public string Status { get; private set; } = "none";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var body = text.Length > 0 ? JsonNode.Parse(text) : null;
            lock (_gate)
            {
                if (path == "/api/sync/status") return Ok(new JsonObject { ["vaultVersion"] = 1, ["keyVersion"] = 1 });
                if (path == "/api/sync/pairing" && request.Method == HttpMethod.Post)
                {
                    _commitment = body!["commitment"]!.GetValue<string>();
                    Status = "requested";
                    return Ok(View(token));
                }

                if (path == "/api/sync/pairing/pending")
                {
                    var list = new JsonArray();
                    if (token == OldToken && Status == "requested") list.Add(View(token));
                    return Ok(new JsonObject { ["requests"] = list });
                }

                if (!path.StartsWith("/api/sync/pairing/", StringComparison.Ordinal))
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"意外"}""", Encoding.UTF8, "application/json") };
                }

                switch (path.Split('/').Last())
                {
                    case "offer" when Status == "requested":
                        _approverKey = body!["publicKey"]!.GetValue<string>(); Status = "offered"; break;
                    case "reveal" when Status is "offered" or "revealed":
                        _requesterKey = body!["publicKey"]!.GetValue<string>(); Status = "revealed"; break;
                    case "approve" when Status is "offered" or "revealed":
                        _envelope = body!["envelope"]!.GetValue<string>(); Status = "approved"; break;
                    case "reject":
                        Status = token == NewToken ? "cancelled" : "rejected"; break;
                    case "complete":
                        Status = "completed"; return Ok(new JsonObject { ["ok"] = true });
                    case "offer" or "reveal" or "approve":
                        return new HttpResponseMessage(HttpStatusCode.Conflict) { Content = new StringContent("""{"error":"配对状态已变化"}""", Encoding.UTF8, "application/json") };
                }

                return Ok(View(token));
            }
        }

        private JsonObject View(string? token) => new()
        {
            ["pairingId"] = "pairUiTest0123456789", ["status"] = Status, ["commitment"] = _commitment,
            ["requesterDeviceName"] = "new-pc", ["requesterPlatform"] = "Windows",
            ["approverDeviceName"] = Status == "requested" ? null : "old-pc",
            ["requesterPublicKey"] = _requesterKey, ["approverPublicKey"] = _approverKey,
            ["envelope"] = token == NewToken && Status == "approved" ? _envelope : null
        };

        private static HttpResponseMessage Ok(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
