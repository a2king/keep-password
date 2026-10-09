using KeepPassword.Core.Paths;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Services;

public sealed class VaultLocation
{
    private readonly string _settingsFile;

    public VaultLocation()
    {
        _settingsFile = CacheDirectory.SettingsFile();
        Directory = CacheDirectory.Resolve(_settingsFile, AppDataPaths.VaultDirectory());
    }

    public string Directory { get; private set; }

    public string VaultFile => Path.Combine(Directory, AppDataPaths.VaultFileName);

    public string Switch(string newDirectory, VaultSession? session)
    {
        try
        {
            Directory = CacheDirectory.Switch(_settingsFile, Directory, newDirectory);
        }
        catch
        {
            Directory = CacheDirectory.Resolve(_settingsFile, Directory);
            throw;
        }
        finally
        {
            session?.Relocate(VaultFile);
        }

        return Directory;
    }
}
