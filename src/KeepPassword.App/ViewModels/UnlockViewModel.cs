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
                OnPropertyChanged(nameof(Hint));
                OnPropertyChanged(nameof(SubmitText));
            }
        }
    }

    public string Title => IsCreate ? "创建保险库" : "解锁保险库";

    public string Hint => IsCreate
        ? "第一次使用会在本机创建保险库。请设置账号、主密码和短密钥。主密码用来加密保险库，短密钥另外保存验证哈希，两者都不会明文落盘。"
        : "再次打开只需输入主密码。短密钥只在自动填充和修改短密钥时使用。";

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
        IsCreate = !_store.Exists(path);
        RefreshAccount();
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

        var account = Account.Trim();
        var master = MasterPassword;
        var shortKey = ShortKey;
        var answers = new[] { Answer1, Answer2, Answer3 };
        var questions = new[] { Question1, Question2, Question3 };
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

                    _store.Create(_path, account, master, shortKey, recovery);
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
            return;
        }

        try
        {
            AccountLabel = "账号 " + _store.ReadAccount(_path);
            RecoveryAvailable = _store.HasRecovery(_path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            AccountLabel = "";
            Error = Friendly(ex);
        }
    }

    private static string Friendly(Exception ex)
    {
        var message = ex.Message;
        var cut = message.IndexOf(" (Parameter ", StringComparison.Ordinal);
        return cut > 0 ? message[..cut] : message;
    }
}
