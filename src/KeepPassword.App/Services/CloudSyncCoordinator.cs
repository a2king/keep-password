using System.Security.Cryptography;
using Avalonia.Threading;
using KeepPassword.Core.Install;
using KeepPassword.Core.Sync;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Services;

/// <summary>管理云同步：绑定状态、保存后上传、定时轮询。</summary>
public sealed class CloudSyncCoordinator : IDisposable
{
    private readonly object _gate = new();
    private VaultSession? _session;
    private SyncSessionState? _state;
    private VaultSyncService? _service;
    private CancellationTokenSource? _loop;
    private readonly SemaphoreSlim _io = new(1, 1);
    private int _binding;
    private int _pushQueued;
    private bool _requiresRebind;
    private CancellationTokenSource? _pairingWatch;
    private readonly HashSet<string> _seenPairings = [];
    private string? _lastStatus;
    private string? _lastError;

    /// <summary>测试注入同步客户端。生产路径不设置。</summary>
    internal Func<string, VaultSyncService>? ServiceFactory { get; set; }

    /// <summary>已绑定设备检查新设备配对请求的间隔；对方公开公钥前按 <see cref="PairingFastInterval"/> 追问。</summary>
    internal TimeSpan PairingPollInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan PairingFastInterval { get; set; } = TimeSpan.FromSeconds(2);

    public event Action? Changed;

    /// <summary>有新设备请求加入，确认码已就绪（在 UI 线程触发）。接收方负责调用批准或拒绝。</summary>
    public event Action<PairingApproval>? PairingRequested;

    public bool IsBound
    {
        get { lock (_gate) { return _state is not null; } }
    }

    public string? Email
    {
        get { lock (_gate) { return _state?.Email; } }
    }

    /// <summary>云端密文所用加密密钥的编号（.kpkey 文件名中的那串）。未知时为空。</summary>
    public string? CloudKeyId
    {
        get { lock (_gate) { return _state?.CloudKeyId; } }
    }

    /// <summary>同步因云端使用另一把密钥而暂停，等待用户选择对应的 .kpkey。</summary>
    public bool NeedsKeyFile
    {
        get { lock (_gate) { return _state?.SyncBlocked == true && _state.SyncBlockReason == SyncMessages.NeedsKeyReason; } }
    }

    public long VaultVersion
    {
        get { lock (_gate) { return _state?.VaultVersion ?? 0; } }
    }

    public string? StatusMessage
    {
        get { lock (_gate) { return _lastError ?? _lastStatus; } }
    }

    public bool HasError
    {
        get { lock (_gate) { return _lastError is not null; } }
    }

    public bool RequiresRebind
    {
        get { lock (_gate) { return _requiresRebind; } }
    }

    public bool SyncBlocked
    {
        get { lock (_gate) { return _state?.SyncBlocked == true; } }
    }

    public bool CanAdoptKey
    {
        get { lock (_gate) { return _state?.SyncBlocked == true && _state.SyncBlockReason != "upgrade"; } }
    }

    public void Attach(VaultSession session)
    {
        Detach();
        lock (_gate)
        {
            _session = session;
            _state = SyncSessionState.TryLoad(session.VaultPath);
            if (_state is not null)
            {
                _service = OpenService(_state.BaseUrl);
                if (_state.SyncBlocked)
                {
                    _lastError = BlockText(_state);
                    _lastStatus = null;
                }
                else
                {
                    _lastStatus = $"已连接云同步（{_state.Email}）";
                    StartLoopUnlocked();
                }
            }
            else
            {
                _lastStatus = "未开启云同步";
            }
        }

        RaiseChanged();
        StartPairingWatch();
        if (IsBound)
        {
            _ = RefreshAsync();
        }
    }

    public void Detach()
    {
        CancellationTokenSource? loop;
        CancellationTokenSource? watch;
        VaultSyncService? service;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
            watch = _pairingWatch;
            _pairingWatch = null;
            _seenPairings.Clear();
            service = _service;
            _service = null;
            _session = null;
            _state = null;
            _requiresRebind = false;
            _lastStatus = null;
            _lastError = null;
        }

        try { loop?.Cancel(); } catch { /* ignore */ }
        loop?.Dispose();
        try { watch?.Cancel(); } catch { /* ignore */ }
        watch?.Dispose();
        service?.Dispose();
        RaiseChanged();
    }

    public async Task RequestBindCodeAsync(string email, CancellationToken ct = default)
    {
        var trimmed = email.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('@'))
        {
            throw new ArgumentException("请填写官网邮箱。");
        }

        using var service = OpenService(await ResolveBaseUrlAsync(ct));
        await service.RequestBindCodeAsync(trimmed, ct);
        SetStatus(SyncMessages.CodeAccepted);
    }

    /// <summary>用网站账号密码和邮箱验证码绑定本机。1.0.1 起不再需要 .kpbind。</summary>
    public async Task BindAsync(
        string email,
        string password,
        string code,
        CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _binding, 1) == 1)
        {
            throw new InvalidOperationException("正在绑定，请等待当前操作结束。");
        }

        VaultSyncService? service = null;
        try
        {
            VaultSession session;
            lock (_gate)
            {
                session = _session ?? throw new InvalidOperationException("请先解锁保险库。");
                if (session.NeedsLocalKeyUpgrade)
                {
                    throw new InvalidOperationException("请先升级到本地密钥格式，再开启云同步。");
                }

                if (_state is not null && !_requiresRebind)
                {
                    throw new InvalidOperationException("已经绑定。请先断开后再绑定，避免重复创建设备。");
                }
            }

            await _io.WaitAsync(ct);
            try
            {
                StopLoop();
                service = OpenService(await ResolveBaseUrlAsync(ct));
                var request = new DeviceBindRequest
                {
                    Email = email.Trim(),
                    Password = password,
                    Code = code.Trim(),
                    DeviceName = Environment.MachineName,
                    Platform = OperatingSystem.IsWindows() ? "Windows"
                        : OperatingSystem.IsMacOS() ? "macOS"
                        : OperatingSystem.IsLinux() ? "Linux" : "Unknown",
                    ClientVersion = InstallConstants.Version
                };
                var result = await service.CompleteBindAsync(session, request, ct);
                CommitBind(session, service, result);
                service = null;
            }
            finally
            {
                _io.Release();
            }
        }
        catch
        {
            service?.Dispose();
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _binding, 0);
            DrainPush();
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (!await _io.WaitAsync(0, ct))
        {
            return;
        }

        VaultSession session;
        SyncSessionState state;
        VaultSyncService service;
        byte[] dataKey;
        lock (_gate)
        {
            if (_session is null || _state is null || _service is null || _requiresRebind)
            {
                _io.Release();
                return;
            }

            session = _session;
            state = _state;
            service = _service;
            dataKey = session.CopyDataKey();
        }

        try
        {
            SetStatus("正在检查云端更新…");
            await service.RefreshAsync(session, state, dataKey, ct);
            lock (_gate)
            {
                _state = state;
                _lastError = null;
                _lastStatus = $"已与云端同步（版本 {state.VaultVersion}）";
            }

            RaiseChanged();
        }
        catch (Exception ex)
        {
            NoteSyncFailure(ex, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            _io.Release();
            DrainPush();
        }
    }

    public void NotifyLocalChanged()
    {
        if (!IsBound || RequiresRebind || SyncBlocked)
        {
            return;
        }

        Interlocked.Exchange(ref _pushQueued, 1);
        _ = PushAsync();
    }

    public async Task PushAsync(CancellationToken ct = default)
    {
        if (!await _io.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            Interlocked.Exchange(ref _pushQueued, 0);
            VaultSession session;
            SyncSessionState state;
            VaultSyncService service;
            byte[] dataKey;
            lock (_gate)
            {
                if (_session is null || _state is null || _service is null || _requiresRebind || _state.SyncBlocked)
                {
                    if (_state?.SyncBlocked == true)
                    {
                        _lastError = BlockText(_state);
                        _lastStatus = null;
                        RaiseChanged();
                    }

                    return;
                }

                session = _session;
                state = _state;
                service = _service;
                dataKey = session.CopyDataKey();
            }

            try
            {
                await service.PushAsync(session, state, dataKey, ct);
                lock (_gate)
                {
                    if (_requiresRebind)
                    {
                        return;
                    }

                    _state = state;
                    _lastError = null;
                    _lastStatus = $"已上传到云端（版本 {state.VaultVersion}）";
                }

                RaiseChanged();
            }
            catch (Exception ex)
            {
                NoteSyncFailure(ex, ct);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            _io.Release();
            DrainPush();
        }
    }

    private void DrainPush()
    {
        if (Interlocked.Exchange(ref _pushQueued, 0) == 1)
        {
            _ = PushAsync();
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        string? token = null;
        string baseUrl = SyncDefaults.BaseUrl;
        string? vaultPath = null;
        VaultSession? session;
        lock (_gate)
        {
            session = _session;
            if (_state is not null)
            {
                token = _state.DeviceToken;
                baseUrl = _state.BaseUrl;
                vaultPath = _session?.VaultPath;
            }
        }

        if (token is not null)
        {
            try
            {
                using var api = new SyncApiClient(baseUrl);
                await api.UnbindSelfAsync(token, ct);
            }
            catch
            {
                // 本地仍断开
            }
        }

        if (vaultPath is not null)
        {
            SyncSessionState.Delete(vaultPath);
        }

        lock (_gate)
        {
            _loop?.Cancel();
            _loop?.Dispose();
            _loop = null;
            _service?.Dispose();
            _service = null;
            _state = null;
            _lastError = null;
            _lastStatus = "已断开云同步，本地数据保留";
            _session = session;
        }

        RaiseChanged();
    }

    private void CommitBind(VaultSession session, VaultSyncService service, BindResult result)
    {
        lock (_gate)
        {
            _service?.Dispose();
            _service = service;
            _state = result.State;
            _requiresRebind = false;
            result.State.Save(session.VaultPath);
            if (result.NeedsKeyFile)
            {
                _lastStatus = null;
                _lastError = SyncMessages.NeedsCloudKey(result.State.CloudKeyId);
            }
            else if (result.VaultSynced)
            {
                _lastError = null;
                _lastStatus = $"{SyncMessages.EnabledPrefix}（{result.State.Email}）";
            }
            else
            {
                _lastStatus = null;
                _lastError = SyncMessages.VaultPending;
            }

            StartLoopUnlocked();
        }

        RaiseChanged();
        StartPairingWatch();
    }

    private void NoteSyncFailure(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
            return;
        }

        if (ex is SyncKeyChangedException changed)
        {
            MarkKeyBlocked(changed);
            return;
        }

        if (ex is SyncApiException api)
        {
            if (api.Kind == SyncFailureKind.Cancelled)
            {
                return;
            }

            if (api.SessionInvalid)
            {
                MarkSessionExpired();
                return;
            }

            if (SyncBlocked)
            {
                SetError(BlockText(CurrentState()));
                return;
            }

            SetError(api.Message);
            return;
        }

        if (ex is CredentialRejectedException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            SetError(ex.Message);
            return;
        }

        SetError("云同步失败，请稍后重试。本地数据仍可使用。");
    }

    public async Task<KeyChangeResult> RotateKeyAsync(string keyFileText, bool confirmConflicts, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct);
        try
        {
            var (session, state, service) = RequireBound();
            var result = await service.RotateKeyAsync(session, state, keyFileText, confirmConflicts, ct);
            NoteKeyResult(state, result, "已更换云端密钥");
            return result;
        }
        finally
        {
            _io.Release();
        }
    }

    public async Task<KeyChangeResult> AdoptKeyFileAsync(string keyFileText, bool confirmConflicts, CancellationToken ct = default,
        IReadOnlyDictionary<Guid, MergeSide>? choices = null)
    {
        await _io.WaitAsync(ct);
        try
        {
            var (session, state, service) = RequireBound();
            var waitingForKey = state.SyncBlockReason == SyncMessages.NeedsKeyReason;
            var result = await service.AdoptKeyFileAsync(session, state, keyFileText, confirmConflicts, ct, choices);
            NoteKeyResult(state, result, waitingForKey ? "已用密钥文件恢复同步。" + SyncMessages.AdoptedHint : "已用新密钥恢复同步");
            return result;
        }
        finally
        {
            _io.Release();
        }
    }

    // ---------------- 设备配对 ----------------

    /// <summary>新设备：向其他已绑定设备请求数据密钥。调用方负责轮询、取消和释放。</summary>
    public async Task<PairingRequest> StartPairingAsync(CancellationToken ct = default)
    {
        var (_, state, service) = RequireBound();
        return await PairingRequest.StartAsync(service.Api, state.DeviceToken, ct);
    }

    /// <summary>新设备：用配对收到的数据密钥恢复同步。校验、合并和冲突确认与选择密钥文件相同。</summary>
    public async Task<KeyChangeResult> AdoptPairedKeyAsync(byte[] dataKey, bool confirmConflicts,
        IReadOnlyDictionary<Guid, MergeSide>? choices = null, CancellationToken ct = default)
    {
        await _io.WaitAsync(ct);
        try
        {
            var (session, state, service) = RequireBound();
            var result = await service.AdoptPairedKeyAsync(session, state, dataKey, confirmConflicts, choices, ct);
            NoteKeyResult(state, result, "已从其他设备取得密钥并开启同步");
            return result;
        }
        finally
        {
            _io.Release();
        }
    }

    /// <summary>老设备：用户核对确认码一致后批准，把本机数据密钥加密发给新设备。</summary>
    public async Task ApprovePairingAsync(PairingApproval approval, CancellationToken ct = default)
    {
        byte[] dataKey;
        lock (_gate)
        {
            dataKey = (_session ?? throw new InvalidOperationException("请先解锁保险库。")).CopyDataKey();
        }

        try
        {
            await approval.ApproveAsync(dataKey, ct);
            SetStatus($"已批准「{approval.DeviceName}」加入云同步");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
            approval.Dispose();
        }
    }

    public async Task RejectPairingAsync(PairingApproval approval)
    {
        try
        {
            await approval.RejectAsync();
        }
        finally
        {
            approval.Dispose();
        }
    }

    private void StartPairingWatch()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_pairingWatch is not null || _session is null)
            {
                return;
            }

            cts = new CancellationTokenSource();
            _pairingWatch = cts;
        }

        _ = Task.Run(() => PairingWatchAsync(cts.Token));
    }

    private async Task PairingWatchAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await CheckPairingsAsync(ct);
                await Task.Delay(PairingPollInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is SyncApiException or HttpRequestException or InvalidOperationException or ObjectDisposedException)
            {
                // 网络或令牌问题由正常同步报告；配对检查静默重试。
                try { await Task.Delay(PairingPollInterval, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>
    /// 老设备检查待批准的配对请求，自动响应并在确认码就绪后通知界面。
    /// 只有本机密钥就是云端密钥时才响应，避免把一把过期的密钥交给新设备。
    /// </summary>
    internal async Task CheckPairingsAsync(CancellationToken ct)
    {
        VaultSyncService service;
        string token;
        lock (_gate)
        {
            if (_session is not { IsUnlocked: true } session || _state is not { } state || _service is null
                || _requiresRebind || state.SyncBlocked)
            {
                return;
            }

            if (!string.IsNullOrEmpty(state.CloudKeyId)
                && !string.Equals(state.CloudKeyId, session.EncryptionKeyId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            service = _service;
            token = state.DeviceToken;
        }

        var pending = await service.Api.GetPendingPairingsAsync(token, ct);
        foreach (var request in pending.Where(request => request.Status == "requested"))
        {
            lock (_gate)
            {
                if (!_seenPairings.Add(request.PairingId)) continue;
            }

            PairingApproval approval;
            try
            {
                approval = await PairingApproval.OfferAsync(service.Api, token, request, ct);
            }
            catch (SyncApiException)
            {
                continue; // 其他设备已经响应，或请求已结束。
            }

            _ = WaitForPairingCodeAsync(approval, request.ExpiresAt, ct);
        }
    }

    private async Task WaitForPairingCodeAsync(PairingApproval approval, DateTimeOffset? expiresAt, CancellationToken ct)
    {
        var until = expiresAt ?? DateTimeOffset.UtcNow.AddMinutes(10);
        try
        {
            while (DateTimeOffset.UtcNow < until)
            {
                if (await approval.PollAsync(ct))
                {
                    if (Dispatcher.UIThread.CheckAccess()) RaisePairing(approval);
                    else Dispatcher.UIThread.Post(() => RaisePairing(approval));
                    return;
                }

                await Task.Delay(PairingFastInterval, ct);
            }
        }
        catch (Exception ex) when (ex is SyncApiException or HttpRequestException or OperationCanceledException)
        {
        }

        approval.Dispose();
    }

    private void RaisePairing(PairingApproval approval)
    {
        if (PairingRequested is { } handler)
        {
            handler(approval);
        }
        else
        {
            _ = RejectPairingAsync(approval);
        }
    }

    private (VaultSession Session, SyncSessionState State, VaultSyncService Service) RequireBound()
    {
        lock (_gate)
        {
            if (_session is null || _state is null || _service is null)
            {
                throw new InvalidOperationException("请先开启云同步。");
            }

            return (_session, _state, _service);
        }
    }

    private void NoteKeyResult(SyncSessionState state, KeyChangeResult result, string success)
    {
        if (!result.Committed)
        {
            return;
        }

        lock (_gate)
        {
            _state = state;
            _lastError = null;
            _lastStatus = $"{success}（保险库版本 {state.VaultVersion}）";
            StartLoopUnlocked();
        }

        RaiseChanged();
    }

    private void MarkKeyBlocked(SyncKeyChangedException ex)
    {
        lock (_gate)
        {
            if (_state is not null)
            {
                // 同步服务在抛出前已写好原因（例如等待密钥文件），这里不覆盖。
                if (!_state.SyncBlocked)
                {
                    _state.SyncBlocked = true;
                    _state.SyncBlockReason = VaultSyncService.BlockReason(ex, _state);
                    _state.CloudKeyId = ex.CloudKeyId ?? _state.CloudKeyId;
                }

                if (_session is not null)
                {
                    _state.Save(_session.VaultPath);
                }
            }

            StopLoopUnlocked();
            _lastStatus = null;
            _lastError = _state is null ? ex.Message : BlockText(_state);
        }

        RaiseChanged();
    }

    private SyncSessionState? CurrentState()
    {
        lock (_gate)
        {
            return _state;
        }
    }

    private static string BlockText(SyncSessionState? state) => state?.SyncBlockReason switch
    {
        "upgrade" => SyncMessages.UpgradeRequired,
        SyncMessages.NeedsKeyReason => SyncMessages.NeedsCloudKey(state.CloudKeyId),
        _ => SyncMessages.KeyChanged
    };

    private void MarkSessionExpired()
    {
        lock (_gate)
        {
            _requiresRebind = true;
            StopLoopUnlocked();
            _lastStatus = null;
            _lastError = SyncMessages.SessionExpired;
        }

        RaiseChanged();
    }

    private VaultSyncService OpenService(string url) =>
        ServiceFactory?.Invoke(url) ?? new VaultSyncService(url);

    private void StopLoop()
    {
        lock (_gate)
        {
            StopLoopUnlocked();
        }
    }

    private void StopLoopUnlocked()
    {
        var loop = _loop;
        _loop = null;
        try
        {
            loop?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        loop?.Dispose();
    }

    private void StartLoopUnlocked()
    {
        StopLoopUnlocked();
        if (_requiresRebind || _state is null || _state.SyncBlocked)
        {
            return;
        }

        var minutes = Math.Clamp(_state.PollMinutes, 1, 120);
        var cts = new CancellationTokenSource();
        _loop = cts;
        _ = Task.Run(() => PollLoopAsync(minutes, cts.Token));
    }

    private async Task PollLoopAsync(int minutes, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            while (await timer.WaitForNextTickAsync(ct))
            {
                await RefreshAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private string ResolveBaseUrl()
    {
        lock (_gate)
        {
            return _state?.BaseUrl ?? SyncDefaults.BaseUrl;
        }
    }

    private async Task<string> ResolveBaseUrlAsync(CancellationToken ct)
    {
        string? saved;
        lock (_gate)
        {
            saved = string.IsNullOrWhiteSpace(_state?.BaseUrl) ? null : _state.BaseUrl;
        }

        return saved ?? await SyncDefaults.PickBaseUrlAsync(ct);
    }

    private void SetStatus(string message)
    {
        lock (_gate)
        {
            _lastError = null;
            _lastStatus = message;
        }

        RaiseChanged();
    }

    private void SetError(string message)
    {
        lock (_gate)
        {
            _lastError = message;
        }

        RaiseChanged();
    }

    private void RaiseChanged()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Changed?.Invoke();
        }
        else
        {
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }

    public void Dispose()
    {
        Detach();
        _io.Dispose();
    }
}
