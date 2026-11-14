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

            state.KeyVersion = bound.KeyVersion;
            try
            {
                if (plan.AdoptKey is not null)
                {
                    await UploadAdoptedAsync(session, state, plan, ct);
                    return new BindResult(state, vaultSynced: true, adoptedCloudKey: true);
                }

                if (plan.Claim)
                {
                    try
                    {
                        return await ClaimAsync(session, state, bound, accountPassword, localKey, plan, wrapProfile, ct);
                    }
                    catch (SyncApiException ex) when (ex is SyncConflictException or SyncKeyChangedException)
                    {
                        return await AdoptAfterLostClaimAsync(session, state, bound, accountPassword, localKey, wrapProfile, ct);
                    }
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
            catch (Exception ex) when (plan.AdoptKey is null && ex is not SyncKeyChangedException && CanKeepDevice(ex))
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
                return new KeyPlan { KeyVersion = bound.KeyVersion };
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
                    RemoteVersion = download.VaultVersion,
                    KeyVersion = bound.KeyVersion
                };
            }

            return new KeyPlan { AdoptKey = remote, RemoteVersion = 0, KeyVersion = bound.KeyVersion };
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

            return new KeyPlan
            {
                Claim = true,
                RemoteVault = download.Data,
                RemoteVersion = download.VaultVersion,
                KeyVersion = bound.KeyVersion
            };
        }

        return new KeyPlan { Claim = true, RemoteVersion = bound.VaultVersion, KeyVersion = bound.KeyVersion };
    }

    private async Task<BindResult> ClaimAsync(
        VaultSession session,
        SyncSessionState state,
        DeviceBindResponse bound,
        string accountPassword,
        byte[] localKey,
        KeyPlan plan,
        KdfProfile? wrapProfile,
        CancellationToken ct)
    {
        byte[] image;
        VaultMerge.Result? merged = null;
        if (plan.RemoteVault is { Length: > 0 })
        {
            var remoteEntries = VaultStore.ReadEntriesWithDataKey(plan.RemoteVault, localKey);
            merged = VaultMerge.Merge(session.AllRecords, remoteEntries, session.Spaces, session.Tags);
            image = session.EncryptSnapshot(localKey, merged.Entries, merged.Spaces, merged.Tags);
        }
        else
        {
            image = await File.ReadAllBytesAsync(session.VaultPath, ct);
        }

        var wrapped = AccountKeyWrap.Wrap(accountPassword, localKey, wrapProfile);
        try
        {
            var committed = await CommitRetryAsync(bound.DeviceToken, "establish", plan.KeyVersion, plan.RemoteVersion, image, wrapped, ct);
            if (merged is not null)
            {
                session.ReplaceAllRecords(merged.Entries, merged.Spaces, merged.Tags);
                session.Save();
            }

            state.KeyVersion = committed.KeyVersion;
            state.VaultVersion = committed.VaultVersion;
            return new BindResult(state, vaultSynced: true, adoptedCloudKey: false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
            CryptographicOperations.ZeroMemory(wrapped);
        }
    }

    private async Task<BindResult> AdoptAfterLostClaimAsync(
        VaultSession session,
        SyncSessionState state,
        DeviceBindResponse bound,
        string accountPassword,
        byte[] localKey,
        KdfProfile? wrapProfile,
        CancellationToken ct)
    {
        var status = await _api.GetStatusAsync(bound.DeviceToken, ct);
        if (!status.HasWrappedKey)
        {
            throw new SyncApiException(SyncFailureKind.Conflict, "云端密钥状态冲突，已停止。本地数据未改动。", 409);
        }

        var follow = new DeviceBindResponse
        {
            DeviceToken = bound.DeviceToken,
            DeviceId = bound.DeviceId,
            VaultVersion = status.VaultVersion,
            BindingKeyFingerprint = status.BindingKeyFingerprint ?? bound.BindingKeyFingerprint,
            HasVault = status.VaultVersion > 0,
            HasWrappedKey = true,
            KeyVersion = status.KeyVersion,
            ServerTime = status.ServerTime
        };
        var plan = await PrepareKeyAsync(follow, accountPassword, localKey, wrapProfile, ct);
        state.VaultVersion = status.VaultVersion;
        state.KeyVersion = status.KeyVersion;
        if (plan.AdoptKey is not null)
        {
            await UploadAdoptedAsync(session, state, plan, ct);
            return new BindResult(state, vaultSynced: true, adoptedCloudKey: true);
        }

        await PullMergeCoreAsync(session, state, localKey, conflictDepth: 0, ct);
        return new BindResult(state, vaultSynced: true, adoptedCloudKey: false);
    }

    private async Task<SyncCommitResponse> CommitRetryAsync(
        string deviceToken,
        string purpose,
        long expectedKeyVersion,
        long expectedVaultVersion,
        byte[] vault,
        byte[]? wrapped,
        CancellationToken ct)
    {
        try
        {
            return await _api.CommitAsync(deviceToken, purpose, expectedKeyVersion, expectedVaultVersion, vault, wrapped, ct);
        }
        catch (SyncApiException ex) when (ex.Kind is SyncFailureKind.Network or SyncFailureKind.Timeout or SyncFailureKind.Server)
        {
            return await _api.CommitAsync(deviceToken, purpose, expectedKeyVersion, expectedVaultVersion, vault, wrapped, ct);
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
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, plan.RemoteVersion, state.KeyVersion, image, ct);
            session.ReplaceWithDataKey(remoteKey, merged.Entries, merged.Spaces, merged.Tags);
            state.VaultVersion = uploaded.VaultVersion;
            Remember(state, session.VaultPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    private async Task PushCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct)
    {
        if (state.SyncBlocked)
        {
            throw new SyncKeyChangedException(state.KeyVersion, state.SyncBlockReason == "upgrade");
        }

        await UploadFileCoreAsync(session, state, dataKey, conflictDepth: 0, ct);
    }

    private async Task RefreshCoreAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct)
    {
        var status = await _api.GetStatusAsync(state.DeviceToken, ct);
        if (status.KeyVersion != state.KeyVersion || state.SyncBlocked)
        {
            var upgrade = status.KeyVersion == state.KeyVersion && state.SyncBlockReason == "upgrade";
            Block(state, upgrade ? "upgrade" : "key", session.VaultPath);
            throw new SyncKeyChangedException(status.KeyVersion, upgrade);
        }

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
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, state.VaultVersion, state.KeyVersion, bytes, ct);
            state.VaultVersion = uploaded.VaultVersion;
            Remember(state, session.VaultPath);
        }
        catch (SyncKeyChangedException ex)
        {
            Block(state, ex.UpgradeRequired ? "upgrade" : "key", session.VaultPath);
            throw;
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

    public Task<KeyChangeResult> RotateKeyAsync(
        VaultSession session,
        SyncSessionState state,
        string keyFileText,
        string accountPassword,
        bool confirmConflicts,
        CancellationToken ct = default,
        KdfProfile? wrapProfile = null) =>
        ExclusiveResult(ct, () => RotateCoreAsync(session, state, keyFileText, accountPassword, confirmConflicts, wrapProfile, ct));

    public Task<KeyChangeResult> AdoptKeyFileAsync(
        VaultSession session,
        SyncSessionState state,
        string keyFileText,
        bool confirmConflicts,
        CancellationToken ct = default) =>
        ExclusiveResult(ct, () => AdoptCoreAsync(session, state, keyFileText, confirmConflicts, ct));

    private async Task<T> ExclusiveResult<T>(CancellationToken ct, Func<Task<T>> work)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await work();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<KeyChangeResult> RotateCoreAsync(
        VaultSession session,
        SyncSessionState state,
        string keyFileText,
        string accountPassword,
        bool confirmConflicts,
        KdfProfile? wrapProfile,
        CancellationToken ct)
    {
        if (state.SyncBlocked && state.SyncBlockReason == "upgrade")
        {
            throw new SyncKeyChangedException(state.KeyVersion, upgradeRequired: true);
        }

        var nextKey = VaultKeyFile.Parse(keyFileText);
        var current = session.CopyDataKey();
        try
        {
            if (nextKey.Length == current.Length && CryptographicOperations.FixedTimeEquals(nextKey, current))
            {
                throw new ArgumentException("这个密钥文件就是当前保险库正在使用的数据密钥，不能当作新密钥提交。");
            }

            var download = await _api.DownloadVaultAsync(state.DeviceToken, ct);
            IReadOnlyList<VaultEntry> remoteEntries = [];
            if (download.VaultVersion > 0)
            {
                if (download.Data is null || download.Data.Length == 0)
                {
                    throw new SyncApiException(SyncFailureKind.Validation, "云端记录了保险库，但没有数据。不能换钥，也不能把它当成空库。本地数据未改动。");
                }

                try
                {
                    remoteEntries = VaultStore.ReadEntriesWithDataKey(download.Data, current);
                }
                catch (Exception ex) when (ex is CredentialRejectedException or InvalidDataException)
                {
                    throw new SyncApiException(SyncFailureKind.Validation, SyncMessages.RotateNeedsCurrent);
                }
            }

            var merged = VaultMerge.Merge(session.AllRecords, remoteEntries, session.Spaces, session.Tags);
            var conflicts = VaultMerge.ContentConflicts(session.AllRecords, remoteEntries);
            if (conflicts.Count > 0 && !confirmConflicts)
            {
                return new KeyChangeResult { NeedsConfirmation = true, ConflictCount = conflicts.Count, KeyVersion = state.KeyVersion, VaultVersion = state.VaultVersion };
            }

            var image = session.EncryptSnapshot(nextKey, merged.Entries, merged.Spaces, merged.Tags);
            var wrapped = AccountKeyWrap.Wrap(accountPassword, nextKey, wrapProfile);
            try
            {
                var committed = await CommitRetryAsync(
                    state.DeviceToken, "rotate", state.KeyVersion, download.VaultVersion > 0 ? download.VaultVersion : state.VaultVersion,
                    image, wrapped, ct);
                try
                {
                    session.ReplaceWithDataKey(nextKey, merged.Entries, merged.Spaces, merged.Tags);
                }
                catch
                {
                    Block(state, "key", session.VaultPath);
                    throw new SyncApiException(SyncFailureKind.Server, "云端已换钥，但本机写入失败。同步已暂停，本地旧库已保留。请用刚选择的密钥文件恢复同步。");
                }

                state.KeyVersion = committed.KeyVersion;
                state.VaultVersion = committed.VaultVersion;
                state.SyncBlocked = false;
                state.SyncBlockReason = null;
                Remember(state, session.VaultPath);
                return new KeyChangeResult
                {
                    Committed = true,
                    ConflictCount = conflicts.Count,
                    KeyVersion = committed.KeyVersion,
                    VaultVersion = committed.VaultVersion
                };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(image);
                CryptographicOperations.ZeroMemory(wrapped);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nextKey);
            CryptographicOperations.ZeroMemory(current);
        }
    }

    private async Task<KeyChangeResult> AdoptCoreAsync(
        VaultSession session,
        SyncSessionState state,
        string keyFileText,
        bool confirmConflicts,
        CancellationToken ct)
    {
        var nextKey = VaultKeyFile.Parse(keyFileText);
        try
        {
            var download = await _api.DownloadVaultAsync(state.DeviceToken, ct);
            if (download.VaultVersion <= 0 || download.Data is null || download.Data.Length == 0)
            {
                throw new SyncApiException(SyncFailureKind.Validation, "云端没有可解开的保险库。同步仍暂停，本地数据未改动。");
            }

            IReadOnlyList<VaultEntry> remoteEntries;
            try
            {
                remoteEntries = VaultStore.ReadEntriesWithDataKey(download.Data, nextKey);
            }
            catch (Exception ex) when (ex is CredentialRejectedException or InvalidDataException)
            {
                throw new SyncApiException(SyncFailureKind.Validation, SyncMessages.AdoptRejected);
            }

            var merged = VaultMerge.Merge(session.AllRecords, remoteEntries, session.Spaces, session.Tags);
            var conflicts = VaultMerge.ContentConflicts(session.AllRecords, remoteEntries);
            if (conflicts.Count > 0 && !confirmConflicts)
            {
                return new KeyChangeResult { NeedsConfirmation = true, ConflictCount = conflicts.Count, KeyVersion = state.KeyVersion, VaultVersion = state.VaultVersion };
            }

            var status = await _api.GetStatusAsync(state.DeviceToken, ct);
            session.ReplaceWithDataKey(nextKey, merged.Entries, merged.Spaces, merged.Tags);
            state.KeyVersion = status.KeyVersion;
            state.VaultVersion = download.VaultVersion;
            state.SyncBlocked = false;
            state.SyncBlockReason = null;
            Remember(state, session.VaultPath);
            try
            {
                await UploadFileCoreAsync(session, state, nextKey, conflictDepth: 0, ct);
            }
            catch (SyncApiException)
            {
                throw new SyncApiException(SyncFailureKind.Server, "本机已改用新密钥并保留未同步的本地条目。云端尚未上传成功，请稍后刷新。同步阻断已解除。");
            }

            return new KeyChangeResult
            {
                Committed = true,
                ConflictCount = conflicts.Count,
                KeyVersion = state.KeyVersion,
                VaultVersion = state.VaultVersion
            };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nextKey);
        }
    }

    private void Block(SyncSessionState state, string reason, string vaultPath)
    {
        state.SyncBlocked = true;
        state.SyncBlockReason = reason;
        Remember(state, vaultPath);
    }

    private sealed class KeyPlan
    {
        public byte[]? AdoptKey { get; init; }
        public byte[]? RemoteVault { get; init; }
        public long RemoteVersion { get; init; }
        public long KeyVersion { get; init; }
        public bool Claim { get; init; }
    }
}
