using System.Security.Cryptography;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Sync;

/// <summary>本地变更上传、刷新拉取与按时间戳合并。</summary>
public sealed class VaultSyncService : IDisposable
{
    private readonly SyncApiClient _api;
    private readonly string _baseUrl;

    public VaultSyncService(string? baseUrl = null, HttpMessageHandler? handler = null)
    {
        _baseUrl = (baseUrl ?? SyncDefaults.BaseUrl).TrimEnd('/');
        _api = new SyncApiClient(_baseUrl, handler);
    }

    public SyncApiClient Api => _api;

    public async Task RequestBindCodeAsync(string email, CancellationToken ct = default) =>
        await _api.RequestDeviceCodeAsync(email, ct);

    public async Task<SyncSessionState> CompleteBindAsync(
        string vaultPath,
        DeviceBindRequest request,
        string accountPassword,
        byte[] dataKey,
        CancellationToken ct = default)
    {
        var bound = await _api.BindDeviceAsync(request, ct);
        var wrapped = AccountKeyWrap.Wrap(accountPassword, dataKey);
        try
        {
            await _api.UploadWrappedKeyAsync(bound.DeviceToken, wrapped, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapped);
        }

        var state = new SyncSessionState
        {
            Email = request.Email,
            DeviceToken = bound.DeviceToken,
            DeviceId = bound.DeviceId,
            VaultVersion = bound.VaultVersion,
            BindingKeyFingerprint = bound.BindingKeyFingerprint,
            BaseUrl = _baseUrl
        };
        state.Save(vaultPath);
        return state;
    }

    public async Task PushAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(session.VaultPath, ct);
        try
        {
            try
            {
                var uploaded = await _api.UploadVaultAsync(state.DeviceToken, state.VaultVersion, bytes, ct);
                state.VaultVersion = uploaded.VaultVersion;
                state.Save(session.VaultPath);
            }
            catch (SyncConflictException)
            {
                await PullMergeAndPushAsync(session, state, dataKey, ct);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task RefreshAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default)
    {
        var status = await _api.GetStatusAsync(state.DeviceToken, ct);
        if (!string.IsNullOrEmpty(status.BindingKeyFingerprint)
            && !string.Equals(status.BindingKeyFingerprint, state.BindingKeyFingerprint, StringComparison.Ordinal))
        {
            state.BindingKeyFingerprint = status.BindingKeyFingerprint;
            state.Save(session.VaultPath);
        }

        if (status.VaultVersion <= state.VaultVersion)
        {
            return;
        }

        await PullMergeAndPushAsync(session, state, dataKey, ct);
    }

    public async Task PullMergeAndPushAsync(VaultSession session, SyncSessionState state, byte[] dataKey, CancellationToken ct = default)
    {
        var download = await _api.DownloadVaultAsync(state.DeviceToken, ct);
        if (download.VaultVersion == 0 || download.Data is null)
        {
            await PushAsync(session, state, dataKey, ct);
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
        state.Save(session.VaultPath);

        var bytes = await File.ReadAllBytesAsync(session.VaultPath, ct);
        try
        {
            var uploaded = await _api.UploadVaultAsync(state.DeviceToken, state.VaultVersion, bytes, ct);
            state.VaultVersion = uploaded.VaultVersion;
            state.Save(session.VaultPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose() => _api.Dispose();
}
