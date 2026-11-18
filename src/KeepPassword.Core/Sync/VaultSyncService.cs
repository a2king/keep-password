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

    /// <summary>
    /// 绑定本机。只用网站账号密码和邮箱验证码换设备令牌；加密只用本机已有的数据密钥。
    /// 本机密钥解不开云端数据时，设备保持绑定、同步暂停，等用户选择对应的 .kpkey（见 <see cref="BindResult.NeedsKeyFile"/>）。
    /// </summary>
    public async Task<BindResult> CompleteBindAsync(
        VaultSession session,
        DeviceBindRequest request,
        CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await CompleteBindCoreAsync(session, request, ct);
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
        CancellationToken ct)
    {
        request.Code = DeviceBindCode.Normalize(request.Code);
        var localKey = session.CopyDataKey();
        DeviceBindResponse? bound = null;
        var persistBefore = _persistState;
        _persistState = false;
        try
        {
            bound = await _api.BindDeviceAsync(request, ct);
            var state = new SyncSessionState
            {
                Email = request.Email,
                DeviceToken = bound.DeviceToken,
                DeviceId = bound.DeviceId,
                VaultVersion = bound.VaultVersion,
                KeyVersion = bound.KeyVersion,
                CloudKeyId = bound.VaultKeyId,
                BaseUrl = _baseUrl
            };

            var plan = await PlanBindAsync(bound, localKey, ct);
            if (plan.NeedsKeyFile)
            {
                return NeedsKeyFile(state);
            }

            try
            {
                if (plan.Claim)
                {
                    try
                    {
                        return await ClaimAsync(session, state, localKey, plan, ct);
                    }
                    catch (SyncApiException ex) when (ex is SyncConflictException or SyncKeyChangedException)
                    {
                        return await AfterLostClaimAsync(session, state, localKey, ct);
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

                return new BindResult(state, vaultSynced: true);
            }
            catch (Exception ex) when (ex is not SyncKeyChangedException && CanKeepDevice(ex))
            {
                return new BindResult(state, vaultSynced: false);
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
        }
    }

    /// <summary>
    /// 决定绑定后怎么处理本地与云端数据。只用本机密钥尝试解密：不需要网站密码，也不从服务器取任何密钥。
    /// </summary>
    private async Task<BindPlan> PlanBindAsync(DeviceBindResponse bound, byte[] localKey, CancellationToken ct)
    {
        if (bound.VaultVersion <= 0 && !bound.HasVault)
        {
            // 云端为空：还没建立密钥代就首次建立，否则直接上传。
            return new BindPlan { Claim = bound.KeyVersion == 0, RemoteVersion = bound.VaultVersion };
        }

        if (!string.IsNullOrEmpty(bound.VaultKeyId))
        {
            if (!string.Equals(bound.VaultKeyId, VaultKeyFile.Id(localKey), StringComparison.OrdinalIgnoreCase))
            {
                return new BindPlan { NeedsKeyFile = true };
            }

            if (bound.KeyVersion > 0)
            {
                // 编号一致：直接合并。合并时仍会用本机密钥解密核对。
                return new BindPlan { RemoteVersion = bound.VaultVersion };
            }
        }

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
            // 解密失败绝不能当成空库上传覆盖。保留设备，等用户选对密钥文件。
            return new BindPlan { NeedsKeyFile = true };
        }

        return bound.KeyVersion == 0
            ? new BindPlan { Claim = true, RemoteVault = download.Data, RemoteVersion = download.VaultVersion }
            : new BindPlan { RemoteVersion = download.VaultVersion };
    }

    private static BindResult NeedsKeyFile(SyncSessionState state)
    {
        state.SyncBlocked = true;
        state.SyncBlockReason = SyncMessages.NeedsKeyReason;
        return new BindResult(state, vaultSynced: false, needsKeyFile: true);
    }

    private async Task<BindResult> ClaimAsync(
        VaultSession session,
        SyncSessionState state,
        byte[] localKey,
        BindPlan plan,
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

        try
        {
            var keyId = VaultKeyFile.Id(localKey);
            var committed = await CommitRetryAsync(state.DeviceToken, "establish", state.KeyVersion, plan.RemoteVersion, keyId, image, ct);
            if (merged is not null)
            {
                session.ReplaceAllRecords(merged.Entries, merged.Spaces, merged.Tags);
                session.Save();
            }

            state.KeyVersion = committed.KeyVersion;
            state.VaultVersion = committed.VaultVersion;
            state.CloudKeyId = keyId;
            return new BindResult(state, vaultSynced: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(image);
        }
    }

    /// <summary>另一台设备抢先建立了云端保险库：按它的结果重新判断，本机密钥对得上就合并，否则等用户选密钥文件。</summary>
    private async Task<BindResult> AfterLostClaimAsync(
        VaultSession session,
        SyncSessionState state,
        byte[] localKey,
        CancellationToken ct)
    {
        var status = await _api.GetStatusAsync(state.DeviceToken, ct);
        if (status.KeyVersion <= 0)
        {
            throw new SyncApiException(SyncFailureKind.Conflict, "云端密钥状态冲突，已停止。本地数据未改动。", 409);
        }

        state.KeyVersion = status.KeyVersion;
        state.VaultVersion = status.VaultVersion;
        state.CloudKeyId = status.VaultKeyId;
        var follow = new DeviceBindResponse
        {
            DeviceToken = state.DeviceToken,
            DeviceId = state.DeviceId,
            VaultVersion = status.VaultVersion,
            VaultKeyId = status.VaultKeyId,
            HasVault = status.VaultVersion > 0,
            KeyVersion = status.KeyVersion,
            ServerTime = status.ServerTime
        };
        var plan = await PlanBindAsync(follow, localKey, ct);
        if (plan.NeedsKeyFile)
        {
            return NeedsKeyFile(state);
        }

        await PullMergeCoreAsync(session, state, localKey, conflictDepth: 0, ct);
        return new BindResult(state, vaultSynced: true);
    }

    private async Task<SyncCommitResponse> CommitRetryAsync(
        string deviceToken,
        string purpose,
        long expectedKeyVersion,
        long expectedVaultVersion,
        string keyId,
        byte[] vault,
        CancellationToken ct)
    {
        try
        {
            return await _api.CommitAsync(deviceToken, purpose, expectedKeyVersion, expectedVaultVersion, keyId, vault, ct);
        }
        catch (SyncApiException ex) when (ex.Kind is SyncFailureKind.Network or SyncFailureKind.Timeout or SyncFailureKind.Server)
        {
            return await _api.CommitAsync(deviceToken, purpose, expectedKeyVersion, expectedVaultVersion, keyId, vault, ct);
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
        if (status.KeyVersion != state.KeyVersion)
        {
            state.CloudKeyId = status.VaultKeyId;
            Block(state, "key", session.VaultPath);
            throw new SyncKeyChangedException(status.KeyVersion, upgradeRequired: false, status.VaultKeyId);
        }

        if (state.SyncBlocked)
        {
            // 已暂停（等待密钥文件或需要升级）：保持原因不变，不上传。
            throw new SyncKeyChangedException(state.KeyVersion, state.SyncBlockReason == "upgrade", state.CloudKeyId);
        }

        if (!string.IsNullOrEmpty(status.VaultKeyId)
            && !string.Equals(status.VaultKeyId, state.CloudKeyId, StringComparison.OrdinalIgnoreCase))
        {
            state.CloudKeyId = status.VaultKeyId;
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
        var keyId = VaultKeyFile.Id(dataKey);
        try
        {
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, state.VaultVersion, state.KeyVersion, keyId, bytes, ct);
            state.VaultVersion = uploaded.VaultVersion;
            state.CloudKeyId = keyId;
            Remember(state, session.VaultPath);
        }
        catch (SyncKeyChangedException ex)
        {
            state.CloudKeyId = ex.CloudKeyId ?? state.CloudKeyId;
            Block(state, BlockReason(ex, state), session.VaultPath);
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

    /// <summary>
    /// 服务器拒绝上传时的暂停原因。同一密钥代但编号不同，说明本机用的是另一把密钥，等用户选密钥文件；
    /// 密钥代变了，说明有设备换了钥。
    /// </summary>
    public static string BlockReason(SyncKeyChangedException ex, SyncSessionState state) =>
        ex.UpgradeRequired ? "upgrade"
        : ex.CloudKeyId is not null && ex.KeyVersion == state.KeyVersion ? SyncMessages.NeedsKeyReason
        : "key";

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

    /// <summary>用一份新的 .kpkey 给云端换钥。只上传用新密钥加密的密文和新密钥编号。</summary>
    public Task<KeyChangeResult> RotateKeyAsync(
        VaultSession session,
        SyncSessionState state,
        string keyFileText,
        bool confirmConflicts,
        CancellationToken ct = default) =>
        ExclusiveResult(ct, () => RotateCoreAsync(session, state, keyFileText, confirmConflicts, ct));

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
        bool confirmConflicts,
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
            var nextId = VaultKeyFile.Id(nextKey);
            try
            {
                var committed = await CommitRetryAsync(
                    state.DeviceToken, "rotate", state.KeyVersion, download.VaultVersion > 0 ? download.VaultVersion : state.VaultVersion,
                    nextId, image, ct);
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
                state.CloudKeyId = nextId;
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
        var nextId = VaultKeyFile.Id(nextKey);
        try
        {
            if (!string.IsNullOrEmpty(state.CloudKeyId) && !string.Equals(state.CloudKeyId, nextId, StringComparison.OrdinalIgnoreCase))
            {
                throw new SyncApiException(SyncFailureKind.Validation, SyncMessages.WrongKeyFile(nextId, state.CloudKeyId));
            }

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
                throw new SyncApiException(SyncFailureKind.Validation, SyncMessages.WrongKeyFile(nextId, state.CloudKeyId));
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
            state.CloudKeyId = nextId;
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

    private sealed class BindPlan
    {
        /// <summary>本机密钥解不开云端数据，需要用户选择对应的 .kpkey。</summary>
        public bool NeedsKeyFile { get; init; }

        /// <summary>云端还没有密钥代：由本机建立（establish）。</summary>
        public bool Claim { get; init; }

        public byte[]? RemoteVault { get; init; }
        public long RemoteVersion { get; init; }
    }
}
