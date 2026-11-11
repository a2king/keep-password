using System.Security.Cryptography;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Sync;

/// <summary>本地变更上传、刷新拉取与按时间戳合并。</summary>
public sealed class VaultSyncService : IDisposable
{
    private readonly SyncApiClient _api;
    private readonly string _baseUrl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _persistState = true;

    public VaultSyncService(string? baseUrl = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = (baseUrl ?? SyncDefaults.BaseUrl).TrimEnd('/');
        _api = new SyncApiClient(_baseUrl, handler);
    }

    public SyncApiClient Api => _api;

    public async Task RequestBindCodeAsync(string email, CancellationToken ct = default) =>
        await _api.RequestDeviceCodeAsync(email, ct);

    public async Task<BindResult> CompleteBindAsync(
        VaultSession session,
        DeviceBindRequest request,
        string accountPassword,
        CancellationToken ct = default,
        KdfProfile? wrapProfile = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await CompleteBindCoreAsync(session, request, accountPassword, wrapProfile, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task PushAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default) =>
        Exclusive(ct, () => PushCoreAsync(session, state, dataKey, ct));

    public Task RefreshAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default) =>
        Exclusive(ct, () => RefreshCoreAsync(session, state, dataKey, ct));

    public Task PullMergeAndPushAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default) =>
        Exclusive(ct, () => PullMergeCoreAsync(session, state, dataKey, conflictDepth: 0, ct));

    private async Task Exclusive(CancellationToken ct, Func<Task> work)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<BindResult> CompleteBindCoreAsync(
        VaultSession session,
        DeviceBindRequest request,
        string accountPassword,
        KdfProfile? wrapProfile,
        CancellationToken ct)
    {
        request.Code = DeviceBindCode.Normalize(request.Code);
        var localKey = session.CopyDataKey();
        DeviceBindResponse? bound = null;
        KeyPlan? plan = null;
        var persistBefore = _persistState;
        _persistState = false;
        try
        {
            bound = await _api.BindDeviceAsync(request, ct);
            plan = await PrepareKeyAsync(bound, accountPassword, localKey, wrapProfile, ct);
            var state = new SyncSessionState
            {
                Email = request.Email,
                DeviceToken = bound.DeviceToken,
                DeviceId = bound.DeviceId,
                VaultVersion = bound.VaultVersion,
                BindingKeyFingerprint = bound.BindingKeyFingerprint,
                BaseUrl = _baseUrl
            };

            try
            {
                if (plan.AdoptKey is not null)
                {
                    await UploadAdoptedAsync(session, state, plan, ct);
                    return new BindResult(state, vaultSynced: true, adoptedCloudKey: true);
                }

                if (bound.VaultVersion <= 0)
                {
                    await UploadFileCoreAsync(session, state, localKey, conflictDepth: 0, ct);
                }
                else
                {
                    await PullMergeCoreAsync(session, state, localKey, conflictDepth: 0, ct);
                }

                return new BindResult(state, vaultSynced: true, adoptedCloudKey: false);
            }
            catch (Exception ex) when (plan.AdoptKey is null && CanKeepDevice(ex))
            {
                return new BindResult(state, vaultSynced: false, adoptedCloudKey: false);
            }
        }
        catch
        {
            if (bound is not null)
            {
                await TryUnbindAsync(bound.DeviceToken, ct);
            }

            throw;
        }
        finally
        {
            _persistState = persistBefore;
            CryptographicOperations.ZeroMemory(localKey);
            if (plan?.AdoptKey is not null)
            {
                CryptographicOperations.ZeroMemory(plan.AdoptKey);
            }
        }
    }

    private async Task<KeyPlan> PrepareKeyAsync(
        DeviceBindResponse bound,
        string accountPassword,
        byte[] localKey,
        KdfProfile? wrapProfile,
        CancellationToken ct)
    {
        if (bound.HasWrappedKey)
        {
            var blob = await _api.DownloadWrappedKeyAsync(bound.DeviceToken, ct);
            if (blob is null || blob.Length == 0)
            {
                throw new SyncApiException(SyncFailureKind.Validation, "服务器标记已有密钥包，但没有返回内容。已停止，本地数据未改动。");
            }

            byte[] remote;
            try
            {
                remote = AccountKeyWrap.Unwrap(accountPassword, blob, wrapProfile);
            }
            catch (CredentialRejectedException)
            {
                throw new SyncApiException(SyncFailureKind.Validation, "官网密码无法解开云端密钥包，已停止绑定。本地保险库未改动。");
            }

            if (remote.Length == localKey.Length && CryptographicOperations.FixedTimeEquals(remote, localKey))
            {
                CryptographicOperations.ZeroMemory(remote);
                return new KeyPlan();
            }

            if (bound.VaultVersion > 0)
            {
                var download = await _api.DownloadVaultAsync(bound.DeviceToken, ct);
                if (download.Data is null || download.Data.Length == 0)
                {
                    CryptographicOperations.ZeroMemory(remote);
                    throw new SyncApiException(SyncFailureKind.Validation, "云端保险库为空，但版本号不为 0。已停止，本地数据未改动。");
                }

                try
                {
                    _ = VaultStore.ReadEntriesWithDataKey(download.Data, remote);
                }
                catch (Exception ex) when (ex is CredentialRejectedException or InvalidDataException)
                {
                    CryptographicOperations.ZeroMemory(remote);
                    throw new SyncApiException(SyncFailureKind.Validation, "云端密钥包与云端保险库不匹配，已停止。本地数据未改动。");
                }

                return new KeyPlan
                {
                    AdoptKey = remote,
                    RemoteVault = download.Data,
                    RemoteVersion = download.VaultVersion
                };
            }

            return new KeyPlan { AdoptKey = remote, RemoteVersion = 0 };
        }

        if (bound.VaultVersion > 0 || bound.HasVault)
        {
            var download = await _api.DownloadVaultAsync(bound.DeviceToken, ct);
            if (download.Data is null || download.Data.Length == 0)
            {
                throw new SyncApiException(SyncFailureKind.Validation, "云端记录了保险库，但没有数据。已停止上传，避免覆盖。本地数据未改动。");
            }

            try
            {
                _ = VaultStore.ReadEntriesWithDataKey(download.Data, localKey);
            }
            catch (Exception ex) when (ex is CredentialRejectedException or InvalidDataException)
            {
                throw new SyncApiException(SyncFailureKind.Validation, "云端已有保险库，但本机密钥无法解密。已停止上传，避免覆盖。请导入原来的加密密钥后再绑定。本地数据未改动。");
            }

            await UploadWrapAsync(bound.DeviceToken, accountPassword, localKey, wrapProfile, ct);
            return new KeyPlan();
        }

        await UploadWrapAsync(bound.DeviceToken, accountPassword, localKey, wrapProfile, ct);
        return new KeyPlan();
    }

    private async Task UploadWrapAsync(string deviceToken, string accountPassword, byte[] dataKey, KdfProfile? wrapProfile, CancellationToken ct)
    {
        var wrapped = AccountKeyWrap.Wrap(accountPassword, dataKey, wrapProfile);
        try
        {
            await _api.UploadWrappedKeyAsync(deviceToken, wrapped, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    private async Task UploadAdoptedAsync(VaultSession session, SyncSessionState state, KeyPlan plan, CancellationToken ct)
    {
        var remoteKey = plan.AdoptKey ?? throw new InvalidOperationException("缺少云端数据密钥。");
        IReadOnlyList<VaultEntry> remoteEntries = [];
        if (plan.RemoteVault is { Length: > 0 })
        {
            remoteEntries = VaultStore.ReadEntriesWithDataKey(plan.RemoteVault, remoteKey);
        }

        var merged = VaultMerge.Merge(session.AllRecords, remoteEntries, session.Spaces, session.Tags);
        var image = session.EncryptSnapshot(remoteKey, merged.Entries, merged.Spaces, merged.Tags);
        try
        {
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, plan.RemoteVersion, image, ct);
            session.ReplaceWithDataKey(remoteKey, merged.Entries, merged.Spaces, merged.Tags);
            state.VaultVersion = uploaded.VaultVersion;
            Remember(state, session.VaultPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    private async Task PushCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct) =>
        await UploadFileCoreAsync(session, state, dataKey, conflictDepth: 0, ct);

    private async Task RefreshCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct)
    {
        var status = await _api.GetStatusAsync(state.DeviceToken, ct);
        if (!string.IsNullOrEmpty(status.BindingKeyFingerprint)
            && !string.Equals(status.BindingKeyFingerprint, state.BindingKeyFingerprint, StringComparison.Ordinal))
        {
            state.BindingKeyFingerprint = status.BindingKeyFingerprint;
            Remember(state, session.VaultPath);
        }

        if (status.VaultVersion == 0 && state.VaultVersion == 0)
        {
            await UploadFileCoreAsync(session, state, dataKey, conflictDepth: 0, ct);
            return;
        }

        if (status.VaultVersion <= state.VaultVersion)
        {
            return;
        }

        await PullMergeCoreAsync(session, state, dataKey, conflictDepth: 0, ct);
    }

    private async Task PullMergeCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, int conflictDepth, CancellationToken ct)
    {
        var download = await _api.DownloadVaultAsync(state.DeviceToken, ct);
        if (download.VaultVersion == 0 || download.Data is null)
        {
            await UploadFileCoreAsync(session, state, dataKey, conflictDepth, ct);
            return;
        }

        DateTimeOffset? serverTime = null;
        if (DateTimeOffset.TryParse(download.ServerTime, out var parsed))
        {
            serverTime = parsed;
        }

        var remoteEntries = VaultStore.ReadEntriesWithDataKey(download.Data, dataKey);
        var merged = VaultMerge.Merge(session.AllRecords, remoteEntries, session.Spaces, session.Tags, serverTime: serverTime);
        session.ReplaceAllRecords(merged.Entries, merged.Spaces, merged.Tags);
        session.Save();

        state.VaultVersion = download.VaultVersion;
        await UploadFileCoreAsync(session, state, dataKey, conflictDepth, ct);
    }

    private async Task UploadFileCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, int conflictDepth, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(session.VaultPath, ct);
        try
        {
            await UploadBytesCoreAsync(session, state, dataKey, bytes, conflictDepth, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private async Task UploadBytesCoreAsync(
        VaultSession session,
        SyncSessionState state,
        byte[] dataKey,
        byte[] bytes,
        int conflictDepth,
        CancellationToken ct)
    {
        try
        {
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, state.VaultVersion, bytes, ct);
            state.VaultVersion = uploaded.VaultVersion;
            Remember(state, session.VaultPath);
        }
        catch (SyncConflictException) when (conflictDepth < 1)
        {
            await PullMergeCoreAsync(session, state, dataKey, conflictDepth + 1, ct);
        }
        catch (SyncConflictException)
        {
            throw new SyncApiException(SyncFailureKind.Conflict, "云端仍有更新，请稍后刷新。本地数据已保留。", 409);
        }
    }

    private void Remember(SyncSessionState state, string vaultPath)
    {
        if (_persistState)
        {
            state.Save(vaultPath);
        }
    }

    private async Task TryUnbindAsync(string deviceToken, CancellationToken ct)
    {
        try
        {
            await _api.UnbindSelfAsync(deviceToken, ct);
        }
        catch (Exception ex) when (ex is SyncApiException or HttpRequestException or OperationCanceledException)
        {
        }
    }

    private static bool CanKeepDevice(Exception ex) => ex switch
    {
        SyncApiException api => !api.SessionInvalid && api.Kind is SyncFailureKind.Network
            or SyncFailureKind.Timeout
            or SyncFailureKind.Tls
            or SyncFailureKind.Server
            or SyncFailureKind.Conflict
            or SyncFailureKind.RateLimited,
        IOException => true,
        _ => false
    };

    public void Dispose()
    {
        _api.Dispose();
        _gate.Dispose();
    }

    private sealed class KeyPlan
    {
        public byte[]? AdoptKey { get; init; }
        public byte[]? RemoteVault { get; init; }
        public long RemoteVersion { get; init; }
    }
}
