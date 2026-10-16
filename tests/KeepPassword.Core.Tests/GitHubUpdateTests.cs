using System.Net;
using System.Security.Cryptography;
using System.Text;
using KeepPassword.Core.Update;

namespace KeepPassword.Core.Tests;

public class GitHubUpdateTests
{
    [Theory]
    [InlineData("0.6.3", 0, 6, 3)]
    [InlineData("v1.2.10", 1, 2, 10)]
    [InlineData("2.0", 2, 0, 0)]
    [InlineData("0.6.3+abc", 0, 6, 3)]
    public void AppVersion_Parses(string text, int major, int minor, int patch)
    {
        Assert.True(AppVersion.TryParse(text, out var version));
        Assert.Equal(new AppVersion(major, minor, patch), version);
    }

    [Theory]
    [InlineData("0.6.2", "0.6.3", -1)]
    [InlineData("0.6.3", "0.6.3", 0)]
    [InlineData("1.0.0", "0.9.9", 1)]
    public void AppVersion_Compares(string left, string right, int expectedSign)
    {
        Assert.True(AppVersion.TryParse(left, out var a));
        Assert.True(AppVersion.TryParse(right, out var b));
        Assert.Equal(Math.Sign(expectedSign), Math.Sign(a.CompareTo(b)));
    }

    [Fact]
    public void ParseReleaseJson_ReadsSetupAndShaAssets()
    {
        var json = """
            {
              "tag_name": "v0.6.4",
              "assets": [
                {
                  "name": "KeepPassword-Setup-win-x64.exe",
                  "browser_download_url": "https://example.com/setup.exe"
                },
                {
                  "name": "KeepPassword-Setup-win-x64.exe.sha256",
                  "browser_download_url": "https://example.com/setup.sha256"
                }
              ]
            }
            """;

        var release = GitHubUpdateClient.ParseReleaseJson(json);
        Assert.NotNull(release);
        Assert.Equal("v0.6.4", release.TagName);
        Assert.Equal("0.6.4", release.Version);
        Assert.Equal("https://example.com/setup.exe", release.SetupDownloadUrl);
        Assert.Equal("https://example.com/setup.sha256", release.Sha256DownloadUrl);
    }

    [Fact]
    public void ParseReleaseJson_MissingAsset_ReturnsNull()
    {
        var json = """
            {
              "tag_name": "v0.6.4",
              "assets": [
                { "name": "KeepPassword-win-x64.zip", "browser_download_url": "https://example.com/zip" }
              ]
            }
            """;
        Assert.Null(GitHubUpdateClient.ParseReleaseJson(json));
    }

    [Theory]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789", true)]
    [InlineData("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789  KeepPassword-Setup-win-x64.exe", true)]
    [InlineData("too-short", false)]
    [InlineData("", false)]
    public void ParseSha256File_AcceptsHex(string content, bool ok)
    {
        var parsed = GitHubUpdateClient.ParseSha256File(content);
        Assert.Equal(ok, parsed is not null);
        if (ok)
        {
            Assert.Equal(64, parsed!.Length);
        }
    }

    [Fact]
    public async Task CheckAsync_FindsNewerRelease()
    {
        var json = """
            {
              "tag_name": "v0.7.0",
              "assets": [
                {
                  "name": "KeepPassword-Setup-win-x64.exe",
                  "browser_download_url": "https://example.com/setup.exe"
                },
                {
                  "name": "KeepPassword-Setup-win-x64.exe.sha256",
                  "browser_download_url": "https://example.com/setup.sha256"
                }
              ]
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubUpdateClient(http);

        var result = await client.CheckAsync("0.6.3");
        Assert.Null(result.Error);
        Assert.True(result.UpdateAvailable);
        Assert.Equal("0.7.0", result.Latest!.Version);
    }

    [Fact]
    public async Task CheckAsync_SameVersion_NotAvailable()
    {
        var json = """
            {
              "tag_name": "v0.6.3",
              "assets": [
                {
                  "name": "KeepPassword-Setup-win-x64.exe",
                  "browser_download_url": "https://example.com/setup.exe"
                },
                {
                  "name": "KeepPassword-Setup-win-x64.exe.sha256",
                  "browser_download_url": "https://example.com/setup.sha256"
                }
              ]
            }
            """;
        using var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubUpdateClient(http);

        var result = await client.CheckAsync("0.6.3");
        Assert.Null(result.Error);
        Assert.False(result.UpdateAvailable);
    }

    [Fact]
    public async Task DownloadAsync_VerifiesSha256()
    {
        var payload = Encoding.UTF8.GetBytes("keep-password-setup-body");
        var hash = Convert.ToHexString(SHA256.HashData(payload));
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        using var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(hash + "  KeepPassword-Setup-win-x64.exe\n")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubUpdateClient(http);

        var release = new UpdateRelease(
            "v0.7.0",
            "0.7.0",
            "https://example.com/KeepPassword-Setup-win-x64.exe",
            "https://example.com/KeepPassword-Setup-win-x64.exe.sha256");

        var downloaded = await client.DownloadAsync(release, root);
        Assert.True(File.Exists(downloaded.SetupPath));
        Assert.Equal(hash, downloaded.Sha256Hex);
        Assert.Equal(payload, await File.ReadAllBytesAsync(downloaded.SetupPath));
    }

    [Fact]
    public async Task DownloadAsync_MismatchedHash_DeletesFile()
    {
        var payload = Encoding.UTF8.GetBytes("keep-password-setup-body");
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);

        using var handler = new StubHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(new string('a', 64))
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        using var http = new HttpClient(handler);
        using var client = new GitHubUpdateClient(http);

        var release = new UpdateRelease(
            "v0.7.0",
            "0.7.0",
            "https://example.com/KeepPassword-Setup-win-x64.exe",
            "https://example.com/KeepPassword-Setup-win-x64.exe.sha256");

        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadAsync(release, root));
        Assert.False(File.Exists(UpdatePaths.SetupPath(root)));
        Assert.False(File.Exists(UpdatePaths.SetupPath(root) + ".tmp"));
    }

    [Fact]
    public void UpdatePaths_IsUnderUpdateCache_AndCleanup()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        var cache = UpdatePaths.CacheDirectory(root);
        Directory.CreateDirectory(cache);
        var setup = Path.Combine(cache, UpdatePaths.SetupAssetName);
        var other = Path.Combine(cache, "notes.txt");
        File.WriteAllText(setup, "x");
        File.WriteAllText(other, "keep");
        File.WriteAllText(setup + ".sha256", new string('b', 64));

        Assert.True(UpdatePaths.IsUnderUpdateCache(setup));
        Assert.False(UpdatePaths.IsUnderUpdateCache(other));

        UpdatePaths.CleanupCache(root);
        Assert.False(File.Exists(setup));
        Assert.False(File.Exists(setup + ".sha256"));
        Assert.True(File.Exists(other));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
