using System.Security.Cryptography;

namespace KeepPassword.Core.Sync;

public enum PairingStage
{
    /// <summary>已发出请求，等其他设备响应。</summary>
    Waiting,
    /// <summary>已有设备响应，两边都显示确认码，等用户核对和批准。</summary>
    Confirming,
    /// <summary>已收到并解开数据密钥。</summary>
    Approved,
    Rejected,
    Cancelled,
    Expired
}

public sealed class PairingProgress
{
    public PairingStage Stage { get; init; }
    public string? Code { get; init; }
    public string? ApproverDeviceName { get; init; }

    /// <summary>Stage = Approved 时的数据密钥。调用方用完后清零。</summary>
    public byte[]? DataKey { get; init; }

    /// <summary>扫码批准：信封用二维码里的秘密加密，已证明对方扫过本机二维码，不需要用户再确认。</summary>
    public bool KeyAuthenticated { get; init; }

    public bool IsFinished => Stage is PairingStage.Approved or PairingStage.Rejected or PairingStage.Cancelled or PairingStage.Expired;
}

/// <summary>
/// 新设备一侧：发起配对、在对方响应后公开公钥并显示确认码、收到信封后解出数据密钥。
/// 只公开一次公钥，对方公钥一旦记下就不再接受改变。
/// </summary>
public sealed class PairingRequest : IDisposable
{
    private readonly SyncApiClient _api;
    private readonly string _token;
    private readonly PairingCrypto.KeyPair _keys;
    private readonly byte[] _qrSecret;
    private byte[]? _approverKey;
    private string? _code;
    private string? _approverName;

    private PairingRequest(SyncApiClient api, string token, PairingCrypto.KeyPair keys, byte[] qrSecret, string pairingId)
    {
        _api = api;
        _token = token;
        _keys = keys;
        _qrSecret = qrSecret;
        PairingId = pairingId;
        QrPayload = PairingCrypto.QrPayload(pairingId, keys.PublicKey, qrSecret);
    }

    public string PairingId { get; }

    /// <summary>给手机扫的二维码内容（以后的移动端用）。只含配对编号、临时公钥和一次性秘密。</summary>
    public string QrPayload { get; }

    public static async Task<PairingRequest> StartAsync(SyncApiClient api, string deviceToken, CancellationToken ct = default)
    {
        var keys = PairingCrypto.Generate();
        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            var info = await api.CreatePairingAsync(deviceToken, PairingCrypto.Commitment(keys.PublicKey), ct);
            return new PairingRequest(api, deviceToken, keys, secret, info.PairingId);
        }
        catch
        {
            keys.Dispose();
            CryptographicOperations.ZeroMemory(secret);
            throw;
        }
    }

    public async Task<PairingProgress> PollAsync(CancellationToken ct = default)
    {
        var info = await _api.GetPairingAsync(_token, PairingId, ct);
        switch (info.Status)
        {
            case "rejected":
                return new PairingProgress { Stage = PairingStage.Rejected, ApproverDeviceName = info.ApproverDeviceName };
            case "cancelled":
                return new PairingProgress { Stage = PairingStage.Cancelled };
            case "expired":
            case "completed":
                return new PairingProgress { Stage = PairingStage.Expired };
            case "requested":
                return new PairingProgress { Stage = PairingStage.Waiting };
        }

        // 记下对方公钥后才公开自己的公钥；之后对方公钥不允许再变。
        var approverKey = PairingCrypto.ParsePublicKey(info.ApproverPublicKey);
        if (_approverKey is null)
        {
            _approverKey = approverKey;
            _approverName = info.ApproverDeviceName;
            _code = PairingCrypto.ConfirmationCode(_keys.PublicKey, approverKey);
        }
        else if (!CryptographicOperations.FixedTimeEquals(_approverKey, approverKey))
        {
            throw new SyncApiException(SyncFailureKind.Validation, "配对过程中对方设备发生变化，已停止。请重新发起。");
        }

        if (info.Status == "offered")
        {
            info = await _api.RevealPairingAsync(_token, PairingId, _keys.PublicKeyText, ct);
        }

        if (info.Status != "approved" || string.IsNullOrEmpty(info.Envelope))
        {
            return new PairingProgress { Stage = PairingStage.Confirming, Code = _code, ApproverDeviceName = _approverName };
        }

        var opened = PairingCrypto.Open(_keys, _approverKey, PairingId, info.Envelope, _qrSecret);
        return new PairingProgress
        {
            Stage = PairingStage.Approved, Code = _code, ApproverDeviceName = _approverName,
            DataKey = opened.DataKey, KeyAuthenticated = opened.Authenticated
        };
    }

    public async Task CancelAsync(CancellationToken ct = default)
    {
        try
        {
            await _api.RejectPairingAsync(_token, PairingId, ct);
        }
        catch (SyncApiException)
        {
        }
    }

    public async Task CompleteAsync(CancellationToken ct = default)
    {
        try
        {
            await _api.CompletePairingAsync(_token, PairingId, ct);
        }
        catch (SyncApiException)
        {
            // 服务器会在过期后清掉；本地已经拿到密钥，不影响结果。
        }
    }

    public void Dispose()
    {
        _keys.Dispose();
        CryptographicOperations.ZeroMemory(_qrSecret);
    }
}

/// <summary>
/// 已绑定设备一侧：响应一个配对请求，等对方公开公钥后核对承诺、给出确认码，用户批准后发送加密的数据密钥。
/// </summary>
public sealed class PairingApproval : IDisposable
{
    private readonly SyncApiClient _api;
    private readonly string _token;
    private readonly PairingCrypto.KeyPair _keys;
    private readonly string _commitment;
    private byte[]? _requesterKey;

    private PairingApproval(SyncApiClient api, string token, PairingCrypto.KeyPair keys, PairingInfo request)
    {
        _api = api;
        _token = token;
        _keys = keys;
        _commitment = request.Commitment ?? "";
        PairingId = request.PairingId;
        DeviceName = string.IsNullOrWhiteSpace(request.RequesterDeviceName) ? "未命名设备" : request.RequesterDeviceName!;
        Platform = request.RequesterPlatform ?? "";
    }

    public string PairingId { get; }
    public string DeviceName { get; }
    public string Platform { get; }

    /// <summary>对方公开公钥且核对承诺通过后才有值。</summary>
    public string? Code { get; private set; }

    public static async Task<PairingApproval> OfferAsync(SyncApiClient api, string deviceToken, PairingInfo request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.Commitment))
        {
            throw new SyncApiException(SyncFailureKind.Validation, "配对请求无效。");
        }

        var keys = PairingCrypto.Generate();
        try
        {
            await api.OfferPairingAsync(deviceToken, request.PairingId, keys.PublicKeyText, ct);
            return new PairingApproval(api, deviceToken, keys, request);
        }
        catch
        {
            keys.Dispose();
            throw;
        }
    }

    /// <summary>返回 true 表示确认码已就绪；false 表示对方还没公开公钥。请求已结束时抛出。</summary>
    public async Task<bool> PollAsync(CancellationToken ct = default)
    {
        if (Code is not null) return true;
        var info = await _api.GetPairingAsync(_token, PairingId, ct);
        if (info.Status is not ("offered" or "revealed"))
        {
            throw new SyncApiException(SyncFailureKind.Conflict, "这个配对请求已取消或过期。");
        }

        if (info.Status != "revealed") return false;
        var key = PairingCrypto.ParsePublicKey(info.RequesterPublicKey);
        if (!PairingCrypto.MatchesCommitment(key, _commitment))
        {
            await RejectAsync(ct);
            throw new SyncApiException(SyncFailureKind.Validation, "新设备的公钥与请求不一致，可能被篡改，已拒绝。");
        }

        _requesterKey = key;
        Code = PairingCrypto.ConfirmationCode(key, _keys.PublicKey);
        return true;
    }

    /// <summary>用户核对确认码一致后调用：把数据密钥加密发给新设备。</summary>
    public async Task ApproveAsync(byte[] dataKey, CancellationToken ct = default)
    {
        if (_requesterKey is null || Code is null)
        {
            throw new InvalidOperationException("确认码还没准备好。");
        }

        var envelope = PairingCrypto.Seal(_keys, _requesterKey, PairingId, dataKey);
        await _api.ApprovePairingAsync(_token, PairingId, envelope, ct);
    }

    public async Task RejectAsync(CancellationToken ct = default)
    {
        try
        {
            await _api.RejectPairingAsync(_token, PairingId, ct);
        }
        catch (SyncApiException)
        {
        }
    }

    /// <summary>
    /// 扫码批准（给以后的移动端）：二维码里已有新设备公钥和一次性秘密，不需要再核对确认码。
    /// 扫码本身就是用户确认；调用方应在扫码后让用户再点一次「批准」。
    /// </summary>
    public static async Task ApproveScannedAsync(SyncApiClient api, string deviceToken, string qrPayload, byte[] dataKey, CancellationToken ct = default)
    {
        var (pairingId, requesterKey, secret) = PairingCrypto.ParseQr(qrPayload);
        try
        {
            var info = await api.GetPairingAsync(deviceToken, pairingId, ct);
            if (!PairingCrypto.MatchesCommitment(requesterKey, info.Commitment))
            {
                throw new SyncApiException(SyncFailureKind.Validation, "二维码与配对请求不一致，已停止。");
            }

            using var keys = PairingCrypto.Generate();
            await api.OfferPairingAsync(deviceToken, pairingId, keys.PublicKeyText, ct);
            var envelope = PairingCrypto.Seal(keys, requesterKey, pairingId, dataKey, secret);
            await api.ApprovePairingAsync(deviceToken, pairingId, envelope, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public void Dispose() => _keys.Dispose();
}
