using KeepPassword.Core.Paths;

namespace KeepPassword.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void AutoLockSeconds_DefaultsAndPersistsWithDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "kp-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var settings = Path.Combine(root, "settings.json");
        var cacheA = Path.Combine(root, "a");
        var cacheB = Path.Combine(root, "b");
        Directory.CreateDirectory(cacheA);

        Assert.Equal(AppSettings.DefaultAutoLockSeconds, AppSettings.GetAutoLockSeconds(settings));

        AppSettings.SetAutoLockSeconds(settings, 45);
        Assert.Equal(45, AppSettings.GetAutoLockSeconds(settings));

        CacheDirectory.Switch(settings, cacheA, cacheB);
        Assert.Equal(Path.GetFullPath(cacheB).TrimEnd(Path.DirectorySeparatorChar), CacheDirectory.Resolve(settings, cacheA));
        Assert.Equal(45, AppSettings.GetAutoLockSeconds(settings));

        AppSettings.SetAutoLockSeconds(settings, 2);
        Assert.Equal(AppSettings.MinAutoLockSeconds, AppSettings.GetAutoLockSeconds(settings));
    }
}
