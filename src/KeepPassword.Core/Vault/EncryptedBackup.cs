namespace KeepPassword.Core.Vault;

public static class EncryptedBackup
{
    public static void Export(string vaultPath, string destination)
    {
        if (string.IsNullOrWhiteSpace(vaultPath) || !File.Exists(vaultPath))
        {
            throw new FileNotFoundException("找不到保险库文件。", vaultPath);
        }

        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new ArgumentException("请选择备份位置。", nameof(destination));
        }

        var source = Path.GetFullPath(vaultPath);
        var target = Path.GetFullPath(destination);
        if (string.Equals(source, target, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException("备份不能覆盖正在使用的保险库文件。");
        }

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = target + ".tmp";
        File.Copy(source, temp, overwrite: true);
        FileProtection.RestrictToCurrentUser(temp);
        File.Move(temp, target, overwrite: true);
        FileProtection.RestrictToCurrentUser(target);
    }

    public static IReadOnlyList<VaultEntry> ReadEntries(string backupPath, string masterPassword)
    {
        using var session = new VaultStore().Unlock(backupPath, masterPassword);
        return session.Entries;
    }
}
