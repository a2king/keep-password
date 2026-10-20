namespace KeepPassword.Core.Sync;

public static class SyncDefaults
{
    /// <summary>默认官网地址。可用环境变量 KEEP_PASSWORD_SYNC_URL 覆盖（开发时常用 http://127.0.0.1:5000）。</summary>
    public const string ProductionBaseUrl = "https://keep-password.example.com";

    public static string BaseUrl =>
        Environment.GetEnvironmentVariable("KEEP_PASSWORD_SYNC_URL")?.Trim().TrimEnd('/')
        is { Length: > 0 } overrideUrl
            ? overrideUrl
            : ProductionBaseUrl;

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);
}
