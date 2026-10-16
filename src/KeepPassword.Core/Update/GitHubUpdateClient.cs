using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using KeepPassword.Core.Install;

namespace KeepPassword.Core.Update;

public sealed class GitHubUpdateClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public GitHubUpdateClient(HttpClient? httpClient = null)
    {
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        EnsureHeaders(_http);
    }

    public static void EnsureHeaders(HttpClient http)
    {
        if (http.DefaultRequestHeaders.UserAgent.Count == 0)
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("KeepPassword/" + InstallConstants.Version);
        }

        if (!http.DefaultRequestHeaders.Accept.Any(h => h.MediaType == "application/vnd.github+json"))
        {
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        }
    }

    public async Task<UpdateCheckResult> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _http.GetAsync(UpdatePaths.LatestReleaseApiUrl, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(currentVersion, null, false, "GitHub 上还没有发布版本。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    currentVersion,
                    null,
                    false,
                    DescribeHttpFailure(response.StatusCode));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            var release = await ParseReleaseAsync(stream, cancellationToken).ConfigureAwait(false);
            if (release is null)
            {
                return new UpdateCheckResult(
                    currentVersion,
                    null,
                    false,
                    "最新发布缺少安装包或校验文件，请确认 Release 中包含 "
                    + UpdatePaths.SetupAssetName + " 与 " + UpdatePaths.Sha256AssetName + "。");
            }

            if (!AppVersion.TryParse(currentVersion, out var current)
                || !AppVersion.TryParse(release.Version, out var latest))
            {
                return new UpdateCheckResult(currentVersion, release, false, "版本号格式无效，无法比较。");
            }

            return new UpdateCheckResult(currentVersion, release, latest.CompareTo(current) > 0, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(currentVersion, null, false, "检查更新超时，请确认能访问 GitHub 后重试。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new UpdateCheckResult(currentVersion, null, false, "无法连接 GitHub，请检查网络或代理后重试。");
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(currentVersion, null, false, "检查更新失败：" + Truncate(ex.Message));
        }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateRelease release,
        string installRoot,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var cache = UpdatePaths.CacheDirectory(installRoot);
        Directory.CreateDirectory(cache);

        var setupPath = UpdatePaths.SetupPath(installRoot);
        var shaPath = Path.Combine(cache, UpdatePaths.Sha256AssetName);
        var setupTmp = setupPath + ".tmp";
        var shaTmp = shaPath + ".tmp";

        try
        {
            await DownloadFileAsync(release.Sha256DownloadUrl, shaTmp, progress: null, weightStart: 0, weightEnd: 0.02, cancellationToken)
                .ConfigureAwait(false);
            var expected = ParseSha256File(await File.ReadAllTextAsync(shaTmp, cancellationToken).ConfigureAwait(false));
            if (expected is null)
            {
                throw new InvalidDataException("校验文件内容无效。");
            }

            ReplaceFile(shaTmp, shaPath);

            await DownloadFileAsync(release.SetupDownloadUrl, setupTmp, progress, weightStart: 0.02, weightEnd: 0.98, cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(0.99);
            var actual = await ComputeSha256HexAsync(setupTmp, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(setupTmp);
                TryDelete(shaPath);
                throw new InvalidDataException("安装包校验失败，已删除下载文件。请重新下载。");
            }

            ReplaceFile(setupTmp, setupPath);
            progress?.Report(1);
            return new UpdateDownloadResult(setupPath, expected);
        }
        catch
        {
            TryDelete(setupTmp);
            TryDelete(shaTmp);
            throw;
        }
    }

    public static UpdateRelease? ParseReleaseJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ParseReleaseDocument(doc.RootElement);
    }

    public static string? ParseSha256File(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        foreach (var raw in content.Split(['\r', '\n', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            if (token.Length == 64 && token.All(IsHex))
            {
                return token.ToUpperInvariant();
            }
        }

        return null;
    }

    public static async Task<string> ComputeSha256HexAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private async Task<UpdateRelease?> ParseReleaseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseReleaseDocument(doc.RootElement);
    }

    private static UpdateRelease? ParseReleaseDocument(JsonElement root)
    {
        if (!root.TryGetProperty("tag_name", out var tagEl))
        {
            return null;
        }

        var tag = tagEl.GetString();
        if (string.IsNullOrWhiteSpace(tag) || !AppVersion.TryParse(tag, out var version))
        {
            return null;
        }

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? setupUrl = null;
        string? shaUrl = null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (string.Equals(name, UpdatePaths.SetupAssetName, StringComparison.OrdinalIgnoreCase))
            {
                setupUrl = url;
            }
            else if (string.Equals(name, UpdatePaths.Sha256AssetName, StringComparison.OrdinalIgnoreCase))
            {
                shaUrl = url;
            }
        }

        if (setupUrl is null || shaUrl is null)
        {
            return null;
        }

        return new UpdateRelease(tag.Trim(), version.ToString(), setupUrl, shaUrl);
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<double>? progress,
        double weightStart,
        double weightEnd,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(DescribeHttpFailure(response.StatusCode));
        }

        var total = response.Content.Headers.ContentLength ?? -1;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(
            destination,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[81920];
        long readTotal = 0;
        int read;
        while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            readTotal += read;
            if (progress is not null && total > 0)
            {
                var fraction = Math.Clamp((double)readTotal / total, 0, 1);
                progress.Report(weightStart + (weightEnd - weightStart) * fraction);
            }
        }

        progress?.Report(weightEnd);
    }

    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            File.Delete(destination);
        }

        File.Move(source, destination);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }

    private static bool IsHex(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static string DescribeHttpFailure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests =>
            "GitHub 请求过于频繁或被拒绝，请稍后再试。",
        HttpStatusCode.NotFound => "找不到发布资源。",
        _ => $"GitHub 返回错误（{(int)status}）。"
    };

    private static string Truncate(string message)
    {
        message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return message.Length <= 160 ? message : message[..157] + "…";
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
