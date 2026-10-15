using System.Security.Cryptography;
using KeepPassword.Core.Import;

namespace KeepPassword.Core.Vault;

public sealed partial class VaultSession
{
    private VaultStore.CipherDto? _passwordWrap;
    private bool _keyFileSaved;

    public bool UsesIndependentKey { get { lock (_gate) { EnsureUnlocked(); return _passwordWrap is not null; } } }

    public bool NeedsKeyFileSave { get { lock (_gate) { EnsureUnlocked(); return _passwordWrap is not null && !_keyFileSaved; } } }

    public string EncryptionKeyId
    {
        get { lock (_gate) { EnsureUnlocked(); return Convert.ToHexString(SHA256.HashData(_dataKey))[..12]; } }
    }

    public string PendingEncryptionKey
    {
        get
        {
            lock (_gate)
            {
                EnsureUnlocked();
                if (!NeedsKeyFileSave) throw new InvalidOperationException("密钥已保存，请使用你保管的密钥文件。");
                return VaultKeyFile.Encode(_dataKey);
            }
        }
    }

    private void VerifyMasterPassword(string password)
    {
        byte[] derived;
        try { derived = VaultStore.UnwrapDataKey(password, _kdf, _passwordWrap, Account); }
        catch (CryptographicException) { throw new CredentialRejectedException("主密码不正确。"); }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(derived, _dataKey))
                throw new CredentialRejectedException("主密码不正确。");
        }
        finally { CryptographicOperations.ZeroMemory(derived); }
    }

    public void VerifyEncryptionKey(string encryptionKey)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            if (_passwordWrap is null)
                throw new InvalidOperationException("请先在设置中更换一次主密码，升级保险库并保存独立加密密钥。");
            var entered = VaultKeyFile.Parse(encryptionKey);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(entered, _dataKey))
                    throw new CredentialRejectedException("此加密密钥与当前保险库不匹配。");
            }
            finally { CryptographicOperations.ZeroMemory(entered); }
        }
    }

    public void SaveEncryptionKeyFile(string path, string encryptionKey)
    {
        lock (_gate)
        {
            VerifyEncryptionKey(encryptionKey);
            EnsureExternalPath(path);
            if (!string.Equals(Path.GetExtension(path), ".kpkey", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("加密密钥文件请使用 .kpkey 扩展名。", nameof(path));
            if (File.Exists(path))
            {
                try { VerifyEncryptionKey(VaultKeyFile.Read(path)); }
                catch (Exception ex) when (ex is CredentialRejectedException or InvalidDataException)
                {
                    throw new IOException("这个位置已有其他文件或不同的密钥，请选择新的文件名。原文件已保留。", ex);
                }
            }
            else
            {
                var bytes = VaultKeyFile.Serialize(_dataKey);
                try { VaultStore.WriteProtectedFile(path, bytes, overwrite: false); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            // Only acknowledge after the user's file was written successfully. Cancellation or
            // write errors leave the setup pending, including across process restarts.
            VaultStore.UpdateKeyHeader(_path, _kdf, _passwordWrap!, keyFileSaved: true);
            _keyFileSaved = true;
        }
    }

    public string ExportCsv(string encryptionKey)
    {
        lock (_gate)
        {
            VerifyEncryptionKey(encryptionKey);
            return CsvExporter.Export(_entries);
        }
    }

    public int RestoreBackup(string backupPath, string encryptionKey)
    {
        var entries = EncryptedBackup.ReadEntries(backupPath, encryptionKey);
        lock (_gate)
        {
            EnsureUnlocked();
            if (_passwordWrap is null)
                throw new InvalidOperationException("请先更换主密码，将当前保险库升级为独立加密密钥。");
            var previous = _entries.ToList();
            var spaces = _labels.Spaces.ToList();
            var tags = _labels.Tags.ToList();
            try
            {
                ReplaceAll(entries);
                Save();
                return _entries.Count;
            }
            catch
            {
                _entries.Clear();
                _entries.AddRange(previous);
                _labels.Spaces.Clear();
                _labels.Spaces.AddRange(spaces);
                _labels.Tags.Clear();
                _labels.Tags.AddRange(tags);
                throw;
            }
        }
    }

    public void EnsureExternalPath(string path)
    {
        var target = Path.GetFullPath(path);
        var vault = Path.GetFullPath(_path);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(target, vault, comparison) || string.Equals(target, vault + ".quick", comparison)
            || string.Equals(target, vault + ".tmp", comparison))
            throw new ArgumentException("不能覆盖正在使用的保险库文件。", nameof(path));
    }
}
