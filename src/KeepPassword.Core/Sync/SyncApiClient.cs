using System.Net;
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
        using var message = JsonMessage(HttpMethod.Post, "api/sync/device/bind", request);
        using var response = await SendAsync(message, ct);
        return await ReadAsync<DeviceBindResponse>(response, authenticated: false, ct);
    }

    public async Task RequestDeviceCodeAsync(string email, CancellationToken ct = default)
    {
        using var message = JsonMessage(HttpMethod.Post, "api/sync/device/request-code", new { email });
        using var response = await SendAsync(message, ct);
        await EnsureSuccess(response, authenticated: false, ct);
    }

    public async Task<SyncStatusResponse> GetStatusAsync(string deviceToken, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Get, "api/sync/status", deviceToken);
        using var response = await SendAsync(message, ct);
        return await ReadAsync<SyncStatusResponse>(response, authenticated: true, ct);
    }

    public async Task<VaultDownloadResponse> DownloadVaultAsync(string deviceToken, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Get, "api/sync/vault", deviceToken);
        using var response = await SendAsync(message, ct);
        return await ReadAsync<VaultDownloadResponse>(response, authenticated: true, ct);
    }

    public async Task<VaultUploadResponse> UploadVaultAsync(string deviceToken, long baseVersion, long keyVersion, byte[] data, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Put, "api/sync/vault", deviceToken);
        message.Content = JsonContent.Create(new
        {
            baseVersion,
            keyVersion,
            dataBase64 = Convert.ToBase64String(data)
        }, options: JsonOptions);
        using var response = await SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var error = await ReadErrorAsync(response, ct);
            throw ConflictFrom(error);
        }

        return await ReadAsync<VaultUploadResponse>(response, authenticated: true, ct);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "api/health");
        using var response = await SendAsync(message, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task UnbindSelfAsync(string deviceToken, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Post, "api/sync/device/unbind-self", deviceToken);
        using var response = await SendAsync(message, ct);
        await EnsureSuccess(response, authenticated: true, ct);
    }

    public async Task UploadWrappedKeyAsync(string deviceToken, byte[] wrapped, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Put, "api/sync/wrapped-key", deviceToken);
        message.Content = JsonContent.Create(new { dataBase64 = Convert.ToBase64String(wrapped) }, options: JsonOptions);
        using var response = await SendAsync(message, ct);
        await EnsureSuccess(response, authenticated: true, ct);
    }

    public async Task<SyncCommitResponse> CommitAsync(
        string deviceToken,
        string purpose,
        long expectedKeyVersion,
        long expectedVaultVersion,
        byte[] vault,
        byte[]? wrapped,
        CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Put, "api/sync/commit", deviceToken);
        message.Content = JsonContent.Create(new
        {
            purpose,
            expectedKeyVersion,
            expectedVaultVersion,
            dataBase64 = Convert.ToBase64String(vault),
            wrappedKeyBase64 = wrapped is null ? null : Convert.ToBase64String(wrapped)
        }, options: JsonOptions);
        using var response = await SendAsync(message, ct);
        if (response.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)426)
        {
            var error = await ReadErrorAsync(response, ct);
            throw ConflictFrom(error, (int)response.StatusCode);
        }

        return await ReadAsync<SyncCommitResponse>(response, authenticated: true, ct);
    }

    public async Task<byte[]?> DownloadWrappedKeyAsync(string deviceToken, CancellationToken ct = default)
    {
        using var message = Authorized(HttpMethod.Get, "api/sync/wrapped-key", deviceToken);
        using var response = await SendAsync(message, ct);
        var body = await ReadAsync<WrappedKeyResponse>(response, authenticated: true, ct);
        return string.IsNullOrEmpty(body.DataBase64) ? null : Convert.FromBase64String(body.DataBase64);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new SyncApiException(SyncFailureKind.Cancelled, "已取消。");
        }
        catch (OperationCanceledException)
        {
            throw new SyncApiException(SyncFailureKind.Timeout, "连接超时，请稍后重试。");
        }
        catch (HttpRequestException ex) when (IsTls(ex))
        {
            throw new SyncApiException(SyncFailureKind.Tls, "安全连接失败，请检查网络或系统时间后重试。");
        }
        catch (HttpRequestException)
        {
            throw new SyncApiException(SyncFailureKind.Network, "无法连接同步服务器，请检查网络后重试。");
        }
    }

    private static bool IsTls(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError)
        {
            return true;
        }

        for (Exception? inner = ex.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is System.Security.Authentication.AuthenticationException)
            {
                return true;
            }
        }

        return false;
    }

    private static HttpRequestMessage JsonMessage(HttpMethod method, string path, object body)
    {
        return new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string deviceToken)
    {
        var message = new HttpRequestMessage(method, path);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);
        return message;
    }

    private static async Task EnsureSuccess(HttpResponseMessage response, bool authenticated, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await CreateStatusException(response, authenticated, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, bool authenticated, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateStatusException(response, authenticated, ct);
        }

        try
        {
            var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
            return value ?? throw new SyncApiException(SyncFailureKind.Server, "同步服务器暂时无法处理，请稍后重试。");
        }
        catch (SyncApiException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            throw new SyncApiException(SyncFailureKind.Server, "同步服务器暂时无法处理，请稍后重试。");
        }
    }

    private static async Task<SyncApiException> CreateStatusException(HttpResponseMessage response, bool authenticated, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        var body = await ReadErrorAsync(response, ct);
        var sessionInvalid = authenticated && response.StatusCode == HttpStatusCode.Unauthorized;
        if (response.StatusCode is HttpStatusCode.Conflict or (HttpStatusCode)426)
        {
            return ConflictFrom(body, status);
        }

        return new SyncApiException(KindFor(status), MessageFor(status, body?.Error, sessionInvalid), status, sessionInvalid);
    }

    private static async Task<ErrorBody?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(raw) || raw.Length > 4096 || !raw.TrimStart().StartsWith('{'))
            {
                return null;
            }

            return JsonSerializer.Deserialize<ErrorBody>(raw, JsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SyncFailureKind KindFor(int status) => status switch
    {
        401 => SyncFailureKind.Unauthorized,
        403 => SyncFailureKind.Forbidden,
        409 => SyncFailureKind.Conflict,
        422 => SyncFailureKind.Validation,
        429 => SyncFailureKind.RateLimited,
        >= 500 => SyncFailureKind.Server,
        >= 400 => SyncFailureKind.Validation,
        _ => SyncFailureKind.Unexpected
    };

    private static string MessageFor(int status, string? serverError, bool sessionInvalid)
    {
        if (sessionInvalid)
        {
            return SyncMessages.SessionExpired;
        }

        if (status == 422)
        {
            return "提交内容未通过校验，请检查后重试。";
        }

        if (IsPublicError(serverError))
        {
            var text = serverError!.Trim();
            if (text == "验证码不正确或已过期")
            {
                return "验证码不正确或已过期，请重新获取。";
            }

            return text;
        }

        return status switch
        {
            401 => "登录校验未通过。",
            403 => "没有权限完成此操作。",
            409 => "云端版本冲突，请稍后刷新。本地数据已保留。",
            429 => "操作过于频繁，请稍后再试。",
            >= 500 => $"同步服务器暂时无法处理（{status}），请稍后重试。",
            >= 400 => "请求无效，请检查填写内容。",
            _ => "同步请求失败，请稍后重试。"
        };
    }

    internal static bool IsPublicError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        var text = error.Trim();
        if (text.Length > 160)
        {
            return false;
        }

        var run = 0;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '<' or '>' or '{' or '}')
            {
                return false;
            }

            if (char.IsAsciiLetterOrDigit(ch) || ch is '+' or '/' or '=')
            {
                run++;
                if (run >= 32)
                {
                    return false;
                }
            }
            else
            {
                run = 0;
            }
        }

        var lower = text.ToLowerInvariant();
        if (lower.Contains("password") || lower.Contains("secret") || lower.Contains("smtp_password") || text.Contains("授权码"))
        {
            return false;
        }

        return !lower.Contains("bearer", StringComparison.Ordinal)
            && !lower.Contains("kpb-", StringComparison.Ordinal)
            && !text.Contains("eyJ", StringComparison.Ordinal)
            && !lower.Contains("datab64", StringComparison.Ordinal)
            && !text.Contains("dataBase64", StringComparison.Ordinal);
    }

    private static SyncApiException ConflictFrom(ErrorBody? body, int status = 409)
    {
        if (status == 426 || body?.UpgradeRequired == true)
        {
            return new SyncKeyChangedException(body?.KeyVersion ?? 0, upgradeRequired: true);
        }

        if (body?.KeyChanged == true)
        {
            return new SyncKeyChangedException(body.KeyVersion, upgradeRequired: false);
        }

        return new SyncConflictException(body?.VaultVersion ?? 0, body?.ServerTime);
    }

    private sealed class ErrorBody
    {
        public string? Error { get; set; }
        public bool Conflict { get; set; }
        public bool KeyChanged { get; set; }
        public bool UpgradeRequired { get; set; }
        public long KeyVersion { get; set; }
        public long VaultVersion { get; set; }
        public string? ServerTime { get; set; }
    }
}

public enum SyncFailureKind
{
    Network,
    Timeout,
    Cancelled,
    Tls,
    Unauthorized,
    Forbidden,
    Conflict,
    Validation,
    RateLimited,
    Server,
    Unexpected
}

public class SyncApiException : Exception
{
    public SyncApiException(string message)
        : this(SyncFailureKind.Unexpected, message)
    {
    }

    public SyncApiException(SyncFailureKind kind, string message, int? statusCode = null, bool sessionInvalid = false)
        : base(message)
    {
        Kind = kind;
        StatusCode = statusCode;
        SessionInvalid = sessionInvalid;
    }

    public SyncFailureKind Kind { get; }

    public int? StatusCode { get; }

    /// <summary>带设备令牌的请求返回 401。不是绑定口令错误。</summary>
    public bool SessionInvalid { get; }
}

public sealed class SyncKeyChangedException : SyncApiException
{
    public SyncKeyChangedException(long keyVersion, bool upgradeRequired)
        : base(
            upgradeRequired ? SyncFailureKind.Validation : SyncFailureKind.Conflict,
            upgradeRequired ? SyncMessages.UpgradeRequired : SyncMessages.KeyChanged,
            upgradeRequired ? 426 : 409)
    {
        KeyVersion = keyVersion;
        UpgradeRequired = upgradeRequired;
    }

    public long KeyVersion { get; }

    public bool UpgradeRequired { get; }
}

public sealed class SyncConflictException : SyncApiException
{
    public SyncConflictException(long serverVersion, string? serverTime)
        : base(SyncFailureKind.Conflict, "云端保险库已有更新，请先合并后再上传。", 409)
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
    public long KeyVersion { get; set; }
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
    public long KeyVersion { get; set; }
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

public sealed class SyncCommitResponse
{
    public bool Ok { get; set; }
    public bool Idempotent { get; set; }
    public long KeyVersion { get; set; }
    public long VaultVersion { get; set; }
    public string? VaultSha256 { get; set; }
}

public sealed class WrappedKeyResponse
{
    public string? DataBase64 { get; set; }
}

public sealed class BindResult
{
    public BindResult(SyncSessionState state, bool vaultSynced, bool adoptedCloudKey)
    {
        State = state;
        VaultSynced = vaultSynced;
        AdoptedCloudKey = adoptedCloudKey;
    }

    public SyncSessionState State { get; }

    /// <summary>保险库上传或合并上传已得到成功响应。</summary>
    public bool VaultSynced { get; }

    /// <summary>本机数据密钥已切换为云端密钥包解开的密钥。云端密钥包没有被覆盖。</summary>
    public bool AdoptedCloudKey { get; }
}
