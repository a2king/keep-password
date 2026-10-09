using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class UnlockViewModel : ViewModelBase
{
    private readonly VaultStore? _store;
    private readonly VaultSession? _softSession;
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

    public UnlockViewModel(VaultStore store, string path)
    {
        _store = store;
        _path = path;
        IsCreate = !store.Exists(path);
        RefreshAccount();
    }

    public UnlockViewModel(VaultSession softSession)
    {
        _softSession = softSession;
        _path = softSession.VaultPath;
        AccountLabel = "账号 " + softSession.Account;
        IsCreate = false;
    }

    public bool IsSoftUnlock => _softSession is not null;

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
                OnPropertyChanged(nameof(NeedsMasterPassword));
                OnPropertyChanged(nameof(NeedsShortKey));
            }
        }
    }

    public bool NeedsMasterPassword => !IsSoftUnlock;

    public bool NeedsShortKey => IsCreate || IsSoftUnlock;

    public string Title => IsCreate
        ? "创建保险库"
        : IsSoftUnlock
            ? "已锁定"
            : "解锁保险库";

    public string Hint => IsCreate
        ? "第一次使用会在本机创建保险库。请设置账号、主密码和短密钥。主密码用来加密保险库，短密钥另外保存验证哈希，两者都不会明文落盘。"
        : IsSoftUnlock
            ? "程序已锁定。输入短密钥即可继续，无需再输入主密码。"
            : "启动后第一次解锁需要输入主密码。锁定后再次打开只需短密钥。";

    public string SubmitText => IsCreate ? "创建并解锁" : "解锁";

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
        if (IsSoftUnlock || _store is null)
        {
            return;
        }

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

        var account = Account.Trim();
        var master = MasterPassword;
        var shortKey = ShortKey;
        IsBusy = true;
        try
        {
            if (IsSoftUnlock)
            {
                var soft = _softSession!;
                await Task.Run(() => soft.UnlockWithShortKey(shortKey));
                Session = soft;
                return true;
            }

            Session = await Task.Run(() =>
            {
                var store = _store!;
                if (IsCreate)
                {
                    store.Create(_path, account, master, shortKey);
                }

                return store.Unlock(_path, master);
            });
            return true;
        }
        catch (Exception ex) when (ex is UnlockFailedException or ArgumentException or InvalidOperationException or IOException or InvalidDataException)
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
        }
    }

    private void RefreshAccount()
    {
        if (IsCreate || _store is null || !_store.Exists(_path))
        {
            AccountLabel = "";
            return;
        }

        try
        {
            AccountLabel = "账号 " + _store.ReadAccount(_path);
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
