using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class UnlockViewModel : ViewModelBase
{
    private readonly VaultStore _store;
    private string _path;
    private string _account = "";
    private string _accountLabel = "";
    private string _accountName = "";
    private string _masterPassword = "";
    private string _confirmMaster = "";
    private string _shortKey = "";
    private string _confirmShort = "";
    private string _error = "";
    private bool _busy;
    private bool _isCreate;
    private bool _riskAcknowledged;
    private bool _isRecovering;
    private bool _recoveryAvailable;
    private bool _hasShortUnlock;
    private bool _useMaster;
    private bool _useEncryptionKey;
    private bool _isLocalKeyVault;
    private string _answer1 = "";
    private string _answer2 = "";
    private string _answer3 = "";
    private string _recoveryQuestion1 = "";
    private string _recoveryQuestion2 = "";
    private string _recoveryQuestion3 = "";

    public UnlockViewModel(VaultStore store, string path)
    {
        _store = store;
        _path = path;
        IsCreate = !store.Exists(path);
        RefreshAccount();
    }

    public bool IsCreate
    {
        get => _isCreate;
        private set
        {
            if (Set(ref _isCreate, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(SubmitText));
                NotifyMode();
            }
        }
    }

    public string Title => IsCreate ? "创建保险库" : "解锁保险库";

    public string Hint
    {
        get
        {
            if (IsCreate)
            {
                return "设置短密钥后，将生成一把独立的加密密钥。请按提示永久保存密钥文件；本机之后用短密钥解锁，换电脑或忘记短密钥时用密钥文件。";
            }

            if (IsRecovering)
            {
                if (_isLocalKeyVault || ShowKeyUnlock)
                {
                    return "使用永久保存的加密密钥设置新的短密钥。保险库数据和加密密钥保持不变。";
                }

                return SupportsKeyRecovery
                    ? "使用永久保存的加密密钥设置新主密码。保险库数据和加密密钥保持不变。"
                    : "回答原有安全问题后设置新的主密码。解锁后请在设置中更换主密码，升级为独立加密密钥。";
            }

            if (ShowShortUnlock)
            {
                return _isLocalKeyVault
                    ? "输入短密钥即可解锁。忘记短密钥时可以使用加密密钥。"
                    : "输入短密钥即可解锁。忘记短密钥时可以改用主密码。";
            }

            if (ShowKeyUnlock)
            {
                return "此保险库无主密码。请选择加密密钥文件并设置本机短密钥。";
            }

            if (ShowPairingShort)
            {
                return "首次在本机解锁，请输入主密码。之后打开只需短密钥。";
            }

            return "使用主密码解锁。下次仍可改用短密钥。";
        }
    }

    public bool HasShortUnlock
    {
        get => _hasShortUnlock;
        private set
        {
            if (Set(ref _hasShortUnlock, value))
            {
                NotifyMode();
            }
        }
    }

    public bool ShowShortUnlock => !IsCreate && !IsRecovering && HasShortUnlock && !_useMaster && !_useEncryptionKey;

    public bool ShowPairingShort => !IsCreate && !IsRecovering && !HasShortUnlock && !_isLocalKeyVault && !_useEncryptionKey;

    public bool ShowKeyUnlock => !IsCreate && !IsRecovering && _isLocalKeyVault && (!HasShortUnlock || _useEncryptionKey);

    public bool ShowMaster => !ShowShortUnlock && !ShowKeyUnlock && !IsCreate;

    public bool ShowShortField => ShowShortUnlock;

    public bool ShowUseMaster => ShowShortUnlock && !_isLocalKeyVault;

    public bool ShowUseEncryptionKey => ShowShortUnlock && _isLocalKeyVault;

    public bool ShowUseShort => !IsCreate && !IsRecovering && HasShortUnlock && (_useMaster || _useEncryptionKey);

    public bool IsQuick => !IsCreate && !IsRecovering && !ShowKeyUnlock;

    public bool ShowHint => IsCreate || IsRecovering || ShowPairingShort || ShowKeyUnlock;

    public bool ShowCreateShort => IsCreate;

    public bool ShowCreateConfirmShort => IsCreate;

    public string MasterLabel => IsRecovering
        ? (_isLocalKeyVault ? "新短密钥" : "新主密码")
        : "主密码";

    public string MasterWatermark => IsCreate
        ? "设置短密钥"
        : IsRecovering
            ? (_isLocalKeyVault ? "输入新短密钥" : "输入新主密码")
            : "输入主密码";

    public string AccountName
    {
        get => _accountName;
        private set
        {
            if (Set(ref _accountName, value))
            {
                OnPropertyChanged(nameof(Initial));
            }
        }
    }

    public string Initial
    {
        get
        {
            if (AccountName.Length > 0)
            {
                return AccountName[..1].ToUpperInvariant();
            }

            return _isLocalKeyVault ? "K" : "?";
        }
    }

    public string SubmitText => IsRecovering
        ? (_isLocalKeyVault ? "重置短密钥并解锁" : "重置并解锁")
        : IsCreate
            ? "创建并解锁"
            : ShowKeyUnlock
                ? "导入密钥并解锁"
                : "解锁";

    public string RecoveryWarning => RecoveryRisk.Warning;

    public bool SupportsKeyRecovery { get; private set; }
    public bool ShowKeyRecovery => (IsRecovering && SupportsKeyRecovery) || ShowKeyUnlock;
    public bool ShowLegacyRecovery => IsRecovering && !SupportsKeyRecovery && !_isLocalKeyVault;
    public string EncryptionKey { get; set; } = "";

    public bool RiskAcknowledged
    {
        get => _riskAcknowledged;
        set => Set(ref _riskAcknowledged, value);
    }

    public bool IsRecovering
    {
        get => _isRecovering;
        private set
        {
            if (Set(ref _isRecovering, value))
            {
                OnPropertyChanged(nameof(SubmitText));
                NotifyMode();
            }
        }
    }

    public bool RecoveryAvailable
    {
        get => _recoveryAvailable;
        private set => Set(ref _recoveryAvailable, value);
    }

    public string Answer1 { get => _answer1; set => Set(ref _answer1, value); }
    public string Answer2 { get => _answer2; set => Set(ref _answer2, value); }
    public string Answer3 { get => _answer3; set => Set(ref _answer3, value); }
    public string RecoveryQuestion1 { get => _recoveryQuestion1; private set => Set(ref _recoveryQuestion1, value); }
    public string RecoveryQuestion2 { get => _recoveryQuestion2; private set => Set(ref _recoveryQuestion2, value); }
    public string RecoveryQuestion3 { get => _recoveryQuestion3; private set => Set(ref _recoveryQuestion3, value); }

    public string AccountLabel
    {
        get => _accountLabel;
        private set => Set(ref _accountLabel, value);
    }

    public string Account
    {
        get => _account;
        set => Set(ref _account, value);
    }

    public string MasterPassword
    {
        get => _masterPassword;
        set => Set(ref _masterPassword, value);
    }

    public string ConfirmMaster
    {
        get => _confirmMaster;
        set => Set(ref _confirmMaster, value);
    }

    public string ShortKey
    {
        get => _shortKey;
        set => Set(ref _shortKey, value);
    }

    public string ConfirmShort
    {
        get => _confirmShort;
        set => Set(ref _confirmShort, value);
    }

    public string Error
    {
        get => _error;
        set
        {
            if (Set(ref _error, value))
            {
                OnPropertyChanged(nameof(HasError));
            }
        }
    }

    public bool HasError => Error.Length > 0;

    public bool IsBusy
    {
        get => _busy;
        set => Set(ref _busy, value);
    }

    public VaultSession? Session { get; private set; }

    public void DropSession()
    {
        Session?.Dispose();
        Session = null;
    }

    public void UseVaultFile(string path)
    {
        _path = path;
        _useMaster = false;
        _useEncryptionKey = false;
        IsCreate = !_store.Exists(path);
        RefreshAccount();
    }

    public void UseMasterPassword()
    {
        if (!HasShortUnlock || IsCreate || _isLocalKeyVault)
        {
            return;
        }

        _useMaster = true;
        _useEncryptionKey = false;
        Error = "";
        NotifyMode();
    }

    public void UseEncryptionKey()
    {
        if (!_isLocalKeyVault || IsCreate)
        {
            return;
        }

        _useEncryptionKey = true;
        _useMaster = false;
        Error = "";
        NotifyMode();
    }

    public void UseShortKey()
    {
        _useMaster = false;
        _useEncryptionKey = false;
        Error = "";
        NotifyMode();
    }

    public async Task<bool> SubmitAsync()
    {
        Error = "";
        if (IsCreate && ShortKey != ConfirmShort)
        {
            Error = "两次输入的短密钥不一致。";
            return false;
        }

        if (ShowLegacyRecovery && !RiskAcknowledged)
        {
            Error = RecoveryRisk.Warning;
            return false;
        }

        if (IsRecovering && MasterPassword != ConfirmMaster)
        {
            Error = _isLocalKeyVault ? "两次输入的短密钥不一致。" : "两次输入的新主密码不一致。";
            return false;
        }

        if (ShowKeyUnlock && ShortKey != ConfirmShort)
        {
            Error = "两次输入的短密钥不一致。";
            return false;
        }

        if (ShowShortField && string.IsNullOrEmpty(ShortKey))
        {
            Error = "请填写短密钥。";
            return false;
        }

        if ((ShowKeyUnlock || (IsRecovering && (_isLocalKeyVault || SupportsKeyRecovery))) && string.IsNullOrEmpty(EncryptionKey))
        {
            Error = "请选择或粘贴加密密钥。";
            return false;
        }

        var master = MasterPassword;
        var shortKey = IsCreate || ShowKeyUnlock ? ShortKey : ShortKey;
        if (IsRecovering && _isLocalKeyVault)
        {
            shortKey = MasterPassword;
        }

        var answers = new[] { Answer1, Answer2, Answer3 };
        var useShort = ShowShortUnlock;
        var useKey = ShowKeyUnlock || (IsRecovering && _isLocalKeyVault);
        var encryptionKey = EncryptionKey;
        if ((IsCreate || ShowKeyUnlock) && string.IsNullOrEmpty(shortKey))
        {
            Error = "请填写短密钥。";
            return false;
        }
        IsBusy = true;
        try
        {
            Session = await Task.Run(() =>
            {
                if (IsRecovering)
                {
                    if (_isLocalKeyVault || useKey)
                    {
                        return _store.UnlockWithEncryptionKey(_path, encryptionKey, shortKey);
                    }

                    return SupportsKeyRecovery
                        ? _store.RecoverWithKey(_path, encryptionKey, master)
                        : _store.Recover(_path, answers, master);
                }

                if (IsCreate)
                {
                    return _store.CreateLocalSession(_path, shortKey);
                }

                if (useKey)
                {
                    return _store.UnlockWithEncryptionKey(_path, encryptionKey, shortKey);
                }

                if (useShort)
                {
                    return _store.UnlockWithShortKey(_path, shortKey);
                }

                return _store.Unlock(_path, master);
            });
            return true;
        }
        catch (Exception ex) when (ex is UnlockFailedException or RecoveryFailedException or CredentialRejectedException or ArgumentException or InvalidOperationException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Error = Friendly(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
            MasterPassword = "";
            ConfirmMaster = "";
            ShortKey = "";
            ConfirmShort = "";
            Answer1 = "";
            Answer2 = "";
            Answer3 = "";
            EncryptionKey = "";
        }
    }

    public void BeginRecovery()
    {
        if (!RecoveryAvailable)
        {
            return;
        }

        var questions = SupportsKeyRecovery || _isLocalKeyVault ? [] : _store.RecoveryQuestions(_path);
        RecoveryQuestion1 = questions.ElementAtOrDefault(0) ?? "";
        RecoveryQuestion2 = questions.ElementAtOrDefault(1) ?? "";
        RecoveryQuestion3 = questions.ElementAtOrDefault(2) ?? "";
        RiskAcknowledged = false;
        _useEncryptionKey = false;
        _useMaster = false;
        IsRecovering = true;
        Error = "";
    }

    public void CancelRecovery()
    {
        IsRecovering = false;
        RiskAcknowledged = false;
        Answer1 = "";
        Answer2 = "";
        Answer3 = "";
        EncryptionKey = "";
    }

    public void CancelKeySetup()
    {
        Session?.Dispose();
        Session = null;
        IsCreate = false;
        IsRecovering = false;
        RefreshAccount();
        Error = "密钥文件尚未保存。下次解锁后会继续提示保存。";
    }

    private void RefreshAccount()
    {
        if (IsCreate || !_store.Exists(_path))
        {
            AccountLabel = "";
            AccountName = "";
            HasShortUnlock = false;
            _isLocalKeyVault = false;
            NotifyMode();
            return;
        }

        try
        {
            AccountName = _store.ReadAccount(_path);
            if (string.IsNullOrWhiteSpace(AccountName))
            {
                AccountName = "本地保险库";
                AccountLabel = AccountName;
            }
            else
            {
                AccountLabel = "账号 " + AccountName;
            }

            SupportsKeyRecovery = _store.UsesIndependentKey(_path);
            _isLocalKeyVault = _store.IsLocalKeyVault(_path);
            RecoveryAvailable = SupportsKeyRecovery || _isLocalKeyVault || _store.HasRecovery(_path);
            HasShortUnlock = _store.HasShortUnlock(_path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            AccountLabel = "";
            HasShortUnlock = false;
            _isLocalKeyVault = false;
            Error = Friendly(ex);
        }

        NotifyMode();
    }

    private void NotifyMode()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(ShowMaster));
        OnPropertyChanged(nameof(ShowShortField));
        OnPropertyChanged(nameof(ShowShortUnlock));
        OnPropertyChanged(nameof(ShowPairingShort));
        OnPropertyChanged(nameof(ShowKeyUnlock));
        OnPropertyChanged(nameof(ShowUseMaster));
        OnPropertyChanged(nameof(ShowUseEncryptionKey));
        OnPropertyChanged(nameof(ShowUseShort));
        OnPropertyChanged(nameof(IsQuick));
        OnPropertyChanged(nameof(ShowHint));
        OnPropertyChanged(nameof(MasterLabel));
        OnPropertyChanged(nameof(MasterWatermark));
        OnPropertyChanged(nameof(ShowKeyRecovery));
        OnPropertyChanged(nameof(ShowLegacyRecovery));
        OnPropertyChanged(nameof(ShowCreateShort));
        OnPropertyChanged(nameof(ShowCreateConfirmShort));
        OnPropertyChanged(nameof(SubmitText));
        OnPropertyChanged(nameof(Initial));
    }

    private static string Friendly(Exception ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" (Parameter ", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}
