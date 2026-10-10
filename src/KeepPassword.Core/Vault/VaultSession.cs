using System.Security.Cryptography;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public sealed class VaultSession : IDisposable
{
    private readonly object _gate = new();
    private string _path;
    private VaultStore.KdfMaterial _kdf;
    private VaultStore.ShortKeyMaterial _shortKey;
    private readonly List<VaultEntry> _entries;
    private readonly VaultLabels _labels;
    private byte[] _masterKey;
    private RecoverySecrets? _recovery;
    private bool _unlocked;

    internal VaultSession(
        string path,
        string account,
        VaultStore.KdfMaterial kdf,
        VaultStore.ShortKeyMaterial shortKey,
        byte[] masterKey,
        List<VaultEntry> entries,
        RecoverySecrets? recovery = null,
        VaultLabels? labels = null)
    {
        _path = path;
        Account = account;
        _kdf = kdf;
        _shortKey = shortKey;
        _masterKey = masterKey;
        _entries = entries;
        _recovery = recovery;
        _labels = labels ?? VaultLabels.From(null, null, entries);
        _unlocked = true;
    }

    public IReadOnlyList<string> Spaces
    {
        get
        {
            lock (_gate)
            {
                EnsureUnlocked();
                return _labels.Spaces.ToList();
            }
        }
    }

    public IReadOnlyList<string> Tags
    {
        get
        {
            lock (_gate)
            {
                EnsureUnlocked();
                return _labels.Tags.ToList();
            }
        }
    }

    public string AddSpace(string name)
    {
        var normalized = LabelName.Require(name);
        lock (_gate)
        {
            EnsureUnlocked();
            return VaultLabels.AddTo(_labels.Spaces, normalized);
        }
    }

    public string AddTag(string name)
    {
        var normalized = LabelName.Require(name);
        lock (_gate)
        {
            EnsureUnlocked();
            return VaultLabels.AddTo(_labels.Tags, normalized);
        }
    }

    public void RenameSpace(string oldName, string newName)
    {
        var target = LabelName.Require(newName);
        lock (_gate)
        {
            EnsureUnlocked();
            var index = IndexOf(_labels.Spaces, oldName);
            var existing = _labels.Spaces.FindIndex(item => LabelName.Comparer.Equals(item, target));
            if (existing >= 0 && existing != index)
            {
                target = _labels.Spaces[existing];
                _labels.Spaces.RemoveAt(index);
            }
            else
            {
                _labels.Spaces[index] = target;
            }

            foreach (var entry in _entries.Where(entry => LabelName.Comparer.Equals(entry.Space, oldName)))
            {
                entry.Space = target;
            }
        }
    }

    public void RenameTag(string oldName, string newName)
    {
        var target = LabelName.Require(newName);
        lock (_gate)
        {
            EnsureUnlocked();
            var index = IndexOf(_labels.Tags, oldName);
            var existing = _labels.Tags.FindIndex(item => LabelName.Comparer.Equals(item, target));
            if (existing >= 0 && existing != index)
            {
                target = _labels.Tags[existing];
                _labels.Tags.RemoveAt(index);
            }
            else
            {
                _labels.Tags[index] = target;
            }

            foreach (var entry in _entries)
            {
                var position = entry.Tags.FindIndex(tag => LabelName.Comparer.Equals(tag, oldName));
                if (position < 0)
                {
                    continue;
                }

                entry.Tags.RemoveAt(position);
                if (!entry.Tags.Contains(target, LabelName.Comparer))
                {
                    entry.Tags.Insert(position, target);
                }
            }
        }
    }

    public void DeleteSpace(string name)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            _labels.Spaces.RemoveAt(IndexOf(_labels.Spaces, name));
            foreach (var entry in _entries.Where(entry => LabelName.Comparer.Equals(entry.Space, name)))
            {
                entry.Space = "";
            }
        }
    }

    public void DeleteTag(string name)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            _labels.Tags.RemoveAt(IndexOf(_labels.Tags, name));
            foreach (var entry in _entries)
            {
                entry.Tags.RemoveAll(tag => LabelName.Comparer.Equals(tag, name));
            }
        }
    }

    private static int IndexOf(List<string> list, string name)
    {
        var index = list.FindIndex(item => LabelName.Comparer.Equals(item, name));
        if (index < 0)
        {
            throw new ArgumentException("找不到这个分类。", nameof(name));
        }

        return index;
    }

    public bool HasRecovery
    {
        get
        {
            lock (_gate)
            {
                return _unlocked && _recovery is not null;
            }
        }
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

            copy.Space = VaultLabels.AddTo(_labels.Spaces, copy.Space);
            copy.Tags = LabelName.NormalizeAll(copy.Tags)
                .Select(tag => VaultLabels.AddTo(_labels.Tags, tag))
                .ToList();

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
            VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _masterKey, _entries, _recovery, _labels);
        }
    }

    public void ChangeMasterPassword(string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
        {
            throw new CredentialRejectedException("主密码不正确。");
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            throw new ArgumentException("请填写新的主密码。", nameof(newPassword));
        }

        if (currentPassword == newPassword)
        {
            throw new ArgumentException("新主密码不能与当前主密码相同。", nameof(newPassword));
        }

        lock (_gate)
        {
            EnsureUnlocked();
            var derived = Argon2Id.Derive(currentPassword, _kdf.Profile, _kdf.Salt);
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

            var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
            var next = Argon2Id.Derive(newPassword, _kdf.Profile, salt);
            var previous = _masterKey;
            var previousKdf = _kdf;
            try
            {
                _masterKey = next;
                _kdf = new VaultStore.KdfMaterial(_kdf.Profile, salt);
                VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _masterKey, _entries, _recovery, _labels);
            }
            catch
            {
                _masterKey = previous;
                _kdf = previousKdf;
                CryptographicOperations.ZeroMemory(next);
                throw;
            }

            CryptographicOperations.ZeroMemory(previous);
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
            VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _masterKey, _entries, _recovery, _labels);
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
        _labels.Spaces.Clear();
        _labels.Tags.Clear();
        _recovery?.Zero();
        _recovery = null;
        _unlocked = false;
    }
}
