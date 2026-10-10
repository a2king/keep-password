using System.Security.Cryptography;
using System.Text;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Security;

public sealed record BreachFinding(Guid EntryId, string Name, string Domain, int Count, string Advice);

public interface IPwnedRangeClient
{
    Task<string> GetRangeAsync(string prefix, CancellationToken cancellationToken);
}

public sealed class HibpRangeClient : IPwnedRangeClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public HibpRangeClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("KeepPassword/0.3.1");
        }

        if (!_http.DefaultRequestHeaders.Contains("Add-Padding"))
        {
            _http.DefaultRequestHeaders.Add("Add-Padding", "true");
        }
    }

    public async Task<string> GetRangeAsync(string prefix, CancellationToken cancellationToken)
    {
        if (prefix.Length != 5)
        {
            throw new ArgumentException("范围前缀必须是 5 位。", nameof(prefix));
        }

        if (_cache.TryGetValue(prefix, out var cached))
        {
            return cached;
        }

        try
        {
            var body = await _http.GetStringAsync("https://api.pwnedpasswords.com/range/" + prefix, cancellationToken).ConfigureAwait(false);
            _cache[prefix] = body;
            return body;
        }
        catch (HttpRequestException ex)
        {
            throw new IOException("泄露检查失败，密码未离开本机。", ex);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}

public static class BreachChecker
{
    public const string PrivacyNotice =
        "泄露检查使用 k-匿名范围查询：只发送密码 SHA-1 的前 5 位，不上传明文或完整哈希。查询结果只显示条目名称和出现次数。";

    public static string Sha1Hex(string password) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(password)));

    public static int MatchCount(string rangeBody, string sha1Hex)
    {
        ArgumentNullException.ThrowIfNull(rangeBody);
        if (sha1Hex.Length != 40)
        {
            throw new ArgumentException("SHA-1 应为 40 位十六进制。", nameof(sha1Hex));
        }

        var suffix = sha1Hex[5..];
        foreach (var line in rangeBody.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':');
            if (parts.Length < 2)
            {
                continue;
            }

            if (parts[0].Equals(suffix, StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[1], out var count))
            {
                return count;
            }
        }

        return 0;
    }

    public static async Task<IReadOnlyList<BreachFinding>> CheckAsync(
        IEnumerable<VaultEntry> entries,
        IPwnedRangeClient client,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(client);
        var findings = new List<BreachFinding>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (string.IsNullOrEmpty(entry.Password))
            {
                continue;
            }

            if (!seen.TryGetValue(entry.Password, out var count))
            {
                var hash = Sha1Hex(entry.Password);
                var body = await client.GetRangeAsync(hash[..5], cancellationToken).ConfigureAwait(false);
                count = MatchCount(body, hash);
                seen[entry.Password] = count;
            }

            if (count <= 0)
            {
                continue;
            }

            findings.Add(new BreachFinding(
                entry.Id,
                string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name,
                DomainGrouping.HostOf(entry.Url),
                count,
                "该密码曾出现在公开泄露数据中 " + count + " 次。请立即更换，且不要在其他站点复用。"));
        }

        return findings;
    }
}
