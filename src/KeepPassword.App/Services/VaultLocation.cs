using KeepPassword.Core.Paths;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Services;

public sealed class VaultLocation
{
    private readonly string _settingsFile;

    public VaultLocation()
        : this(CacheDirectory.SettingsFile(), AppDataPaths.VaultDirectory())
    {
    }

    internal VaultLocation(string settingsFile, string defaultDirectory)
    {
        _settingsFile = settingsFile;
        Directory = CacheDirectory.Resolve(_settingsFile, defaultDirectory);
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
