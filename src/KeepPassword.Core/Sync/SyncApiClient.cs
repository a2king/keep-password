using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeepPassword.Core.Sync;

/// <summary>与 keep-password-server 同步 API 交互的轻量客户端。</summary>
public sealed class SyncApiClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public SyncApiClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        var root = baseUrl.TrimEnd('/') + "/";
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(root);
        _http.Timeout = TimeSpan.FromSeconds(60);
    }

    public void Dispose() => _http.Dispose();

    public async Task<DeviceBindResponse> BindDeviceAsync(DeviceBindRequest request, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/sync/device/bind", request, JsonOptions, ct);
        return await ReadAsync<DeviceBindResponse>(response, ct);
    }

    public async Task RequestDeviceCodeAsync(string email, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync("api/sync/device/request-code", new { email }, JsonOptions, ct);
        await EnsureSuccess(response, ct);
    }

    public async Task<SyncStatusResponse> GetStatusAsync(string deviceToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync<SyncStatusResponse>(response, ct);
    }

    public async Task<VaultDownloadResponse> DownloadVaultAsync(string deviceToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/vault");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        return await ReadAsync<VaultDownloadResponse>(response, ct);
    }

    public async Task<VaultUploadResponse> UploadVaultAsync(string deviceToken, long baseVersion, byte[] data, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/sync/vault")
        {
            Content = JsonContent.Create(new
            {
                baseVersion,
                dataBase64 = Convert.ToBase64String(data)
            }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            var conflict = await response.Content.ReadFromJsonAsync<VaultConflictResponse>(JsonOptions, ct)
                ?? new VaultConflictResponse();
            throw new SyncConflictException(conflict.VaultVersion, conflict.ServerTime);
        }

        return await ReadAsync<VaultUploadResponse>(response, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var response = await _http.GetAsync("api/health", ct);
        return response.IsSuccessStatusCode;
    }

    public async Task UnbindSelfAsync(string deviceToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/sync/device/unbind-self");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    public async Task UploadWrappedKeyAsync(string deviceToken, byte[] wrapped, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "api/sync/wrapped-key")
        {
            Content = JsonContent.Create(new { dataBase64 = Convert.ToBase64String(wrapped) }, options: JsonOptions)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccess(response, ct);
    }

    public async Task<byte[]?> DownloadWrappedKeyAsync(string deviceToken, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/sync/wrapped-key");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        using var response = await _http.SendAsync(request, ct);
        var body = await ReadAsync<WrappedKeyResponse>(response, ct);
        return string.IsNullOrEmpty(body.DataBase64) ? null : Convert.FromBase64String(body.DataBase64);
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var err = await TryReadError(response, ct);
        throw new SyncApiException(err ?? $"请求失败：{(int)response.StatusCode}");
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var err = await TryReadError(response, ct);
            throw new SyncApiException(err ?? $"请求失败：{(int)response.StatusCode}");
        }

        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return value ?? throw new SyncApiException("服务器返回空响应。");
    }

    private static async Task<string?> TryReadError(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(JsonOptions, ct);
            return body?.Error;
        }
        catch
        {
            return null;
        }
    }

    private sealed class ErrorBody
    {
        public string? Error { get; set; }
    }
}

public class SyncApiException : Exception
{
    public SyncApiException(string message) : base(message) { }
}

public sealed class SyncConflictException : SyncApiException
{
    public SyncConflictException(long serverVersion, string? serverTime)
        : base("云端保险库已有更新，请先合并后再上传。")
    {
        ServerVersion = serverVersion;
        ServerTime = serverTime;
    }

    public long ServerVersion { get; }
    public string? ServerTime { get; }
}

public sealed class DeviceBindRequest
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
    public string BindingKey { get; set; } = "";
    public string Code { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string ClientVersion { get; set; } = "";
}

public sealed class DeviceBindResponse
{
    public string DeviceToken { get; set; } = "";
    public int DeviceId { get; set; }
    public long VaultVersion { get; set; }
    public string? BindingKeyFingerprint { get; set; }
    public bool HasVault { get; set; }
    public bool HasWrappedKey { get; set; }
    public string? ServerTime { get; set; }
}

public sealed class SyncStatusResponse
{
    public long VaultVersion { get; set; }
    public long VaultSizeBytes { get; set; }
    public string? VaultSha256 { get; set; }
    public string? BindingKeyFingerprint { get; set; }
    public string? BindingKeyCreatedAt { get; set; }
    public bool HasWrappedKey { get; set; }
    public string? ServerTime { get; set; }
}

public sealed class VaultDownloadResponse
{
    public long VaultVersion { get; set; }
    public string? VaultSha256 { get; set; }
    public string? DataBase64 { get; set; }
    public string? ServerTime { get; set; }

    public byte[]? Data => string.IsNullOrEmpty(DataBase64) ? null : Convert.FromBase64String(DataBase64);
}

public sealed class VaultUploadResponse
{
    public bool Ok { get; set; }
    public long VaultVersion { get; set; }
    public string? VaultSha256 { get; set; }
    public string? ServerTime { get; set; }
}

public sealed class VaultConflictResponse
{
    public long VaultVersion { get; set; }
    public string? ServerTime { get; set; }
}

public sealed class WrappedKeyResponse
{
    public string? DataBase64 { get; set; }
}
