using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class UnlockViewModel : ViewModelBase
{
    private readonly VaultStore _store;
    private readonly string _path;
    private string _account = "";
    private string _masterPassword = "";
    private string _confirmMaster = "";
    private string _shortKey = "";
    private string _confirmShort = "";
    private string _error = "";
    private bool _busy;

    public UnlockViewModel(VaultStore store, string path)
    {
        _store = store;
        _path = path;
        IsCreate = !store.Exists(path);
    }

    public bool IsCreate { get; }

    public string Title => IsCreate ? "创建保险库" : "解锁保险库";

    public string Hint => IsCreate
        ? "第一次使用会在本机创建保险库。请设置账号、主密码和短密钥。主密码用来加密保险库，短密钥另外保存验证哈希，两者都不会明文落盘。"
        : "每次打开都要填写账号、主密码和短密钥。任意一项错误都无法解锁。";

    public string SubmitText => IsCreate ? "创建并解锁" : "解锁";

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
            Session = await Task.Run(() =>
            {
                if (IsCreate)
                {
                    _store.Create(_path, account, master, shortKey);
                }

                return _store.Unlock(_path, account, master, shortKey);
            });
            return true;
        }
        catch (Exception ex) when (ex is UnlockFailedException or ArgumentException or InvalidOperationException or IOException or InvalidDataException)
        {
            Error = ex.Message;
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
}
