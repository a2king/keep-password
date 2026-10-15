using System.Security.Cryptography;
using KeepPassword.Core.Crypto;

namespace KeepPassword.Core.Vault;

public sealed partial class VaultSession : IDisposable
{
    private readonly object _gate = new();
    private string _path;
    private VaultStore.KdfMaterial _kdf;
    private VaultStore.ShortKeyMaterial _shortKey;
    private readonly List<VaultEntry> _entries;
    private readonly VaultLabels _labels;
    private byte[] _dataKey;
    private VaultStore.ShortWrapMaterial? _shortWrap;
    private byte[]? _shortWrapKey;
    private bool _deviceQuick;
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
        VaultLabels? labels = null,
        VaultStore.ShortWrapMaterial? shortWrap = null,
        byte[]? shortWrapKey = null,
        bool deviceQuick = false,
        VaultStore.CipherDto? passwordWrap = null,
        bool keyFileSaved = false)
    {
        _path = path;
        Account = account;
        _kdf = kdf;
        _shortKey = shortKey;
        _dataKey = masterKey;
        _entries = entries;
        _recovery = recovery;
        _shortWrap = shortWrap;
        _shortWrapKey = shortWrapKey;
        _deviceQuick = deviceQuick;
        _passwordWrap = passwordWrap;
        _keyFileSaved = keyFileSaved;
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

    public bool ShortUnlockReady
    {
        get
        {
            lock (_gate)
            {
                return _unlocked && (_shortWrap is not null || _deviceQuick);
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
            var copy = Prepare(entry);
            var index = _entries.FindIndex(item => item.Id == copy.Id);
            if (index >= 0 && _entries[index].Kind != copy.Kind)
            {
                throw new InvalidOperationException("条目类型创建后不能修改。");
            }

            if (copy.Accounts.Any(account => account.SshKeyId is { } keyId && !_entries.Any(item => item.Id == keyId && item.Kind == VaultItemKind.SshKey)))
            {
                throw new ArgumentException("关联的 SSH 密钥不存在。", nameof(entry));
            }

            Commit(copy, index);
        }
    }

    public void ReplaceAll(IEnumerable<VaultEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        lock (_gate)
        {
            EnsureUnlocked();
            var prepared = entries.Select(Prepare).ToList();
            DropDanglingKeyReferences(prepared);
            _entries.Clear();
            foreach (var copy in prepared)
            {
                Commit(copy, _entries.FindIndex(item => item.Id == copy.Id));
            }
        }
    }

    public IReadOnlyList<string> ReferencingNames(Guid keyId)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            return _entries
                .Where(entry => entry.SshKeyId == keyId || entry.Accounts.Any(account => account.SshKeyId == keyId))
                .Select(entry => string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name)
                .ToList();
        }
    }

    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            EnsureUnlocked();
            var removed = _entries.RemoveAll(entry => entry.Id == id) > 0;
            if (removed)
            {
                DropDanglingKeyReferences(_entries);
            }

            return removed;
        }
    }

    internal static void DropDanglingKeyReferences(List<VaultEntry> entries)
    {
        var keys = entries.Where(entry => entry.Kind == VaultItemKind.SshKey).Select(entry => entry.Id).ToHashSet();
        foreach (var entry in entries)
        {
            if (entry.SshKeyId is { } id && !keys.Contains(id))
            {
                entry.SshKeyId = null;
                entry.SetField(FieldKeys.Auth, null);
            }

            foreach (var account in entry.Accounts)
            {
                if (account.SshKeyId is { } keyId && !keys.Contains(keyId))
                {
                    account.SshKeyId = null;
                    account.Auth = "";
                }
            }
        }
    }

    private static VaultEntry Prepare(VaultEntry entry)
    {
        var copy = entry.Clone();
        if (copy.Id == Guid.Empty)
        {
            copy.Id = Guid.NewGuid();
        }

        foreach (var field in copy.CustomFields)
        {
            field.Name = field.Name.Trim();
        }

        ItemTemplates.AdoptLegacyKeyAuth(copy);
        var allowKey = ItemTemplates.SupportsKeyAuth(copy);
        foreach (var account in copy.Accounts)
        {
            if (account.Id == Guid.Empty)
            {
                account.Id = Guid.NewGuid();
            }

            if (!allowKey || account.Auth != FieldKeys.AuthSshKey)
            {
                account.Auth = "";
                account.SshKeyId = null;
                continue;
            }

            account.Password = "";
        }

        if (copy.Kind == VaultItemKind.SshKey)
        {
            var details = SshKeyInfo.Inspect(copy.Field(FieldKeys.PrivateKey), copy.Field(FieldKeys.PublicKey)).Details;
            copy.SetField(FieldKeys.KeyType, details?.KeyType);
            copy.SetField(FieldKeys.Fingerprint, details?.Fingerprint);
        }

        if (VaultItemRules.Validate(copy) is { } error)
        {
            throw new ArgumentException(error, nameof(entry));
        }

        return copy;
    }

    private void Commit(VaultEntry copy, int index)
    {
        copy.Space = VaultLabels.AddTo(_labels.Spaces, copy.Space);
        copy.Tags = LabelName.NormalizeAll(copy.Tags)
            .Select(tag => VaultLabels.AddTo(_labels.Tags, tag))
            .ToList();
        if (index < 0)
        {
            _entries.Add(copy);
        }
        else
        {
            _entries[index] = copy;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            EnsureUnlocked();
            VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _dataKey, _entries, _recovery, _labels, _passwordWrap, _keyFileSaved);
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
            VerifyMasterPassword(currentPassword);

            var salt = RandomNumberGenerator.GetBytes(Argon2Id.SaltLength);
            var upgrading = _passwordWrap is null;
            var next = upgrading ? RandomNumberGenerator.GetBytes(Argon2Id.HashLength) : _dataKey;
            var previous = _dataKey;
            var nextKdf = new VaultStore.KdfMaterial(_kdf.Profile, salt);
            var passwordWrap = VaultStore.WrapDataKey(newPassword, nextKdf, next, Account);
            var nextWrap = !upgrading ? _shortWrap : _shortWrapKey is null || _shortWrap is null
                ? null
                : VaultStore.RewrapShortUnlock(_shortWrap, _shortWrapKey, next);
            try
            {
                if (upgrading)
                {
                    VaultStore.WriteFile(_path, Account, nextKdf, _shortKey, next, _entries, labels: _labels,
                        passwordWrap: passwordWrap);
                }
                else
                {
                    VaultStore.UpdateKeyHeader(_path, nextKdf, passwordWrap, _keyFileSaved);
                }
            }
            catch
            {
                if (upgrading) CryptographicOperations.ZeroMemory(next);
                throw;
            }

            _dataKey = next;
            _kdf = nextKdf;
            _passwordWrap = passwordWrap;
            _shortWrap = nextWrap;
            if (upgrading)
            {
                _keyFileSaved = false;
                _recovery?.Zero();
                _recovery = null;
            }

            try
            {
                if (_shortWrap is not null)
                {
                    VaultStore.WriteQuickUnlock(_path, _kdf.Salt, _shortWrap);
                    _deviceQuick = false;
                }
                else if (_deviceQuick)
                {
                    if (!VaultStore.TryWriteDeviceQuickUnlock(_path, _kdf.Salt, _dataKey))
                    {
                        VaultStore.DeleteQuickUnlock(_path);
                        _deviceQuick = false;
                    }
                }
                else
                {
                    VaultStore.DeleteQuickUnlock(_path);
                    _shortWrap = null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VaultStore.DeleteQuickUnlock(_path);
                _shortWrap = null;
                _deviceQuick = false;
                if (_shortWrapKey is not null)
                {
                    CryptographicOperations.ZeroMemory(_shortWrapKey);
                    _shortWrapKey = null;
                }
            }

            if (upgrading) CryptographicOperations.ZeroMemory(previous);
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
            VerifyMasterPassword(masterPassword);

            var nextShort = VaultStore.HashShortKey(newShortKey, _shortKey.Profile);
            var (wrap, key) = VaultStore.CreateShortWrap(newShortKey, _shortKey.Profile, _dataKey);
            var previousShort = _shortKey;
            var previousWrap = _shortWrap;
            var previousKey = _shortWrapKey;
            _shortKey = nextShort;
            _shortWrap = wrap;
            _shortWrapKey = key;
            try
            {
                VaultStore.WriteFile(_path, Account, _kdf, _shortKey, _dataKey, _entries, _recovery, _labels, _passwordWrap, _keyFileSaved);
            }
            catch
            {
                _shortKey = previousShort;
                _shortWrap = previousWrap;
                _shortWrapKey = previousKey;
                CryptographicOperations.ZeroMemory(key);
                throw;
            }

            try
            {
                VaultStore.WriteQuickUnlock(_path, _kdf.Salt, wrap);
                _deviceQuick = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                VaultStore.DeleteQuickUnlock(_path);
                _deviceQuick = false;
            }

            if (previousKey is not null)
            {
                CryptographicOperations.ZeroMemory(previousKey);
            }
        }
    }

    public void EnableShortUnlock(string shortKey)
    {
        if (string.IsNullOrEmpty(shortKey))
        {
            throw new ArgumentException("请填写短密钥。", nameof(shortKey));
        }

        lock (_gate)
        {
            EnsureUnlocked();
            if (!Argon2Id.Verify(shortKey, _shortKey.Profile, _shortKey.Salt, _shortKey.Hash))
            {
                throw new ArgumentException("短密钥不正确。", nameof(shortKey));
            }

            var (wrap, key) = VaultStore.CreateShortWrap(shortKey, _shortKey.Profile, _dataKey);
            var previousWrap = _shortWrap;
            var previousKey = _shortWrapKey;
            _shortWrap = wrap;
            _shortWrapKey = key;
            try
            {
                VaultStore.WriteQuickUnlock(_path, _kdf.Salt, wrap);
                _deviceQuick = false;
            }
            catch
            {
                _shortWrap = previousWrap;
                _shortWrapKey = previousKey;
                CryptographicOperations.ZeroMemory(key);
                throw;
            }

            if (previousKey is not null)
            {
                CryptographicOperations.ZeroMemory(previousKey);
            }
        }
    }

    public void Lock()
    {
        bool wasUnlocked;
        lock (_gate)
        {
            wasUnlocked = _unlocked;
            ClearSecrets();
        }
        if (wasUnlocked) Locked?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Locked;

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
        if (_dataKey.Length > 0)
        {
            CryptographicOperations.ZeroMemory(_dataKey);
        }

        if (_shortWrapKey is not null)
        {
            CryptographicOperations.ZeroMemory(_shortWrapKey);
            _shortWrapKey = null;
        }

        _shortWrap = null;
        _deviceQuick = false;
        _entries.Clear();
        _labels.Spaces.Clear();
        _labels.Tags.Clear();
        _recovery?.Zero();
        _recovery = null;
        _unlocked = false;
    }
}
