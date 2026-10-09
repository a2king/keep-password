using System.Security.Cryptography;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public sealed class VaultSession : IDisposable
{
    private readonly object _gate = new();
    private string _path;
    private readonly VaultStore.KdfMaterial _kdf;
    private VaultStore.ShortKeyMaterial _shortKey;
    private readonly List<VaultEntry> _entries;
    private byte[] _masterKey;
    private bool _unlocked;

    internal VaultSession(
        string path,
        string account,
        VaultStore.KdfMaterial kdf,
        VaultStore.ShortKeyMaterial shortKey,
        byte[] masterKey,
        List<VaultEntry> entries)
    {
        _path = path;
        Account = account;
        _kdf = kdf;
        _shortKey = shortKey;
        _masterKey = masterKey;
        _entries = entries;
        _unlocked = true;
    }

    public string Account { get; }

    public string VaultPath
    {
        get
        {
            lock (_gate)
            {
                return _path;
            }
        }
    }

    public void Relocate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("请填写保险库路径。", nameof(path));
        }

        lock (_gate)
        {
            EnsureUnlocked();
            _path = path;
        }
    }

    public bool IsUnlocked
    {
        get
        {
            lock (_gate)
            {
                return _unlocked;
            }
        }
    }

    public IReadOnlyList<VaultEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                EnsureUnlocked();
                return _entries.Select(entry => entry.Clone()).ToList();
            }
        }
    }

    public void Upsert(VaultEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            EnsureUnlocked();
            var copy = entry.Clone();
            if (copy.Id == Guid.Empty)
            {
                copy.Id = Guid.NewGuid();
            }

            var index = _entries.FindIndex(item => item.Id == copy.Id);
            if (index < 0)
            {
                _entries.Add(copy);
            }
            else
            {
                _entries[index] = copy;
            }
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            return _entries.RemoveAll(entry => entry.Id == id) > 0;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            EnsureUnlocked();
            VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _masterKey, _entries);
        }
    }

    public bool VerifyShortKey(string? shortKey)
    {
        if (string.IsNullOrEmpty(shortKey))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_unlocked)
            {
                return false;
            }

            return Argon2Id.Verify(shortKey, _shortKey.Profile, _shortKey.Salt, _shortKey.Hash);
        }
    }

    public void ChangeShortKey(string masterPassword, string newShortKey)
    {
        if (string.IsNullOrEmpty(masterPassword))
        {
            throw new CredentialRejectedException("主密码不正确。");
        }

        if (string.IsNullOrEmpty(newShortKey))
        {
            throw new ArgumentException("请填写新的短密钥。", nameof(newShortKey));
        }

        lock (_gate)
        {
            EnsureUnlocked();
            var derived = Argon2Id.Derive(masterPassword, _kdf.Profile, _kdf.Salt);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(derived, _masterKey))
                {
                    throw new CredentialRejectedException("主密码不正确。");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(derived);
            }

            _shortKey = VaultStore.HashShortKey(newShortKey, _shortKey.Profile);
            VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _masterKey, _entries);
        }
    }

    public void Lock()
    {
        lock (_gate)
        {
            ClearSecrets();
        }
    }

    public void Dispose() => Lock();

    private void EnsureUnlocked()
    {
        if (!_unlocked)
        {
            throw new InvalidOperationException("保险库已锁定。");
        }
    }

    private void ClearSecrets()
    {
        if (_masterKey.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_masterKey);
        }

        _entries.Clear();
        _unlocked = false;
    }
}
