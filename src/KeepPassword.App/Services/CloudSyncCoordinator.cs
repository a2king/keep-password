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
    private int _pushing;
    private string? _lastStatus;
    private string? _lastError;

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
        using var api = new SyncApiClient(ResolveBaseUrl());
        await api.RequestDeviceCodeAsync(email.Trim(), ct);
        SetStatus("验证码已发送到邮箱（开发环境见服务器控制台）");
    }

    public async Task BindAsync(
        string email,
        string password,
        string bindingKey,
        string code,
        CancellationToken ct = default)
    {
        VaultSession session;
        lock (_gate)
        {
            session = _session ?? throw new InvalidOperationException("请先解锁保险库。");
            if (session.NeedsLocalKeyUpgrade)
            {
                throw new InvalidOperationException("请先升级到本地密钥格式，再开启云同步。");
            }
        }

        var dataKey = session.CopyDataKey();
        try
        {
            var service = new VaultSyncService(ResolveBaseUrl());
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
            var state = await service.CompleteBindAsync(session.VaultPath, request, password, dataKey, ct);

            if (state.VaultVersion == 0)
            {
                await service.PushAsync(session, state, dataKey, ct);
            }
            else
            {
                await service.PullMergeAndPushAsync(session, state, dataKey, ct);
            }

            lock (_gate)
            {
                _service?.Dispose();
                _service = service;
                _state = state;
                _lastError = null;
                _lastStatus = $"已绑定云同步（{state.Email}）";
                StartLoopUnlocked();
            }

            RaiseChanged();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        VaultSession session;
        SyncSessionState state;
        VaultSyncService service;
        byte[] dataKey;
        lock (_gate)
        {
            if (_session is null || _state is null || _service is null)
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
        catch (Exception ex) when (ex is SyncApiException or CredentialRejectedException or InvalidDataException or IOException or OperationCanceledException)
        {
            if (ex is not OperationCanceledException)
            {
                SetError(ex.Message);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public void NotifyLocalChanged()
    {
        if (!IsBound)
        {
            return;
        }

        _ = PushAsync();
    }

    public async Task PushAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _pushing, 1) == 1)
        {
            return;
        }

        try
        {
            VaultSession session;
            SyncSessionState state;
            VaultSyncService service;
            byte[] dataKey;
            lock (_gate)
            {
                if (_session is null || _state is null || _service is null)
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
                    _state = state;
                    _lastError = null;
                    _lastStatus = $"已上传到云端（版本 {state.VaultVersion}）";
                }

                RaiseChanged();
            }
            catch (Exception ex) when (ex is SyncApiException or CredentialRejectedException or InvalidDataException or IOException)
            {
                SetError(ex.Message);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dataKey);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pushing, 0);
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

    private void StartLoopUnlocked()
    {
        _loop?.Cancel();
        _loop?.Dispose();
        var minutes = Math.Clamp(_state?.PollMinutes ?? 5, 1, 120);
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

    public void Dispose() => Detach();
}
