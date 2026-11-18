using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class DevicePairingTests
{
    private const string NewToken = "token-new";
    private const string OldToken = "token-old";

    [Fact]
    public void Crypto_CodeMatchesOnBothSides_AndEnvelopeOpensOnlyWithRightKeys()
    {
        using var requester = PairingCrypto.Generate();
        using var approver = PairingCrypto.Generate();
        Assert.Matches("^[0-9]{3} [0-9]{3}$", PairingCrypto.ConfirmationCode(requester.PublicKey, approver.PublicKey));
        Assert.True(PairingCrypto.MatchesCommitment(requester.PublicKey, PairingCrypto.Commitment(requester.PublicKey)));
        Assert.False(PairingCrypto.MatchesCommitment(approver.PublicKey, PairingCrypto.Commitment(requester.PublicKey)));

        var dataKey = RandomNumberGenerator.GetBytes(32);
        var secret = RandomNumberGenerator.GetBytes(32);
        var envelope = PairingCrypto.Seal(approver, requester.PublicKey, "pair-1", dataKey);
        Assert.DoesNotContain(Convert.ToBase64String(dataKey), envelope);
        var opened = PairingCrypto.Open(requester, approver.PublicKey, "pair-1", envelope, secret);
        Assert.Equal(dataKey, opened.DataKey);
        Assert.False(opened.Authenticated);

        // 其他配对编号、别的设备、把模式改成扫码，都解不开。
        Assert.Throws<SyncApiException>(() => PairingCrypto.Open(requester, approver.PublicKey, "pair-2", envelope, secret));
        using var stranger = PairingCrypto.Generate();
        Assert.Throws<SyncApiException>(() => PairingCrypto.Open(stranger, approver.PublicKey, "pair-1", envelope, secret));
        var upgraded = envelope.Replace("\"code\"", "\"qr\"");
        Assert.Throws<SyncApiException>(() => PairingCrypto.Open(requester, approver.PublicKey, "pair-1", upgraded, secret));

        // 扫码模式：只有知道二维码秘密才能做出解得开的信封，解开即视为已认证；改成确认码模式也解不开。
        var qr = PairingCrypto.Seal(approver, requester.PublicKey, "pair-1", dataKey, secret);
        var fromQr = PairingCrypto.Open(requester, approver.PublicKey, "pair-1", qr, secret);
        Assert.True(fromQr.Authenticated);
        Assert.Equal(dataKey, fromQr.DataKey);
        Assert.Throws<SyncApiException>(() => PairingCrypto.Open(requester, approver.PublicKey, "pair-1", qr, RandomNumberGenerator.GetBytes(32)));
        Assert.Throws<SyncApiException>(() => PairingCrypto.Open(requester, approver.PublicKey, "pair-1", qr.Replace("\"qr\"", "\"code\""), secret));
    }

    [Fact]
    public void QrPayload_RoundTripsAndRendersAsAMatrix()
    {
        using var keys = PairingCrypto.Generate();
        var secret = RandomNumberGenerator.GetBytes(32);
        var payload = PairingCrypto.QrPayload("abcDEF_123-xyz456", keys.PublicKey, secret);
        Assert.StartsWith(PairingCrypto.QrPrefix, payload);
        var (id, key, parsedSecret) = PairingCrypto.ParseQr(payload);
        Assert.Equal("abcDEF_123-xyz456", id);
        Assert.Equal(keys.PublicKey, key);
        Assert.Equal(secret, parsedSecret);
        var matrix = PairingCrypto.QrMatrix(payload);
        Assert.Equal(matrix.GetLength(0), matrix.GetLength(1));
        Assert.InRange(matrix.GetLength(0), 25, 80);
        Assert.Throws<SyncApiException>(() => PairingCrypto.ParseQr("otpauth://totp/x"));
    }

    [Fact]
    public async Task CodeFlow_BothSidesShowSameCode_AndNewDeviceGetsTheDataKey()
    {
        var server = new FakePairingServer();
        using var api = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(api, NewToken);
        Assert.Equal(PairingStage.Waiting, (await request.PollAsync()).Stage);

        var pending = Assert.Single(await api.GetPendingPairingsAsync(OldToken));
        Assert.Equal("new-pc", pending.RequesterDeviceName);
        using var approval = await PairingApproval.OfferAsync(api, OldToken, pending);
        Assert.False(await approval.PollAsync());
        Assert.Null(approval.Code);

        var confirming = await request.PollAsync();
        Assert.Equal(PairingStage.Confirming, confirming.Stage);
        Assert.Equal("old-pc", confirming.ApproverDeviceName);
        Assert.True(await approval.PollAsync());
        Assert.Equal(confirming.Code, approval.Code);

        var dataKey = RandomNumberGenerator.GetBytes(32);
        await approval.ApproveAsync(dataKey);
        var done = await request.PollAsync();
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.False(done.KeyAuthenticated);
        Assert.Equal(dataKey, done.DataKey);
        await request.CompleteAsync();
        Assert.Equal("completed", server.Status);

        // 服务器经手的所有内容都不含数据密钥。
        var raw = Convert.ToBase64String(dataKey);
        Assert.DoesNotContain(server.Bodies, body => body.Contains(raw));
    }

    [Fact]
    public async Task ServerSwappingRequesterKey_IsRejectedByCommitment()
    {
        // 恶意服务器给两边各塞自己的公钥：两边确认码不同，用户核对时会发现。
        var server = new FakePairingServer { Mitm = true };
        using var api = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(api, NewToken);
        var pending = Assert.Single(await api.GetPendingPairingsAsync(OldToken));
        using var approval = await PairingApproval.OfferAsync(api, OldToken, pending);
        var onNew = await request.PollAsync();
        // 服务器伪造的“新设备公钥”过不了承诺校验，老设备直接拒绝，不会给出确认码。
        var error = await Assert.ThrowsAsync<SyncApiException>(() => approval.PollAsync());
        Assert.Contains("不一致", error.Message);
        Assert.Null(approval.Code);
        Assert.NotNull(onNew.Code);
    }

    [Fact]
    public async Task ApproverKeyChangingMidway_StopsTheRequester()
    {
        var server = new FakePairingServer();
        using var api = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(api, NewToken);
        var pending = Assert.Single(await api.GetPendingPairingsAsync(OldToken));
        using var approval = await PairingApproval.OfferAsync(api, OldToken, pending);
        await request.PollAsync();
        server.ApproverKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await Assert.ThrowsAsync<SyncApiException>(() => request.PollAsync());
    }

    [Fact]
    public async Task ScanFlow_ApprovesWithoutCode_AndRequesterTrustsIt()
    {
        var server = new FakePairingServer();
        using var api = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(api, NewToken);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        await PairingApproval.ApproveScannedAsync(api, OldToken, request.QrPayload, dataKey);
        var done = await request.PollAsync();
        Assert.Equal(PairingStage.Approved, done.Stage);
        Assert.True(done.KeyAuthenticated);
        Assert.Equal(dataKey, done.DataKey);
    }

    [Fact]
    public async Task RejectedRequest_EndsOnTheNewDevice()
    {
        var server = new FakePairingServer();
        using var api = new SyncApiClient("http://sync.test", server);
        using var request = await PairingRequest.StartAsync(api, NewToken);
        var pending = Assert.Single(await api.GetPendingPairingsAsync(OldToken));
        using var approval = await PairingApproval.OfferAsync(api, OldToken, pending);
        await approval.RejectAsync();
        var result = await request.PollAsync();
        Assert.Equal(PairingStage.Rejected, result.Stage);
        Assert.True(result.IsFinished);
    }

    [Fact]
    public void Merge_HonoursPerEntryChoices_AndDescribesConflictsWithoutSecrets()
    {
        var id = Guid.NewGuid();
        var local = new VaultEntry { Id = id, Name = "GitHub", UpdatedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            Accounts = [new VaultAccount { Username = "me", Password = "local-secret" }] };
        var remote = new VaultEntry { Id = id, Name = "GitHub", UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
            Accounts = [new VaultAccount { Username = "me", Password = "remote-secret" }] };
        var conflict = Assert.Single(VaultMerge.DescribeConflicts([local], [remote]));
        Assert.Equal(MergeSide.Local, conflict.Suggested);
        Assert.Equal(["密码/验证码"], conflict.Differences);
        Assert.DoesNotContain(conflict.Differences, text => text.Contains("secret"));

        Assert.Equal("local-secret", VaultMerge.Merge([local], [remote]).Entries.Single().Accounts[0].Password);
        var chosen = VaultMerge.Merge([local], [remote], choices: new Dictionary<Guid, MergeSide> { [id] = MergeSide.Remote });
        Assert.Equal("remote-secret", chosen.Entries.Single().Accounts[0].Password);
    }

    [Fact]
    public async Task AdoptPairedKey_ReturnsConflictList_ThenAppliesChoices()
    {
        using var session = TestVault.Store().CreateLocalSession(TestVault.NewPath(), "short-key", awaitCloudKey: true);
        Assert.True(session.NeedsKeyFileSave);
        var id = Guid.NewGuid();
        session.Upsert(new VaultEntry { Id = id, Name = "shared", UpdatedAt = DateTimeOffset.Parse("2026-05-01T00:00:00Z"),
            Accounts = [new VaultAccount { Username = "u", Password = "local" }] });
        session.Upsert(new VaultEntry { Name = "only-local", Accounts = [new VaultAccount { Username = "u", Password = "p" }] });
        session.Save();
        var cloudKey = RandomNumberGenerator.GetBytes(32);
        var cloudVault = session.EncryptSnapshot(cloudKey,
        [
            new VaultEntry { Id = id, Name = "shared", UpdatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00Z"), Accounts = [new VaultAccount { Username = "u", Password = "cloud" }] },
            new VaultEntry { Name = "only-cloud", Accounts = [new VaultAccount { Username = "u", Password = "p" }] }
        ], session.Spaces, session.Tags);
        var handler = new Json(new Dictionary<string, Func<string>>
        {
            ["GET /api/sync/vault"] = () => JsonSerializer.Serialize(new { vaultVersion = 3, dataBase64 = Convert.ToBase64String(cloudVault) }),
            ["GET /api/sync/status"] = () => """{"vaultVersion":3,"keyVersion":1}""",
            ["PUT /api/sync/vault"] = () => """{"ok":true,"vaultVersion":4}"""
        });
        using var service = new VaultSyncService("http://sync.test", handler);
        var state = new SyncSessionState { DeviceToken = "t", VaultVersion = 3, KeyVersion = 1, SyncBlocked = true,
            SyncBlockReason = SyncMessages.NeedsKeyReason, CloudKeyId = VaultKeyFile.Id(cloudKey), BaseUrl = "http://sync.test" };

        var preview = await service.AdoptPairedKeyAsync(session, state, cloudKey, confirmConflicts: false);
        Assert.True(preview.NeedsConfirmation);
        var conflict = Assert.Single(preview.Conflicts);
        Assert.Equal(id, conflict.Id);
        Assert.True(state.SyncBlocked);

        var result = await service.AdoptPairedKeyAsync(session, state, cloudKey, confirmConflicts: true,
            new Dictionary<Guid, MergeSide> { [id] = MergeSide.Remote });
        Assert.True(result.Committed);
        Assert.False(state.SyncBlocked);
        Assert.Equal(VaultKeyFile.Id(cloudKey), session.EncryptionKeyId);
        Assert.False(session.NeedsKeyFileSave);
        Assert.Equal("cloud", session.Entries.Single(entry => entry.Id == id).Accounts[0].Password);
        Assert.Equal(["only-cloud", "only-local", "shared"], session.Entries.Select(entry => entry.Name).Order().ToArray());
    }

    [Fact]
    public void CreateAwaitingCloudKey_KeepsTheTemporaryKeyMarkedUnsaved()
    {
        // 临时密钥仍算“未保存”：如果云端其实为空、这把密钥成了正式密钥，用户会继续被提醒保存。
        using var session = TestVault.Store().CreateLocalSession(TestVault.NewPath(), "short-key", awaitCloudKey: true);
        Assert.True(session.NeedsKeyFileSave);
        Assert.Matches("^[0-9A-F]{12}$", session.EncryptionKeyId);
    }

    /// <summary>按服务端规则实现的内存配对服务器；Mitm=true 时给两边各换上自己生成的公钥。</summary>
    private sealed class FakePairingServer : HttpMessageHandler
    {
        private string? _commitment;
        private string? _requesterKey;
        private string? _envelope;
        private readonly string _id = "pairTestId0123456789";
        public string Status { get; private set; } = "none";
        public string? ApproverKey { get; set; }
        public bool Mitm { get; init; }
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var token = request.Headers.Authorization?.Parameter;
            var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Bodies.Add(text);
            var body = text.Length > 0 ? JsonNode.Parse(text) : null;
            var method = request.Method.Method;
            if (method == "POST" && path == "/api/sync/pairing")
            {
                _commitment = body!["commitment"]!.GetValue<string>();
                Status = "requested";
                return Ok(View(token));
            }

            if (path == "/api/sync/pairing/pending")
            {
                return Ok(new JsonObject { ["requests"] = token == OldToken && Status == "requested" ? new JsonArray(View(token)) : new JsonArray() });
            }

            var action = path.Split('/').Last();
            switch (method, action)
            {
                case ("POST", "offer"):
                    ApproverKey = Mitm ? Convert.ToBase64String(PairingCrypto.Generate().PublicKey) : body!["publicKey"]!.GetValue<string>();
                    Status = "offered";
                    break;
                case ("POST", "reveal"):
                    _requesterKey = Mitm ? Convert.ToBase64String(PairingCrypto.Generate().PublicKey) : body!["publicKey"]!.GetValue<string>();
                    Status = "revealed";
                    break;
                case ("POST", "approve"):
                    _envelope = body!["envelope"]!.GetValue<string>();
                    Status = "approved";
                    break;
                case ("POST", "reject"):
                    Status = token == NewToken ? "cancelled" : "rejected";
                    break;
                case ("POST", "complete"):
                    Status = "completed";
                    return Ok(new JsonObject { ["ok"] = true });
            }

            return Ok(View(token));
        }

        private JsonObject View(string? token) => new()
        {
            ["pairingId"] = _id,
            ["status"] = Status,
            ["commitment"] = _commitment,
            ["requesterDeviceName"] = "new-pc",
            ["approverDeviceName"] = Status is "requested" ? null : "old-pc",
            ["requesterPublicKey"] = _requesterKey,
            ["approverPublicKey"] = ApproverKey,
            ["envelope"] = token == NewToken && Status == "approved" ? _envelope : null
        };

        private static HttpResponseMessage Ok(JsonNode node) =>
            new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class Json(Dictionary<string, Func<string>> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var key = request.Method.Method + " " + request.RequestUri!.AbsolutePath;
            var response = routes.TryGetValue(key, out var route)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(route(), Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("""{"error":"意外"}""", Encoding.UTF8, "application/json") };
            return Task.FromResult(response);
        }
    }
}
