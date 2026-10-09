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
        using var session = TestVault.Store().Unlock(Path.Combine(destination, AppDataPaths.VaultFileName), "correct horse");
        Assert.Equal("ada", session.Account);
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

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "keep-password-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        return root;
    }
}
