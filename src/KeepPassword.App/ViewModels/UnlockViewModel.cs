using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class UnlockViewModel : ViewModelBase
{
    private readonly VaultStore _store;
    private string _path;
    private string _account = "";
    private string _accountLabel = "";
    private string _masterPassword = "";
    private string _confirmMaster = "";
    private string _shortKey = "";
    private string _confirmShort = "";
    private string _error = "";
    private bool _busy;
    private bool _isCreate;
    private bool _enableRecovery;
    private bool _riskAcknowledged;
    private bool _isRecovering;
    private bool _recoveryAvailable;
    private bool _hasShortUnlock;
    private bool _useMaster;
    private string _question1 = "";
    private string _question2 = "";
    private string _question3 = "";
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
                return "第一次使用会在本机创建保险库。请设置账号、主密码和短密钥。之后打开只需输入短密钥。";
            }

            if (IsRecovering)
            {
                return "回答安全问题后设置新的主密码。短密钥解锁会关闭，下次用主密码打开后会重新开启。";
            }

            if (ShowShortUnlock)
            {
                return "输入短密钥即可解锁。忘记短密钥时可以改用主密码。";
            }

            if (ShowPairingShort)
            {
                return "请输入主密码。之后打开只需短密钥。";
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

    public bool ShowShortUnlock => !IsCreate && !IsRecovering && HasShortUnlock && !_useMaster;

    public bool ShowPairingShort => !IsCreate && !IsRecovering && !HasShortUnlock;

    public bool ShowMaster => !ShowShortUnlock;

    public bool ShowShortField => ShowShortUnlock;

    public bool ShowUseMaster => ShowShortUnlock;

    public bool ShowUseShort => !IsCreate && !IsRecovering && HasShortUnlock && _useMaster;

    public string SubmitText => IsRecovering ? "重置并解锁" : IsCreate ? "创建并解锁" : "解锁";

    public string RecoveryWarning => RecoveryRisk.Warning;

    public bool EnableRecovery
    {
        get => _enableRecovery;
        set => Set(ref _enableRecovery, value);
    }

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

    public string Question1 { get => _question1; set => Set(ref _question1, value); }
    public string Question2 { get => _question2; set => Set(ref _question2, value); }
    public string Question3 { get => _question3; set => Set(ref _question3, value); }
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

    public void UseVaultFile(string path)
    {
        _path = path;
        _useMaster = false;
        IsCreate = !_store.Exists(path);
        RefreshAccount();
    }

    public void UseMasterPassword()
    {
        if (!HasShortUnlock || IsCreate)
        {
            return;
        }

        _useMaster = true;
        Error = "";
        NotifyMode();
    }

    public void UseShortKey()
    {
        _useMaster = false;
        Error = "";
        NotifyMode();
    }

    public async Task<bool> SubmitAsync()
    {
        Error = "";
        if (IsCreate && MasterPassword != ConfirmMaster)
        {
            Error = "两次输入的主密码不一致。";
            return false;
        }

        if (IsCreate && ShortKey != ConfirmShort)
        {
            Error = "两次输入的短密钥不一致。";
            return false;
        }

        if ((IsCreate && EnableRecovery || IsRecovering) && !RiskAcknowledged)
        {
            Error = RecoveryRisk.Warning;
            return false;
        }

        if (IsRecovering && MasterPassword != ConfirmMaster)
        {
            Error = "两次输入的新主密码不一致。";
            return false;
        }

        if (ShowShortField && string.IsNullOrEmpty(ShortKey))
        {
            Error = "请填写短密钥。";
            return false;
        }

        var account = Account.Trim();
        var master = MasterPassword;
        var shortKey = ShortKey;
        var answers = new[] { Answer1, Answer2, Answer3 };
        var questions = new[] { Question1, Question2, Question3 };
        var useShort = ShowShortUnlock;
        IsBusy = true;
        try
        {
            Session = await Task.Run(() =>
            {
                if (IsRecovering)
                {
                    return _store.Recover(_path, answers, master);
                }

                if (IsCreate)
                {
                    RecoverySetup? recovery = null;
                    if (EnableRecovery)
                    {
                        recovery = new RecoverySetup
                        {
                            RiskAcknowledged = true,
                            Questions = questions,
                            Answers = answers
                        };
                    }

                    return _store.CreateSession(_path, account, master, shortKey, recovery);
                }

                if (useShort)
                {
                    return _store.UnlockWithShortKey(_path, shortKey);
                }

                return _store.Unlock(_path, master);
            });
            return true;
        }
        catch (Exception ex) when (ex is UnlockFailedException or RecoveryFailedException or ArgumentException or InvalidOperationException or IOException or InvalidDataException)
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
        }
    }

    public void BeginRecovery()
    {
        if (!RecoveryAvailable)
        {
            return;
        }

        var questions = _store.RecoveryQuestions(_path);
        RecoveryQuestion1 = questions.ElementAtOrDefault(0) ?? "";
        RecoveryQuestion2 = questions.ElementAtOrDefault(1) ?? "";
        RecoveryQuestion3 = questions.ElementAtOrDefault(2) ?? "";
        RiskAcknowledged = false;
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
    }

    private void RefreshAccount()
    {
        if (IsCreate || !_store.Exists(_path))
        {
            AccountLabel = "";
            HasShortUnlock = false;
            NotifyMode();
            return;
        }

        try
        {
            AccountLabel = "账号 " + _store.ReadAccount(_path);
            RecoveryAvailable = _store.HasRecovery(_path);
            HasShortUnlock = _store.HasShortUnlock(_path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            AccountLabel = "";
            HasShortUnlock = false;
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
        OnPropertyChanged(nameof(ShowUseMaster));
        OnPropertyChanged(nameof(ShowUseShort));
    }

    private static string Friendly(Exception ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" (Parameter ", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}
