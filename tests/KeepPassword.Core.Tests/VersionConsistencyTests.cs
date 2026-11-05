using System.Text.Json;
using System.Text.RegularExpressions;
using KeepPassword.Core.Install;

namespace KeepPassword.Core.Tests;

public class VersionConsistencyTests
{
    [Fact]
    public void AllVersionSourcesAgree()
    {
        var root = RepoRoot();
        var props = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));
        var fromProps = Regex.Match(props, @"<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(fromProps));

        // 代码里读到的版本（InstallConstants.Version）必须就是 props 里写的那一个。
        Assert.Equal(fromProps, InstallConstants.Version);

        // 浏览器扩展的 manifest 版本也必须一致，否则商店包与桌面端版本对不上。
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extension", "manifest.json")));
        Assert.Equal(fromProps, manifest.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void ProjectsDoNotOverrideTheSharedVersion()
    {
        var root = RepoRoot();
        foreach (var project in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories))
        {
            Assert.DoesNotMatch(@"<Version>", File.ReadAllText(project));
        }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("找不到仓库根目录（Directory.Build.props）。");
    }
}
