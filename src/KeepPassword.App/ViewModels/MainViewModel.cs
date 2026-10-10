using System.Collections.ObjectModel;
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

    public string KindBadge { get; init; } = "";

    public bool Favorite { get; init; }

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
    public const string FavoriteKey = "\u0001fav";

    private readonly VaultSession _session;
    private readonly List<string> _pendingSpaces = [];
    private readonly List<string> _pendingTags = [];
    private List<VaultEntry> _all = [];
    private IReadOnlyList<string> _spaces = [];
    private IReadOnlyList<string> _tags = [];
    private List<(VaultEntry Entry, EntryItemView View)> _views = [];
    private List<Guid> _lastImport = [];
    private string _spaceFilter = AllKey;
    private string _tagFilter = AllKey;
    private string _kindFilter = AllKey;
    private string _editSpace = "";
    private List<string> _editTags = [];
    private string _newSpaceText = "";
    private string _newTagText = "";
    private string _newFieldName = "";
    private bool _newFieldSensitive;
    private string _search = "";
    private VaultEntry _draft = new();
    private string _name = "";
    private string _note = "";
    private string _status = "";
    private string _totpPreview = "";
    private string _snapshot = "";
    private bool _hasDetail;
    private bool _isNew;
    private int _columns = 3;
    private SshKeyInspection _sshInspection = new(null, null);

    public MainViewModel(VaultSession session, bool platformAutofillSupported)
    {
        _session = session;
        PlatformAutofillSupported = platformAutofillSupported;
        Refresh();
    }

    public string Account => _session.Account;

    public bool PlatformAutofillSupported { get; }

    public string AutofillHint => PlatformAutofillSupported
        ? "确认短密钥后，Windows 程序通过模拟键盘输入（仅登录类型）。浏览器使用扩展按钮或 Ctrl+Shift+L。空闲 5 分钟、锁屏或休眠时自动锁定。"
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

    public IReadOnlyList<FilterItemView> KindFilters { get; private set; } = [];

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

    public string EntryCountText => $"{Entries.Count} 个条目";

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
        ? "还没有条目。点「+ 新建」选择类型，或导入 CSV。"
        : "当前筛选条件下没有匹配的条目。";

    public bool CanUndoImport => _lastImport.Count > 0;

    public string UndoImportText => $"撤销导入（{_lastImport.Count} 条）";

    public bool IsNewItem
    {
        get => _isNew;
        private set => Set(ref _isNew, value);
    }

    public VaultItemKind Kind => _draft.Kind;

    public string KindName => VaultItemKinds.DisplayName(_draft.Kind);

    public string KindBadge => VaultItemKinds.Badge(_draft.Kind);

    public string Name
    {
        get => _name;
        set => Set(ref _name, value);
    }

    public string Note
    {
        get => _note;
        set => Set(ref _note, value);
    }

    public bool Favorite => _draft.Favorite;

    public string FavoriteText => _draft.Favorite ? "★ 已收藏" : "☆ 收藏";

    public IReadOnlyList<FieldEditorView> PresetFields { get; private set; } = [];

    public ObservableCollection<CustomFieldEditorView> CustomFields { get; } = [];

    public ObservableCollection<NodeEditorView> Nodes { get; } = [];

    public string NewFieldName
    {
        get => _newFieldName;
        set => Set(ref _newFieldName, value);
    }

    public bool NewFieldSensitive
    {
        get => _newFieldSensitive;
        set => Set(ref _newFieldSensitive, value);
    }

    public bool HasCustomFields => CustomFields.Count > 0;

    public IReadOnlyList<ServerProtocol> Protocols => ItemTemplates.Protocols;

    public bool ShowProtocol => _draft.Kind == VaultItemKind.Server;

    public ServerProtocol? SelectedProtocol
    {
        get => ShowProtocol ? ItemTemplates.Protocol(_draft.Field(FieldKeys.Protocol)) : null;
        set
        {
            if (value is null || !ShowProtocol)
            {
                return;
            }

            var previous = ItemTemplates.Protocol(_draft.Field(FieldKeys.Protocol));
            if (previous.Code == value.Code)
            {
                return;
            }

            var port = _draft.Field(FieldKeys.Port).Trim();
            if (port.Length == 0 || port == previous.DefaultPort)
            {
                _draft.SetField(FieldKeys.Port, value.DefaultPort);
            }

            _draft.SetField(FieldKeys.Protocol, value.Code);
            if (!value.SupportsKey)
            {
                _draft.SetField(FieldKeys.Auth, FieldKeys.AuthPassword);
            }

            RebuildPresetFields();
        }
    }

    public IReadOnlyList<DatabaseDriver> Drivers => ItemTemplates.Drivers;

    public bool ShowDriver => _draft.Kind == VaultItemKind.Database;

    public DatabaseDriver? SelectedDriver
    {
        get => ShowDriver ? ItemTemplates.Driver(_draft.Field(FieldKeys.Driver)) : null;
        set
        {
            if (value is null || !ShowDriver)
            {
                return;
            }

            var previous = ItemTemplates.Driver(_draft.Field(FieldKeys.Driver));
            if (previous.Code == value.Code)
            {
                return;
            }

            var port = _draft.Field(FieldKeys.Port).Trim();
            if (port.Length == 0 || port == previous.DefaultPort)
            {
                _draft.SetField(FieldKeys.Port, value.DefaultPort);
            }

            _draft.SetField(FieldKeys.Driver, value.Code);
            RebuildPresetFields();
        }
    }

    public bool ShowAuth => _draft.Kind == VaultItemKind.Cluster
        || (_draft.Kind == VaultItemKind.Server && ItemTemplates.Protocol(_draft.Field(FieldKeys.Protocol)).SupportsKey);

    public bool UseKeyAuth => _draft.Field(FieldKeys.Auth) == FieldKeys.AuthSshKey;

    public bool UsePasswordAuth => !UseKeyAuth;

    public bool ShowKeyPicker => ItemTemplates.UsesKeyAuth(_draft);

    public IReadOnlyList<KeyOptionView> SshKeyOptions { get; private set; } = [];

    public bool HasSshKeyOptions => SshKeyOptions.Count > 0;

    public KeyOptionView? SelectedSshKey
    {
        get => SshKeyOptions.FirstOrDefault(option => option.Id == _draft.SshKeyId);
        set
        {
            var id = value?.Id;
            if (_draft.SshKeyId == id)
            {
                return;
            }

            _draft.SshKeyId = id;
            OnPropertyChanged();
        }
    }

    public bool ShowConnection => _draft.Kind == VaultItemKind.Database;

    public string ConnectionPreview
    {
        get
        {
            if (_draft.Field(FieldKeys.Connection).Trim().Length > 0)
            {
                return "使用上方填写的自定义连接字符串";
            }

            var masked = _draft.Clone();
            if (masked.Password.Length > 0)
            {
                masked.Password = "******";
            }

            var text = ItemTemplates.ConnectionString(masked);
            return text.Length == 0 ? "填写主机等字段后自动生成" : text;
        }
    }

    public string ConnectionString => ItemTemplates.ConnectionString(_draft);

    public bool ShowSshInfo => _draft.Kind == VaultItemKind.SshKey;

    public string SshKeyType => _sshInspection.Details?.KeyType ?? "未识别";

    public string SshFingerprint => _sshInspection.Details?.Fingerprint ?? "—";

    public string SshWarning => _sshInspection.Warning ?? "";

    public bool HasSshWarning => _sshInspection.Warning is not null;

    public string SshReferences
    {
        get
        {
            if (!ShowSshInfo || IsNewItem)
            {
                return "";
            }

            var names = _session.ReferencingNames(_draft.Id);
            return names.Count == 0 ? "尚未被任何条目引用。" : $"被 {names.Count} 个条目引用：{string.Join("、", names)}";
        }
    }

    public bool ShowNodes => _draft.Kind == VaultItemKind.Cluster;

    public bool ShowTotp => _draft.Kind == VaultItemKind.Login;

    public string Status
    {
        get => _status;
        set => Set(ref _status, value);
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

        IsNewItem = false;
        Apply(entry);
        MarkSelected(id);
        Status = "";
    }

    public void NewItem(VaultItemKind kind)
    {
        var entry = NewEntryForCurrentFilter();
        entry.Kind = kind;
        ItemTemplates.ApplyDefaults(entry);
        if (kind == VaultItemKind.Cluster)
        {
            entry.Nodes.Add(new ClusterNode());
        }

        IsNewItem = true;
        Apply(entry);
        ClearSelection();
        Status = kind switch
        {
            VaultItemKind.Login => "新的登录条目。需要密码时点「生成」，默认是 16 位字母和数字。",
            VaultItemKind.SshKey => "粘贴私钥和公钥内容，保存后只写入加密保险库。",
            VaultItemKind.Cluster => "集群内所有节点共用同一套账号和密码 / 密钥。",
            _ => $"新的{VaultItemKinds.DisplayName(kind)}条目。条目类型保存后不能修改。"
        };
    }

    public void NewTotp()
    {
        NewItem(VaultItemKind.Login);
        Status = "独立验证码只需填写名称和验证码密钥。";
    }

    public static string GeneratePassword() => PasswordGenerator.Generate(PasswordGeneratorOptions.DefaultLogin);

    public void SetKeyAuth(bool useKey)
    {
        var value = useKey ? FieldKeys.AuthSshKey : FieldKeys.AuthPassword;
        if (_draft.Field(FieldKeys.Auth) == value)
        {
            return;
        }

        _draft.SetField(FieldKeys.Auth, value);
        if (!useKey)
        {
            _draft.SshKeyId = null;
        }

        RebuildPresetFields();
    }

    public string? ToggleFavorite()
    {
        if (!HasDetail)
        {
            return null;
        }

        _draft.Favorite = !_draft.Favorite;
        OnPropertyChanged(nameof(Favorite));
        OnPropertyChanged(nameof(FavoriteText));
        if (IsNewItem)
        {
            return null;
        }

        var stored = _session.Entries.FirstOrDefault(entry => entry.Id == _draft.Id);
        if (stored is null)
        {
            return null;
        }

        stored.Favorite = _draft.Favorite;
        try
        {
            _session.Upsert(stored);
            _session.Save();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }

        Refresh();
        MarkSelected(_draft.Id);
        return null;
    }

    public string? AddCustomField()
    {
        var name = NewFieldName.Trim();
        if (name.Length == 0)
        {
            return "请填写字段名称。";
        }

        if (name.Length > VaultCustomField.MaxNameLength)
        {
            return $"字段名称最多 {VaultCustomField.MaxNameLength} 个字符。";
        }

        if (CustomFields.Any(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return $"字段「{name}」已存在。";
        }

        if (CustomFields.Count >= VaultItemRules.MaxCustomFields)
        {
            return $"自定义字段最多 {VaultItemRules.MaxCustomFields} 个。";
        }

        CustomFields.Add(new CustomFieldEditorView(name, "", NewFieldSensitive, OnFieldChanged));
        NewFieldName = "";
        NewFieldSensitive = false;
        OnPropertyChanged(nameof(HasCustomFields));
        return null;
    }

    public void RemoveCustomField(CustomFieldEditorView field)
    {
        CustomFields.Remove(field);
        OnPropertyChanged(nameof(HasCustomFields));
    }

    public void AddNode()
    {
        if (Nodes.Count < VaultItemRules.MaxNodes)
        {
            Nodes.Add(new NodeEditorView(new ClusterNode(), Nodes.Count + 1, () => { }));
        }
    }

    public void RemoveNode(NodeEditorView node)
    {
        Nodes.Remove(node);
        for (var i = 0; i < Nodes.Count; i++)
        {
            Nodes[i].Index = i + 1;
        }
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

        var entry = BuildEntry();
        try
        {
            _session.Upsert(entry);
            _session.Save();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return ex.Message;
        }

        _pendingSpaces.Clear();
        _pendingTags.Clear();
        IsNewItem = false;
        Refresh();
        var saved = _session.Entries.First(item => item.Id == entry.Id);
        Apply(saved);
        MarkSelected(entry.Id);
        Status = "已保存。";
        return null;
    }

    public IReadOnlyList<string> ReferencingNames() =>
        HasDetail && !IsNewItem && _draft.Kind == VaultItemKind.SshKey ? _session.ReferencingNames(_draft.Id) : [];

    public bool Delete()
    {
        if (!HasDetail)
        {
            return false;
        }

        var existed = _session.Entries.Any(entry => entry.Id == _draft.Id);
        if (existed)
        {
            _session.Remove(_draft.Id);
            _session.Save();
        }

        _lastImport.Remove(_draft.Id);
        CloseDetail();
        Refresh();
        Status = "已删除。";
        return true;
    }

    public int ApplyImport(IReadOnlyList<VaultEntry> entries)
    {
        var ids = new List<Guid>();
        foreach (var entry in entries)
        {
            _session.Upsert(entry);
            ids.Add(entry.Id);
        }

        if (ids.Count > 0)
        {
            _session.Save();
        }

        _lastImport = ids;
        OnImportChanged();
        Refresh();
        Status = ids.Count == 0 ? "没有可导入的行。" : $"已导入 {ids.Count} 条。可在本次会话内撤销。";
        return ids.Count;
    }

    public int UndoImport()
    {
        if (_lastImport.Count == 0)
        {
            return 0;
        }

        var removed = _lastImport.Count(id => _session.Remove(id));
        _session.Save();
        if (HasDetail && _lastImport.Contains(_draft.Id))
        {
            CloseDetail();
        }

        _lastImport = [];
        OnImportChanged();
        Refresh();
        Status = $"已撤销导入，移除 {removed} 条。";
        return removed;
    }

    public IReadOnlyList<AuditFinding> Audit() => VaultAudit.Analyze(_session.Entries);

    public async Task<IReadOnlyList<BreachFinding>> CheckBreachesAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HibpRangeClient();
        return await BreachChecker.CheckAsync(_session.Entries, client, cancellationToken);
    }

    public void Reload()
    {
        CloseDetail();
        _lastImport = [];
        OnImportChanged();
        Refresh();
        Status = "保险库已更新。";
    }

    public void RefreshTotp()
    {
        var secret = _draft.TotpSecret;
        if (!HasDetail || !ShowTotp || string.IsNullOrWhiteSpace(secret))
        {
            TotpPreview = "";
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!TotpGenerator.TryGenerateFromBase32(secret, now, out var code))
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

    public void SelectTag(string key)
    {
        if (_tagFilter == key)
        {
            return;
        }

        _tagFilter = key;
        ApplyFilters();
    }

    public void SelectKind(string key)
    {
        if (_kindFilter == key)
        {
            return;
        }

        _kindFilter = key;
        ApplyFilters();
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
        IsNewItem = false;
        _snapshot = "";
        ClearFields();
        ClearSelection();
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
            .Select(entry => (Entry: entry, View: CreateView(entry)))
            .OrderBy(pair => pair.View.Address, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(pair => pair.View.Username, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        SshKeyOptions = _all
            .Where(entry => entry.Kind == VaultItemKind.SshKey)
            .OrderBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(entry => new KeyOptionView
            {
                Id = entry.Id,
                Name = string.IsNullOrWhiteSpace(entry.Name) ? "未命名密钥" : entry.Name,
                Detail = string.Join(" · ", new[] { entry.Field(FieldKeys.KeyType), entry.Field(FieldKeys.Fingerprint) }.Where(text => text.Length > 0))
            })
            .ToList();
        OnPropertyChanged(nameof(SshKeyOptions));
        OnPropertyChanged(nameof(HasSshKeyOptions));
        OnPropertyChanged(nameof(SelectedSshKey));
        ApplyFilters();
    }

    private static EntryItemView CreateView(VaultEntry entry)
    {
        var login = entry.Kind == VaultItemKind.Login;
        var title = login ? AddressOf(entry) : (string.IsNullOrWhiteSpace(entry.Name) ? VaultItemKinds.DisplayName(entry.Kind) : entry.Name.Trim());
        var summary = ItemTemplates.Summary(entry);
        string subtitle;
        if (login)
        {
            subtitle = string.IsNullOrWhiteSpace(entry.Username) ? "未填写账号" : entry.Username;
        }
        else
        {
            var parts = new[] { summary, entry.Username }.Where(text => !string.IsNullOrWhiteSpace(text)).ToList();
            subtitle = parts.Count == 0 ? VaultItemKinds.DisplayName(entry.Kind) : string.Join(" · ", parts);
        }

        return new EntryItemView
        {
            Id = entry.Id,
            Address = title,
            Username = subtitle,
            KindBadge = VaultItemKinds.Badge(entry.Kind),
            Favorite = entry.Favorite,
            Labels = string.Join(" · ", entry.Tags)
        };
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
        var kindCounts = new Dictionary<VaultItemKind, int>();
        var inSpaceKind = 0;
        var untagged = 0;
        var inSpaceTag = 0;
        var favorites = 0;
        foreach (var entry in _all)
        {
            if (!MatchesSpace(entry))
            {
                continue;
            }

            if (MatchesKind(entry))
            {
                inSpaceKind++;
                if (entry.Tags.Count == 0)
                {
                    untagged++;
                }

                foreach (var tag in entry.Tags)
                {
                    tagCounts[tag] = tagCounts.GetValueOrDefault(tag) + 1;
                }
            }

            if (MatchesTag(entry))
            {
                inSpaceTag++;
                kindCounts[entry.Kind] = kindCounts.GetValueOrDefault(entry.Kind) + 1;
                if (entry.Favorite)
                {
                    favorites++;
                }
            }
        }

        var tagRows = new List<(string Key, string Name, int Count)> { (AllKey, "全部标签", inSpaceKind) };
        tagRows.AddRange(_tags.Select(tag => (tag, tag, tagCounts.GetValueOrDefault(tag))));
        if (untagged > 0 || _tagFilter == NoneKey)
        {
            tagRows.Add((NoneKey, "无标签", untagged));
        }

        var kindRows = new List<(string Key, string Name, int Count)>
        {
            (AllKey, "全部类型", inSpaceTag),
            (FavoriteKey, "★ 收藏", favorites)
        };
        kindRows.AddRange(VaultItemKinds.All.Select(kind => (VaultItemKinds.Code(kind), VaultItemKinds.DisplayName(kind), kindCounts.GetValueOrDefault(kind))));

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

        if (Sync(KindFilters, kindRows, _kindFilter) is { } kinds)
        {
            KindFilters = kinds;
            OnPropertyChanged(nameof(KindFilters));
        }

        var search = Search.Trim();
        var selected = HasDetail ? _draft.Id : Guid.Empty;
        var entries = new List<EntryItemView>();
        foreach (var (entry, view) in _views)
        {
            if (!MatchesSpace(entry) || !MatchesTag(entry) || !MatchesKind(entry) || (search.Length > 0 && !EntrySearch.Matches(entry, search)))
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

    private bool MatchesKind(VaultEntry entry) => _kindFilter switch
    {
        AllKey => true,
        FavoriteKey => entry.Favorite,
        _ => VaultItemKinds.Code(entry.Kind) == _kindFilter
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
        Tags = _tagFilter is AllKey or NoneKey ? new List<string>() : new List<string> { _tagFilter },
        Favorite = _kindFilter == FavoriteKey
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
        var unlocked = _session.IsUnlocked;
        var spaces = (unlocked ? _session.Spaces : []).Concat(_pendingSpaces).Distinct(LabelName.Comparer).ToList();
        var tags = (unlocked ? _session.Tags : []).Concat(_pendingTags).Distinct(LabelName.Comparer).ToList();
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
        _draft = entry.Clone();
        Name = entry.Name;
        Note = entry.Note;
        _editSpace = entry.Space;
        _editTags = entry.Tags.ToList();
        _pendingSpaces.Clear();
        _pendingTags.Clear();
        NewSpaceText = "";
        NewTagText = "";
        NewFieldName = "";
        NewFieldSensitive = false;
        CustomFields.Clear();
        foreach (var field in entry.CustomFields)
        {
            CustomFields.Add(new CustomFieldEditorView(field.Name, field.Value, field.Sensitive, OnFieldChanged));
        }

        Nodes.Clear();
        for (var i = 0; i < entry.Nodes.Count; i++)
        {
            Nodes.Add(new NodeEditorView(entry.Nodes[i], i + 1, () => { }));
        }

        HasDetail = true;
        RebuildPresetFields();
        _snapshot = Snapshot();
        BuildChoices();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void RebuildPresetFields()
    {
        var canGenerate = IsNewItem;
        PresetFields = ItemTemplates.Fields(_draft)
            .Select(spec => new FieldEditorView(
                spec,
                FieldKeys.Read(_draft, spec.Key),
                canGenerate && spec.Key == FieldKeys.Password,
                OnFieldChanged))
            .ToList();
        UpdateSshInspection();
        OnPropertyChanged(nameof(PresetFields));
        OnPropertyChanged(nameof(Kind));
        OnPropertyChanged(nameof(KindName));
        OnPropertyChanged(nameof(KindBadge));
        OnPropertyChanged(nameof(Favorite));
        OnPropertyChanged(nameof(FavoriteText));
        OnPropertyChanged(nameof(ShowProtocol));
        OnPropertyChanged(nameof(SelectedProtocol));
        OnPropertyChanged(nameof(ShowDriver));
        OnPropertyChanged(nameof(SelectedDriver));
        OnPropertyChanged(nameof(ShowAuth));
        OnPropertyChanged(nameof(UseKeyAuth));
        OnPropertyChanged(nameof(UsePasswordAuth));
        OnPropertyChanged(nameof(ShowKeyPicker));
        OnPropertyChanged(nameof(SelectedSshKey));
        OnPropertyChanged(nameof(ShowConnection));
        OnPropertyChanged(nameof(ConnectionPreview));
        OnPropertyChanged(nameof(ShowSshInfo));
        OnPropertyChanged(nameof(SshReferences));
        OnPropertyChanged(nameof(ShowNodes));
        OnPropertyChanged(nameof(ShowTotp));
        OnPropertyChanged(nameof(HasCustomFields));
        RefreshTotp();
    }

    private void OnFieldChanged(SecretFieldView field)
    {
        if (field is not FieldEditorView preset)
        {
            return;
        }

        FieldKeys.Write(_draft, preset.Key, preset.Value);
        switch (preset.Key)
        {
            case FieldKeys.PrivateKey or FieldKeys.PublicKey:
                UpdateSshInspection();
                break;
            case FieldKeys.Totp:
                RefreshTotp();
                break;
        }

        if (ShowConnection)
        {
            OnPropertyChanged(nameof(ConnectionPreview));
        }
    }

    private void UpdateSshInspection()
    {
        _sshInspection = _draft.Kind == VaultItemKind.SshKey
            ? SshKeyInfo.Inspect(_draft.Field(FieldKeys.PrivateKey), _draft.Field(FieldKeys.PublicKey))
            : new SshKeyInspection(null, null);
        OnPropertyChanged(nameof(SshKeyType));
        OnPropertyChanged(nameof(SshFingerprint));
        OnPropertyChanged(nameof(SshWarning));
        OnPropertyChanged(nameof(HasSshWarning));
    }

    private VaultEntry BuildEntry()
    {
        var entry = _draft.Clone();
        entry.Name = Name.Trim();
        entry.Note = Note;
        entry.Space = _editSpace;
        entry.Tags = _editTags.ToList();
        entry.CustomFields = CustomFields.Select(field => field.ToModel()).ToList();
        entry.Nodes = Nodes.Select(node => node.ToModel()).ToList();
        return entry;
    }

    private void ClearFields()
    {
        _draft = new VaultEntry();
        Name = "";
        Note = "";
        TotpPreview = "";
        _editSpace = "";
        _editTags = [];
        _pendingSpaces.Clear();
        _pendingTags.Clear();
        NewSpaceText = "";
        NewTagText = "";
        NewFieldName = "";
        CustomFields.Clear();
        Nodes.Clear();
        RebuildPresetFields();
        BuildChoices();
        OnPropertyChanged(nameof(ShowEmpty));
    }

    private void OnImportChanged()
    {
        OnPropertyChanged(nameof(CanUndoImport));
        OnPropertyChanged(nameof(UndoImportText));
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

    private string Snapshot()
    {
        var entry = BuildEntry();
        return string.Join(
            '\u001f',
            entry.Name,
            entry.Url,
            entry.Username,
            entry.Password,
            entry.Note,
            entry.TotpSecret ?? "",
            entry.Space,
            string.Join('\u001e', entry.Tags),
            string.Join('\u001e', entry.Fields.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)),
            string.Join('\u001e', entry.CustomFields.Select(field => field.Name + "=" + field.Value)),
            entry.SshKeyId?.ToString() ?? "",
            string.Join('\u001e', entry.Nodes.Select(node => string.Join('\u001d', node.Name, node.Role, node.Host, node.Port, node.Service))));
    }
}
