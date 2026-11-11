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
    private string? _lastStatus;
    private string? _lastError;

    /// <summary>测试注入同步客户端。生产路径不设置。</summary>
    internal Func<string, VaultSyncService>? ServiceFactory { get; set; }

    public event Action? Changed;

    public bool IsBound
    {
        get { lock (_gate) { return _state is not null; } }
    }

    public string? Email
    {
        get { lock (_gate) { return _state?.Email; } }
    }

    public string? BindingKeyFingerprint
    {
        get { lock (_gate) { return _state?.BindingKeyFingerprint; } }
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

    public void Attach(VaultSession session)
    {
        Detach();
        lock (_gate)
        {
            _session = session;
            _state = SyncSessionState.TryLoad(session.VaultPath);
            if (_state is not null)
            {
                _service = new VaultSyncService(_state.BaseUrl);
                _lastStatus = $"已连接云同步（{_state.Email}）";
                StartLoopUnlocked();
            }
            else
            {
                _lastStatus = "未开启云同步";
            }
        }

        RaiseChanged();
        if (IsBound)
        {
            _ = RefreshAsync();
        }
    }

    public void Detach()
    {
        CancellationTokenSource? loop;
        VaultSyncService? service;
        lock (_gate)
        {
            loop = _loop;
            _loop = null;
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

    public async Task BindAsync(
        string email,
        string password,
        string bindingKey,
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
                    BindingKey = BindingKeyFile.Parse(bindingKey),
                    Code = code.Trim(),
                    DeviceName = Environment.MachineName,
                    Platform = OperatingSystem.IsWindows() ? "Windows"
                        : OperatingSystem.IsMacOS() ? "macOS"
                        : OperatingSystem.IsLinux() ? "Linux" : "Unknown",
                    ClientVersion = InstallConstants.Version
                };
                var result = await service.CompleteBindAsync(session, request, password, ct);
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
        if (!IsBound || RequiresRebind)
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
                if (_session is null || _state is null || _service is null || _requiresRebind)
                {
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
            if (result.VaultSynced)
            {
                _lastError = null;
                _lastStatus = result.AdoptedCloudKey
                    ? $"{SyncMessages.EnabledPrefix}（{result.State.Email}）。{SyncMessages.AdoptedHint}"
                    : $"{SyncMessages.EnabledPrefix}（{result.State.Email}）";
            }
            else
            {
                _lastStatus = null;
                _lastError = SyncMessages.VaultPending;
            }

            StartLoopUnlocked();
        }

        RaiseChanged();
    }

    private void NoteSyncFailure(Exception ex, CancellationToken ct)
    {
        if (ex is OperationCanceledException && ct.IsCancellationRequested)
        {
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
        if (_requiresRebind || _state is null)
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
