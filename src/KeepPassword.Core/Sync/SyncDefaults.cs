namespace KeepPassword.Core.Sync;

/// <summary>
/// 客户端访问的 API 根地址。服务器上的主机名列表在 keep-password-server 的 deploy/domains.env。
/// </summary>
public static class SyncDefaults
{
    /// <summary>
    /// 生产环境 API 根地址，按优先级排列，不要带路径。
    /// 过渡期把旧地址加在后面；新绑定会依次探测，用第一个能连通的。已绑定设备仍用当时保存的地址。
    /// </summary>
    public static readonly string[] ProductionBaseUrls =
    [
        "https://api.keep-password.work",
    ];

    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<string> BaseUrls { get; } = ResolveBaseUrls();

    public static string BaseUrl => BaseUrls[0];

    public static string ProductionBaseUrl => ProductionBaseUrls[0];

    /// <summary>
    /// 新绑定用。只有一个地址时直接返回，不额外发请求。
    /// 多个地址时依次请求 /api/health，用第一个成功的。
    /// </summary>
    public static async Task<string> PickBaseUrlAsync(CancellationToken ct = default)
    {
        var urls = BaseUrls;
        if (urls.Count <= 1)
        {
            return urls[0];
        }

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var api = new SyncApiClient(url);
                if (await api.PingAsync(ct))
                {
                    return url;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                last = ex;
            }
        }

        if (last is not null)
        {
            throw last;
        }

        return urls[0];
    }

    private static string[] ResolveBaseUrls()
    {
        var single = Environment.GetEnvironmentVariable("KEEP_PASSWORD_SYNC_URL");
        if (!string.IsNullOrWhiteSpace(single))
        {
            return [Normalize(single)];
        }

        var many = Environment.GetEnvironmentVariable("KEEP_PASSWORD_SYNC_URLS");
        if (!string.IsNullOrWhiteSpace(many))
        {
            var parsed = Split(many);
            if (parsed.Length > 0)
            {
                return parsed;
            }
        }

        return ProductionBaseUrls;
    }

    private static string[] Split(string raw) =>
        raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(url => url.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string Normalize(string url) => url.Trim().TrimEnd('/');
}
