using KeepPassword.Core.Import;
using KeepPassword.Core.Security;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public sealed class FilterItemView : ViewModelBase
{
    private bool _isSelected;
    private int _count;

    public required string Key { get; init; }

    public required string Name { get; init; }

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class LabelChipView : ViewModelBase
{
    private bool _isSelected;

    public required string Name { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class EntryItemView : ViewModelBase
{
    private bool _isSelected;

    public required Guid Id { get; init; }

    public required string Address { get; init; }

    public required string Username { get; init; }

    public string Labels { get; init; } = "";

    public bool HasLabels => Labels.Length > 0;

    public string Initial
    {
        get
        {
            var text = Address.Trim();
            return text.Length == 0 ? "?" : text[..1].ToUpperInvariant();
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class EntryRowView
{
    public required IReadOnlyList<EntryItemView> Items { get; init; }

    public required int Columns { get; init; }
}

public sealed class MainViewModel : ViewModelBase
{
    public const string AllKey = "\u0001all";
    public const string NoneKey = "\u0001none";

    private readonly VaultSession _session;
    private readonly List<string> _pendingSpaces = [];
    private readonly List<string> _pendingTags = [];
    private List<VaultEntry> _all = [];
    private IReadOnlyList<string> _spaces = [];
    private IReadOnlyList<string> _tags = [];
    private List<(VaultEntry Entry, EntryItemView View)> _views = [];
    private string _spaceFilter = AllKey;
    private string _tagFilter = AllKey;
    private string _editSpace = "";
    private List<string> _editTags = [];
    private string _newSpaceText = "";
    private string _newTagText = "";
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
    private bool _isNewLogin;
    private Guid _editingId;
    private bool _passwordVisible;
    private int _columns = 3;

    public MainViewModel(VaultSession session, bool platformAutofillSupported)
    {
        _session = session;
        PlatformAutofillSupported = platformAutofillSupported;
        Refresh();
    }

    public string Account => _session.Account;

    public bool PlatformAutofillSupported { get; }

    public string AutofillHint => PlatformAutofillSupported
        ? "确认短密钥后，Windows 程序通过模拟键盘输入。浏览器使用扩展按钮或 Ctrl+Shift+L。空闲 5 分钟、锁屏或休眠时自动锁定。"
        : "当前系统的程序补全尚未实现。浏览器使用扩展按钮或 Ctrl+Shift+L。";

    public string Search
    {
        get => _search;
        set
        {
            if (Set(ref _search, value))
            {
                ApplyFilters();
            }
        }
    }

    public IReadOnlyList<FilterItemView> SpaceFilters { get; private set; } = [];

    public IReadOnlyList<FilterItemView> TagFilters { get; private set; } = [];

    public FilterItemView? SelectedSpaceFilter
    {
        get => SpaceFilters.FirstOrDefault(item => item.IsSelected);
        set
        {
            if (value is not null)
            {
                SelectSpace(value.Key);
            }
        }
    }

    public IReadOnlyList<EntryItemView> Entries { get; private set; } = [];

    public IReadOnlyList<EntryRowView> EntryRows { get; private set; } = [];

    public IReadOnlyList<LabelChipView> SpaceChoices { get; private set; } = [];

    public IReadOnlyList<LabelChipView> TagChoices { get; private set; } = [];

    public bool HasSpaceChoices => SpaceChoices.Count > 0;

    public bool HasTagChoices => TagChoices.Count > 0;

    public string EntryCountText => $"{Entries.Count} 个账号";

    public string NewSpaceText
    {
        get => _newSpaceText;
        set => Set(ref _newSpaceText, value);
    }

    public string NewTagText
    {
        get => _newTagText;
        set => Set(ref _newTagText, value);
    }

    public bool HasDetail
    {
        get => _hasDetail;
        private set => Set(ref _hasDetail, value);
    }

    public bool ShowEmpty => Entries.Count == 0;

    public string EmptyText => _all.Count == 0
        ? "还没有账号。新建一条登录，或导入 CSV。"
        : "当前分类下没有匹配的账号。";

    public bool IsNewLogin
    {
        get => _isNewLogin;
        private set => Set(ref _isNewLogin, value);
    }

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
        IsNewLogin = false;
        Status = "";
    }

    public void NewLogin()
    {
        Apply(NewEntryForCurrentFilter());
        ClearSelection();
        IsNewLogin = true;
        Status = "新的登录条目。需要密码时点「生成密码」，默认是 16 位字母和数字。";
    }

    public void FillGeneratedPassword()
    {
        Password = PasswordGenerator.Generate(PasswordGeneratorOptions.DefaultLogin);
        PasswordVisible = true;
        Status = "已填入 16 位随机密码（字母和数字）。";
    }

    public void NewTotp()
    {
        Apply(NewEntryForCurrentFilter());
        ClearSelection();
        IsNewLogin = false;
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
            TotpSecret = string.IsNullOrWhiteSpace(TotpSecret) ? null : TotpSecret.Trim(),
            Space = _editSpace,
            Tags = _editTags.ToList()
        };
        _session.Upsert(entry);
        _session.Save();
        _pendingSpaces.Clear();
        _pendingTags.Clear();
        _snapshot = Snapshot();
        Refresh();
        BuildChoices();
        MarkSelected(entry.Id);
        IsNewLogin = false;
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
        IsNewLogin = false;
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
        Status = imported.Count == 0 ? "没有可导入的行。" : $"已导入 {imported.Count} 条。";
        return imported.Count;
    }

    public IReadOnlyList<AuditFinding> Audit() => VaultAudit.Analyze(_session.Entries);

    public async Task<IReadOnlyList<BreachFinding>> CheckBreachesAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HibpRangeClient();
        return await BreachChecker.CheckAsync(_session.Entries, client, cancellationToken);
    }

    public void Reload()
    {
        HasDetail = false;
        IsNewLogin = false;
        _snapshot = "";
        ClearFields();
        Refresh();
        Status = "保险库已更新。";
    }

    public int Restore(IReadOnlyList<VaultEntry> entries)
    {
        foreach (var existing in _session.Entries.ToList())
        {
            _session.Remove(existing.Id);
        }

        foreach (var entry in entries)
        {
            _session.Upsert(entry);
        }

        _session.Save();
        HasDetail = false;
        IsNewLogin = false;
        ClearFields();
        Refresh();
        Status = entries.Count == 0 ? "备份中没有条目。" : $"已从加密备份还原 {entries.Count} 条。";
        return entries.Count;
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

    public void SelectSpace(string key)
    {
        if (_spaceFilter == key)
        {
            return;
        }

        _spaceFilter = key;
        ApplyFilters();
        OnPropertyChanged(nameof(SelectedSpaceFilter));
    }

    public void SetColumns(int columns)
    {
        columns = Math.Clamp(columns, 1, 8);
        if (columns == _columns)
        {
            return;
        }

        _columns = columns;
        BuildRows();
    }

    public void CloseDetail()
    {
        HasDetail = false;
        IsNewLogin = false;
        _snapshot = "";
        ClearFields();
        foreach (var view in Entries)
        {
            view.IsSelected = false;
        }
    }

    private void BuildRows()
    {
        var rows = new List<EntryRowView>((Entries.Count + _columns - 1) / _columns);
        for (var i = 0; i < Entries.Count; i += _columns)
        {
            rows.Add(new EntryRowView
            {
                Items = Entries.Skip(i).Take(_columns).ToList(),
                Columns = _columns
            });
        }

        EntryRows = rows;
        OnPropertyChanged(nameof(EntryRows));
    }

    public void SelectTag(string key)
    {
        if (_tagFilter == key)
        {
            return;
        }

        _tagFilter = key;
        ApplyFilters();
    }

    public void ChooseSpace(string name)
    {
        _editSpace = LabelName.Comparer.Equals(_editSpace, name) ? "" : name;
        BuildChoices();
    }

    public void ToggleTag(string name)
    {
        var index = _editTags.FindIndex(tag => LabelName.Comparer.Equals(tag, name));
        if (index >= 0)
        {
            _editTags.RemoveAt(index);
        }
        else
        {
            _editTags.Add(name);
        }

        BuildChoices();
    }

    public string? AddSpaceChoice()
    {
        var name = LabelName.Normalize(NewSpaceText);
        if (name.Length == 0)
        {
            return "请填写空间分类名称。";
        }

        name = Known(_session.Spaces, _pendingSpaces, name);
        _editSpace = name;
        NewSpaceText = "";
        BuildChoices();
        return null;
    }

    public string? AddTagChoice()
    {
        var name = LabelName.Normalize(NewTagText);
        if (name.Length == 0)
        {
            return "请填写账号标签名称。";
        }

        name = Known(_session.Tags, _pendingTags, name);
        if (!_editTags.Contains(name, LabelName.Comparer))
        {
            _editTags.Add(name);
        }

        NewTagText = "";
        BuildChoices();
        return null;
    }

    public void Refresh()
    {
        _all = _session.Entries.ToList();
        _spaces = _session.Spaces;
        _tags = _session.Tags;
        _views = _all
            .Select(entry => (Entry: entry, View: new EntryItemView
            {
                Id = entry.Id,
                Address = AddressOf(entry),
                Username = string.IsNullOrWhiteSpace(entry.Username) ? "未填写账号" : entry.Username,
                Labels = string.Join(" · ", entry.Tags)
            }))
            .OrderBy(pair => pair.View.Address, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.View.Username, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        _spaceFilter = Resolve(_spaceFilter, _spaces);
        var spaceCounts = new Dictionary<string, int>(LabelName.Comparer);
        var unassigned = 0;
        foreach (var entry in _all)
        {
            if (entry.Space.Length == 0)
            {
                unassigned++;
            }
            else
            {
                spaceCounts[entry.Space] = spaceCounts.GetValueOrDefault(entry.Space) + 1;
            }
        }

        var spaceRows = new List<(string Key, string Name, int Count)> { (AllKey, "全部空间", _all.Count) };
        spaceRows.AddRange(_spaces.Select(space => (space, space, spaceCounts.GetValueOrDefault(space))));
        if (unassigned > 0 || _spaceFilter == NoneKey)
        {
            spaceRows.Add((NoneKey, "未分类", unassigned));
        }

        _tagFilter = Resolve(_tagFilter, _tags);
        var tagCounts = new Dictionary<string, int>(LabelName.Comparer);
        var inSpace = 0;
        var untagged = 0;
        foreach (var entry in _all)
        {
            if (!MatchesSpace(entry))
            {
                continue;
            }

            inSpace++;
            if (entry.Tags.Count == 0)
            {
                untagged++;
            }

            foreach (var tag in entry.Tags)
            {
                tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
            }
        }

        var tagRows = new List<(string Key, string Name, int Count)> { (AllKey, "全部标签", inSpace) };
        tagRows.AddRange(_tags.Select(tag => (tag, tag, tagCounts.GetValueOrDefault(tag))));
        if (untagged > 0 || _tagFilter == NoneKey)
        {
            tagRows.Add((NoneKey, "无标签", untagged));
        }

        if (Sync(SpaceFilters, spaceRows, _spaceFilter) is { } spaces)
        {
            SpaceFilters = spaces;
            OnPropertyChanged(nameof(SpaceFilters));
            OnPropertyChanged(nameof(SelectedSpaceFilter));
        }

        if (Sync(TagFilters, tagRows, _tagFilter) is { } tags)
        {
            TagFilters = tags;
            OnPropertyChanged(nameof(TagFilters));
        }

        var search = Search.Trim();
        var selected = HasDetail ? _editingId : Guid.Empty;
        var entries = new List<EntryItemView>();
        foreach (var (entry, view) in _views)
        {
            if (!MatchesSpace(entry) || !MatchesTag(entry) || (search.Length > 0 && !MatchesSearch(entry, search)))
            {
                continue;
            }

            view.IsSelected = view.Id == selected;
            entries.Add(view);
        }

        if (!entries.SequenceEqual(Entries))
        {
            Entries = entries;
            OnPropertyChanged(nameof(Entries));
            BuildRows();
            OnPropertyChanged(nameof(EntryCountText));
            OnPropertyChanged(nameof(ShowEmpty));
            OnPropertyChanged(nameof(EmptyText));
        }
    }

    private static IReadOnlyList<FilterItemView>? Sync(
        IReadOnlyList<FilterItemView> current,
        List<(string Key, string Name, int Count)> rows,
        string selected)
    {
        if (current.Count == rows.Count && current.Select(item => item.Key).SequenceEqual(rows.Select(row => row.Key)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                current[i].Count = rows[i].Count;
                current[i].IsSelected = rows[i].Key == selected;
            }

            return null;
        }

        return rows
            .Select(row => new FilterItemView { Key = row.Key, Name = row.Name, Count = row.Count, IsSelected = row.Key == selected })
            .ToList();
    }

    private static bool MatchesSearch(VaultEntry entry, string needle) =>
        Contains(entry.Name, needle)
        || Contains(entry.Url, needle)
        || Contains(entry.Username, needle)
        || Contains(entry.Note, needle)
        || Contains(entry.Space, needle)
        || entry.Tags.Any(tag => Contains(tag, needle));

    private static bool Contains(string? value, string needle) =>
        !string.IsNullOrEmpty(value) && value.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Resolve(string filter, IReadOnlyList<string> names)
    {
        if (filter is AllKey or NoneKey)
        {
            return filter;
        }

        return names.FirstOrDefault(name => LabelName.Comparer.Equals(name, filter)) ?? AllKey;
    }

    private bool MatchesSpace(VaultEntry entry) => _spaceFilter switch
    {
        AllKey => true,
        NoneKey => entry.Space.Length == 0,
        _ => LabelName.Comparer.Equals(entry.Space, _spaceFilter)
    };

    private bool MatchesTag(VaultEntry entry) => _tagFilter switch
    {
        AllKey => true,
        NoneKey => entry.Tags.Count == 0,
        _ => entry.Tags.Contains(_tagFilter, LabelName.Comparer)
    };

    private static string AddressOf(VaultEntry entry)
    {
        var url = entry.Url.Trim();
        if (url.Length > 0)
        {
            var candidate = url.Contains("://", StringComparison.Ordinal) ? url : "https://" + url;
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            {
                var host = uri.Host;
                return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
            }

            return url;
        }

        return string.IsNullOrWhiteSpace(entry.Name) ? "未填写地址" : entry.Name.Trim();
    }

    private VaultEntry NewEntryForCurrentFilter() => new()
    {
        Id = Guid.NewGuid(),
        Name = "",
        Space = _spaceFilter is AllKey or NoneKey ? "" : _spaceFilter,
        Tags = _tagFilter is AllKey or NoneKey ? new List<string>() : new List<string> { _tagFilter }
    };

    private static string Known(IReadOnlyList<string> saved, List<string> pending, string name)
    {
        var existing = saved.Concat(pending).FirstOrDefault(item => LabelName.Comparer.Equals(item, name));
        if (existing is not null)
        {
            return existing;
        }

        pending.Add(name);
        return name;
    }

    private void BuildChoices()
    {
        var spaces = _session.Spaces.Concat(_pendingSpaces).Distinct(LabelName.Comparer).ToList();
        var tags = _session.Tags.Concat(_pendingTags).Distinct(LabelName.Comparer).ToList();
        SpaceChoices = spaces
            .Select(space => new LabelChipView { Name = space, IsSelected = LabelName.Comparer.Equals(space, _editSpace) })
            .ToList();
        TagChoices = tags
            .Select(tag => new LabelChipView { Name = tag, IsSelected = _editTags.Contains(tag, LabelName.Comparer) })
            .ToList();
        OnPropertyChanged(nameof(SpaceChoices));
        OnPropertyChanged(nameof(TagChoices));
        OnPropertyChanged(nameof(HasSpaceChoices));
        OnPropertyChanged(nameof(HasTagChoices));
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
        _editSpace = entry.Space;
        _editTags = entry.Tags.ToList();
        _pendingSpaces.Clear();
        _pendingTags.Clear();
        NewSpaceText = "";
        NewTagText = "";
        PasswordVisible = false;
        HasDetail = true;
        _snapshot = Snapshot();
        BuildChoices();
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
        _editSpace = "";
        _editTags = [];
        _pendingSpaces.Clear();
        _pendingTags.Clear();
        NewSpaceText = "";
        NewTagText = "";
        PasswordVisible = false;
        BuildChoices();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void ClearSelection()
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = false;
        }
    }

    private void MarkSelected(Guid id)
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = entry.Id == id;
        }
    }

    private string Snapshot() => string.Join(
        '\u001f',
        Name,
        Url,
        Username,
        Password,
        Note,
        TotpSecret,
        _editSpace,
        string.Join('\u001e', _editTags));
}
