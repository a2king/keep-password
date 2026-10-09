using KeepPassword.Core.Import;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class DomainGroupView
{
    public required string Domain { get; init; }

    public required IReadOnlyList<EntryItemView> Entries { get; init; }
}

public sealed class EntryItemView : ViewModelBase
{
    private bool _isSelected;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Username { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class MainViewModel : ViewModelBase
{
    private readonly VaultSession _session;
    private string _search = "";
    private string _name = "";
    private string _url = "";
    private string _username = "";
    private string _password = "";
    private string _note = "";
    private string _totpSecret = "";
    private string _status = "";
    private string _totpPreview = "";
    private string _snapshot = "";
    private bool _hasDetail;
    private Guid _editingId;
    private bool _passwordVisible;

    public MainViewModel(VaultSession session, bool platformAutofillSupported)
    {
        _session = session;
        PlatformAutofillSupported = platformAutofillSupported;
        Refresh();
    }

    public string Account => _session.Account;

    public bool PlatformAutofillSupported { get; }

    public string AutofillHint => PlatformAutofillSupported
        ? "程序补全会查找密码框。填入前仍要确认短密钥。"
        : "当前系统的程序补全尚未实现，可使用浏览器扩展。填入前仍要确认短密钥。";

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                Refresh();
            }
        }
    }

    public IReadOnlyList<DomainGroupView> Groups { get; private set; } = [];

    public bool HasDetail
    {
        get => _hasDetail;
        private set => Set(ref _hasDetail, value);
    }

    public bool ShowEmpty => Groups.Count == 0 && !HasDetail;

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Url
    {
        get => _url;
        set => Set(ref _url, value);
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public string TotpSecret
    {
        get => _totpSecret;
        set => Set(ref _totpSecret, value);
    }

    public bool PasswordVisible
    {
        get => _passwordVisible;
        set
        {
            if (Set(ref _passwordVisible, value))
            {
                OnPropertyChanged(nameof(PasswordToggleText));
            }
        }
    }

    public string PasswordToggleText => PasswordVisible ? "隐藏" : "显示";

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string TotpPreview
    {
        get => _totpPreview;
        private set => Set(ref _totpPreview, value);
    }

    public bool IsDirty => HasDetail && Snapshot() != _snapshot;

    public void Load(Guid id)
    {
        var entry = _session.Entries.FirstOrDefault(item => item.Id == id);
        if (entry is null)
        {
            return;
        }

        Apply(entry);
        MarkSelected(id);
        Status = "";
    }

    public void NewLogin()
    {
        Apply(new VaultEntry { Id = Guid.NewGuid(), Name = "" });
        ClearSelection();
        Status = "新的登录条目，保存后会出现在列表中。";
    }

    public void NewTotp()
    {
        Apply(new VaultEntry { Id = Guid.NewGuid(), Name = "" });
        ClearSelection();
        Status = "独立验证码只需填写名称和密钥。";
    }

    public string? Save()
    {
        if (!HasDetail)
        {
            return "请先选择或新建一条记录。";
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            return "请填写名称。";
        }

        var entry = new VaultEntry
        {
            Id = _editingId,
            Name = Name.Trim(),
            Url = Url.Trim(),
            Username = Username,
            Password = Password,
            Note = Note,
            TotpSecret = string.IsNullOrWhiteSpace(TotpSecret) ? null : TotpSecret.Trim()
        };
        _session.Upsert(entry);
        _session.Save();
        _snapshot = Snapshot();
        Refresh();
        MarkSelected(entry.Id);
        Status = "已保存。";
        RefreshTotp();
        return null;
    }

    public bool Delete()
    {
        if (!HasDetail)
        {
            return false;
        }

        var existed = _session.Entries.Any(entry => entry.Id == _editingId);
        if (existed)
        {
            _session.Remove(_editingId);
            _session.Save();
        }

        HasDetail = false;
        _snapshot = "";
        ClearFields();
        Refresh();
        Status = "已删除。";
        return true;
    }

    public int ImportCsv(string text)
    {
        var imported = CsvImporter.Import(text);
        foreach (var entry in imported)
        {
            _session.Upsert(entry);
        }

        if (imported.Count > 0)
        {
            _session.Save();
        }

        Refresh();
        Status = imported.Count == 0 ? "没有可导入的行。" : $"已导入 {imported.Count} 条，并按域名归类。";
        return imported.Count;
    }

    public void RefreshTotp()
    {
        if (!HasDetail || string.IsNullOrWhiteSpace(TotpSecret))
        {
            TotpPreview = "";
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!TotpGenerator.TryGenerateFromBase32(TotpSecret, now, out var code))
        {
            TotpPreview = "密钥无法识别";
            return;
        }

        TotpPreview = $"{code}    剩余 {TotpGenerator.RemainingSeconds(now)} 秒";
    }

    public void Refresh()
    {
        var selected = _editingId;
        var filtered = EntrySearch.Query(_session.Entries, Search);
        Groups = DomainGrouping.Group(filtered)
            .Select(group => new DomainGroupView
            {
                Domain = group.Domain,
                Entries = group.Entries.Select(entry => new EntryItemView
                {
                    Id = entry.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名" : entry.Name,
                    Username = entry.Username,
                    IsSelected = entry.Id == selected && HasDetail
                }).ToList()
            })
            .ToList();
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void Apply(VaultEntry entry)
    {
        _editingId = entry.Id;
        Name = entry.Name;
        Url = entry.Url;
        Username = entry.Username;
        Password = entry.Password;
        Note = entry.Note;
        TotpSecret = entry.TotpSecret ?? "";
        PasswordVisible = false;
        HasDetail = true;
        _snapshot = Snapshot();
        RefreshTotp();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void ClearFields()
    {
        Name = "";
        Url = "";
        Username = "";
        Password = "";
        Note = "";
        TotpSecret = "";
        TotpPreview = "";
        PasswordVisible = false;
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void ClearSelection()
    {
        foreach (var group in Groups)
        {
            foreach (var entry in group.Entries)
            {
                entry.IsSelected = false;
            }
        }
    }

    private void MarkSelected(Guid id)
    {
        foreach (var group in Groups)
        {
            foreach (var entry in group.Entries)
            {
                entry.IsSelected = entry.Id == id;
            }
        }
    }

    private string Snapshot() => string.Join('\u001f', Name, Url, Username, Password, Note, TotpSecret);
}
