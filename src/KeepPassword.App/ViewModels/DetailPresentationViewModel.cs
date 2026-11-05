using System.Collections.Specialized;
using System.ComponentModel;

namespace KeepPassword.App.ViewModels;

/// <summary>只保存详情展示状态；字段值和持久化仍由 MainViewModel 管理。</summary>
public sealed class DetailPresentationViewModel : ViewModelBase, IDisposable
{
    private MainViewModel? _source;
    private Guid _detailId;
    private Guid? _selectedAccountId;
    private AccountEditorView? _currentAccount;
    private IReadOnlyList<DetailAccountTab> _accountTabs = [];
    private string _validationMessage = "";
    private bool _canSave;
    private bool _isDirty;

    public DetailPresentationViewModel(MainViewModel source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        source.PropertyChanged += OnSourceChanged;
        source.Accounts.CollectionChanged += OnAccountsChanged;
        Refresh();
    }

    public MainViewModel Source => _source ?? throw new ObjectDisposedException(nameof(DetailPresentationViewModel));

    public AccountEditorView? CurrentAccount => _currentAccount;

    public Guid? SelectedAccountId => _selectedAccountId;

    public IReadOnlyList<DetailAccountTab> AccountTabs => _accountTabs;

    public int ActiveAccountIndex
    {
        get => _source is not null && _currentAccount is not null ? _source.Accounts.IndexOf(_currentAccount) : -1;
        set
        {
            if (_source is not null && value >= 0 && value < _source.Accounts.Count)
            {
                SelectAccount(_source.Accounts[value]);
            }
        }
    }

    public string ValidationMessage => _validationMessage;

    public bool CanSave => _canSave;

    public bool IsDirty => _isDirty;

    /// <summary>仅在条目/字段对象替换或账号切换后触发，普通输入不会请求重建编辑器。</summary>
    public event EventHandler? DisplayChanged;

    public void SelectAccount(AccountEditorView account)
    {
        if (_source is null)
        {
            return;
        }

        // 允许调用方传入保存前的对象，但选择始终解析为当前集合中的对象。
        var current = _source.Accounts.FirstOrDefault(item => item.Id == account.Id);
        if (current is null || ReferenceEquals(current, _currentAccount))
        {
            return;
        }

        SetCurrentAccount(current);
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Refresh(bool resetSelection = false)
    {
        var source = _source;
        if (source is null || source.IsApplyingDetail)
        {
            return;
        }

        var detailId = source.DetailId;
        var selectedId = !resetSelection && detailId == _detailId ? _selectedAccountId : null;
        _detailId = detailId;
        foreach (var tab in _accountTabs)
        {
            tab.Dispose();
        }

        _accountTabs = source.HasDetail && source.ShowAccounts
            ? source.Accounts.Select((account, index) => new DetailAccountTab(account, index + 1)).ToArray()
            : [];
        OnPropertyChanged(nameof(AccountTabs));
        var account = _accountTabs.FirstOrDefault(tab => tab.Id == selectedId)?.Account
            ?? _accountTabs.FirstOrDefault()?.Account;
        SetCurrentAccount(account);
        UpdateState();
        DisplayChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_source is null)
        {
            return;
        }

        _source.PropertyChanged -= OnSourceChanged;
        _source.Accounts.CollectionChanged -= OnAccountsChanged;
        foreach (var tab in _accountTabs)
        {
            tab.Dispose();
        }

        _source = null;
        _accountTabs = [];
        SetCurrentAccount(null);
        OnPropertyChanged(nameof(AccountTabs));
        UpdateState();
        DisplayChanged = null;
    }

    private void SetCurrentAccount(AccountEditorView? account)
    {
        var changed = !ReferenceEquals(_currentAccount, account);
        _currentAccount = account;
        _selectedAccountId = account?.Id;
        if (changed)
        {
            OnPropertyChanged(nameof(CurrentAccount));
            OnPropertyChanged(nameof(SelectedAccountId));
        }

        OnPropertyChanged(nameof(ActiveAccountIndex));
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_source is null || _source.IsApplyingDetail)
        {
            return;
        }

        if (e.PropertyName is nameof(MainViewModel.DetailId) or nameof(MainViewModel.HasDetail) or nameof(MainViewModel.PresetFields))
        {
            Refresh();
        }
        else if (e.PropertyName is nameof(MainViewModel.IsDirty) or nameof(MainViewModel.Name)
            or nameof(MainViewModel.Note) or nameof(MainViewModel.IsNewItem))
        {
            UpdateState();
        }
    }

    private void OnAccountsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void UpdateState()
    {
        var source = _source;
        Set(ref _validationMessage, source is { HasDetail: true } ? source.GetDetailValidationMessage() ?? "" : "", nameof(ValidationMessage));
        Set(ref _isDirty, source?.IsDirty ?? false, nameof(IsDirty));
        Set(ref _canSave, source is { HasDetail: true } && _validationMessage.Length == 0
            && (source.IsNewItem || _isDirty), nameof(CanSave));
    }
}

/// <summary>账号顺序与存储一致；页签名称从说明、用户名依次回退。</summary>
public sealed class DetailAccountTab : ViewModelBase, IDisposable
{
    private AccountEditorView? _account;
    private readonly int _number;

    internal DetailAccountTab(AccountEditorView account, int number)
    {
        _account = account;
        _number = number;
        account.PropertyChanged += OnAccountChanged;
        account.UsernameField.PropertyChanged += OnUsernameChanged;
    }

    public Guid Id => _account?.Id ?? Guid.Empty;

    public AccountEditorView Account => _account ?? throw new ObjectDisposedException(nameof(DetailAccountTab));

    public string Label => _account is null ? "" : !string.IsNullOrWhiteSpace(_account.Note)
        ? _account.Note.Trim()
        : !string.IsNullOrWhiteSpace(_account.UsernameField.Value) ? _account.UsernameField.Value.Trim() : $"账号 {_number}";

    public void Dispose()
    {
        if (_account is null)
        {
            return;
        }

        _account.PropertyChanged -= OnAccountChanged;
        _account.UsernameField.PropertyChanged -= OnUsernameChanged;
        _account = null;
    }

    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AccountEditorView.Note))
        {
            OnPropertyChanged(nameof(Label));
        }
    }

    private void OnUsernameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SecretFieldView.Value))
        {
            OnPropertyChanged(nameof(Label));
        }
    }
}
