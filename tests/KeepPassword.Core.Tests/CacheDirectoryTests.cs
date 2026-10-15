using KeepPassword.Core.Paths;
using KeepPassword.Core.Tests.Support;
using KeepPassword.Core.Vault;

namespace KeepPassword.Core.Tests;

public class CacheDirectoryTests
{
    [Fact]
    public void Resolve_WithoutSettings_UsesDefault()
    {
        var root = NewRoot();
        var settings = Path.Combine(root, "config", "settings.json");
        var fallback = Path.Combine(root, "default");

        Assert.Equal(Path.GetFullPath(fallback), CacheDirectory.Resolve(settings, fallback));
    }

    [Fact]
    public void Switch_MovesFilesAndClearsOldDirectory()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "old");
        var destination = Path.Combine(root, "new");
        var settings = Path.Combine(root, "config", "settings.json");
        Directory.CreateDirectory(source);
        var vault = Path.Combine(source, AppDataPaths.VaultFileName);
        TestVault.Store().Create(vault, "ada", "correct horse", "short-key");
        File.WriteAllText(Path.Combine(source, "note.txt"), "keep");

        var updated = CacheDirectory.Switch(settings, source, destination);

        Assert.Equal(Path.GetFullPath(destination), updated);
        Assert.False(Directory.Exists(source));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "note.txt")));
        var moved = Path.Combine(destination, AppDataPaths.VaultFileName);
        using var session = TestVault.Store().Unlock(moved, "correct horse");
        Assert.Equal("ada", session.Account);
        Assert.True(TestVault.Store().HasShortUnlock(moved));
        using var byShort = TestVault.Store().UnlockWithShortKey(moved, "short-key");
        Assert.Equal("ada", byShort.Account);
        Assert.Equal(Path.GetFullPath(destination), CacheDirectory.Resolve(settings, source));
    }

    [Fact]
    public void Switch_RefusesNestedDirectory_AndLeavesSource()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "old");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "note.txt"), "keep");
        var settings = Path.Combine(root, "config", "settings.json");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CacheDirectory.Switch(settings, source, Path.Combine(source, "child")));

        Assert.Contains("不能放在", error.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "note.txt")));
        Assert.False(File.Exists(settings));
    }

    [Fact]
    public void Switch_RefusesWhenDestinationHasTheSameName()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "old");
        var destination = Path.Combine(root, "new");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(source, AppDataPaths.VaultFileName), "old");
        File.WriteAllText(Path.Combine(destination, AppDataPaths.VaultFileName), "already");
        var settings = Path.Combine(root, "config", "settings.json");

        var error = Assert.Throws<InvalidOperationException>(() =>
            CacheDirectory.Switch(settings, source, destination));

        Assert.Contains(AppDataPaths.VaultFileName, error.Message);
        Assert.Equal("old", File.ReadAllText(Path.Combine(source, AppDataPaths.VaultFileName)));
        Assert.Equal("already", File.ReadAllText(Path.Combine(destination, AppDataPaths.VaultFileName)));
    }

    [Fact]
    public void Switch_SameDirectory_KeepsFiles()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "cache");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "note.txt"), "keep");
        var settings = Path.Combine(root, "config", "settings.json");

        var updated = CacheDirectory.Switch(settings, source, source + Path.DirectorySeparatorChar);

        Assert.Equal(Path.GetFullPath(source), updated);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(source, "note.txt")));
    }

    [Fact]
    public void Theme_DefaultAndPersistence()
    {
        var root = NewRoot();
        var settings = Path.Combine(root, "config", "settings.json");

        Assert.Equal("System", CacheDirectory.GetTheme(settings));

        CacheDirectory.SetTheme(settings, "Dark");
        Assert.Equal("Dark", CacheDirectory.GetTheme(settings));

        CacheDirectory.SetTheme(settings, "Light");
        Assert.Equal("Light", CacheDirectory.GetTheme(settings));

        // When switching directory, theme should be preserved
        var source = Path.Combine(root, "cache1");
        var dest = Path.Combine(root, "cache2");
        Directory.CreateDirectory(source);
        CacheDirectory.Switch(settings, source, dest);
        Assert.Equal("Light", CacheDirectory.GetTheme(settings));
    }

    [Fact]
    public void MotionMode_MissingSettingsAndLegacySettings_DefaultToFull()
    {
        var root = NewRoot();
        var settings = Path.Combine(root, "config", "settings.json");

        Assert.Equal("Full", CacheDirectory.GetMotionMode(settings));
        CacheDirectory.SetTheme(settings, "Dark");
        Assert.Equal("Full", CacheDirectory.GetMotionMode(settings));
    }

    [Theory]
    [InlineData("Full")]
    [InlineData("System")]
    [InlineData("Reduced")]
    public void MotionMode_PersistsAndSurvivesThemeAndDirectoryChanges(string mode)
    {
        var root = NewRoot();
        var settings = Path.Combine(root, "config", "settings.json");
        var source = Path.Combine(root, "cache1");
        var destination = Path.Combine(root, "cache2");
        var finalDestination = Path.Combine(root, "cache3");
        Directory.CreateDirectory(source);
        CacheDirectory.SetTheme(settings, "Dark");
        CacheDirectory.Switch(settings, source, destination);

        CacheDirectory.SetMotionMode(settings, mode);
        Assert.Equal(mode, CacheDirectory.GetMotionMode(settings));
        Assert.Equal("Dark", CacheDirectory.GetTheme(settings));
        Assert.Equal(Path.GetFullPath(destination), CacheDirectory.Resolve(settings, source));

        CacheDirectory.SetTheme(settings, "Light");
        CacheDirectory.Switch(settings, destination, finalDestination);
        Assert.Equal(mode, CacheDirectory.GetMotionMode(settings));
        Assert.Equal("Light", CacheDirectory.GetTheme(settings));
        Assert.Equal(Path.GetFullPath(finalDestination), CacheDirectory.Resolve(settings, source));
    }

    [Fact]
    public void MotionMode_InvalidValue_FallsBackToFull()
    {
        var root = NewRoot();
        var settings = Path.Combine(root, "settings.json");
        File.WriteAllText(settings, "{\"motionMode\":\"unknown\"}");
        Assert.Equal("Full", CacheDirectory.GetMotionMode(settings));

        CacheDirectory.SetMotionMode(settings, "unknown");
        Assert.Equal("Full", CacheDirectory.GetMotionMode(settings));
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
