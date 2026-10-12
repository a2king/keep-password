using Avalonia.Media;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.ViewModels;

public abstract class SecretFieldView : ViewModelBase
{
    private readonly Action<SecretFieldView> _changed;
    private string _value;
    private bool _revealed;

    protected SecretFieldView(string value, bool sensitive, bool multiline, Action<SecretFieldView> changed)
    {
        _value = value;
        Sensitive = sensitive;
        Multiline = multiline;
        _changed = changed;
        _revealed = !sensitive || value.Length == 0;
    }

    public bool Sensitive { get; }

    public bool Multiline { get; }

    public TextWrapping Wrapping => Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap;

    public string Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value ?? ""))
            {
                OnPropertyChanged(nameof(MaskedText));
                _changed(this);
            }
        }
    }

    public bool Revealed
    {
        get => _revealed;
        set
        {
            if (Set(ref _revealed, value))
            {
                OnPropertyChanged(nameof(MaskChar));
                OnPropertyChanged(nameof(ShowEditor));
                OnPropertyChanged(nameof(ShowMasked));
                OnPropertyChanged(nameof(RevealText));
            }
        }
    }

    public char MaskChar => Sensitive && !Multiline && !Revealed ? '•' : '\0';

    public bool ShowEditor => !(Sensitive && Multiline && !Revealed);

    public bool ShowMasked => !ShowEditor;

    public string MaskedText => $"已隐藏（{Value.Length} 个字符），点眼睛图标后可查看或编辑";

    public string RevealText => Revealed ? "隐藏" : "显示";

    public abstract string Label { get; }
}

public sealed class FieldEditorView : SecretFieldView
{
    private string _label;

    public FieldEditorView(FieldSpec spec, string value, bool canGenerate, Action<SecretFieldView> changed)
        : base(value, spec.Sensitive, spec.Multiline, changed)
    {
        Spec = spec;
        CanGenerate = canGenerate;
        _label = spec.Label;
    }

    public FieldSpec Spec { get; }

    public string Key => Spec.Key;

    public override string Label => _label;

    public void Relabel(string label)
    {
        if (_label != label)
        {
            _label = label;
            OnPropertyChanged(nameof(Label));
        }
    }

    public string Placeholder => Spec.Placeholder;

    public bool CanGenerate { get; }

    public bool CanReveal => Sensitive;
}

public sealed class CustomFieldEditorView : SecretFieldView
{
    public CustomFieldEditorView(string name, string value, bool sensitive, Action<SecretFieldView> changed)
        : base(value, sensitive, multiline: false, changed)
    {
        Name = name;
    }

    public string Name { get; }

    public override string Label => Name;

    public string TypeText => Sensitive ? "敏感" : "文本";

    public VaultCustomField ToModel() => new() { Name = Name, Value = Value, Sensitive = Sensitive };
}

public sealed class AccountEditorView : ViewModelBase
{
    private readonly Action _changed;
    private string _note;
    private bool _showPassword = true;
    private bool _showAuth;
    private bool _useKey;
    private Guid? _sshKeyId;
    private IReadOnlyList<KeyOptionView> _keys = [];
    private bool _showTotp;
    private string _totpPreview = "";

    public AccountEditorView(VaultAccount account, bool canGenerate, Action changed)
    {
        Id = account.Id;
        _note = account.Note;
        _useKey = account.Auth == FieldKeys.AuthSshKey;
        _sshKeyId = account.SshKeyId;
        _showPassword = !_useKey;
        _changed = changed;
        UsernameField = new FieldEditorView(new FieldSpec(FieldKeys.Username, "用户名"), account.Username, false, _ => _changed());
        PasswordField = new FieldEditorView(new FieldSpec(FieldKeys.Password, "密码", Sensitive: true), account.Password, canGenerate, _ => _changed());
        TotpField = new FieldEditorView(
            new FieldSpec(FieldKeys.Totp, "验证码密钥", Sensitive: true, Placeholder: "Base32 密钥或 otpauth:// 链接"),
            account.TotpSecret ?? "",
            false,
            _ => { RefreshTotp(); _changed(); });
    }

    public Guid Id { get; }

    public FieldEditorView UsernameField { get; }

    public FieldEditorView PasswordField { get; }

    public FieldEditorView TotpField { get; }

    public string Note
    {
        get => _note;
        set
        {
            if (Set(ref _note, value ?? ""))
            {
                _changed();
            }
        }
    }

    public bool ShowPassword
    {
        get => _showPassword;
        private set => Set(ref _showPassword, value);
    }

    public bool ShowAuth
    {
        get => _showAuth;
        private set => Set(ref _showAuth, value);
    }

    public bool UseKeyAuth => _useKey;

    public bool UsePasswordAuth => !_useKey;

    public bool HasSshKeyOptions => _keys.Count > 0;

    public bool ShowKeyPicker => UseKeyAuth && HasSshKeyOptions;

    public bool ShowMissingKeyHint => UseKeyAuth && !HasSshKeyOptions;

    public IReadOnlyList<KeyOptionView> SshKeyOptions => _keys;

    public KeyOptionView? SelectedSshKey
    {
        get => _keys.FirstOrDefault(option => option.Id == _sshKeyId);
        set
        {
            var id = value?.Id;
            if (_sshKeyId == id)
            {
                return;
            }

            _sshKeyId = id;
            OnPropertyChanged();
            _changed();
        }
    }

    public void SetKeyAuth(bool useKey)
    {
        if (_useKey == useKey)
        {
            return;
        }

        _useKey = useKey;
        if (!useKey)
        {
            _sshKeyId = null;
        }

        ShowPassword = !useKey;
        NotifyAuth();
        _changed();
    }

    public void SetKeyOptions(IReadOnlyList<KeyOptionView> keys)
    {
        _keys = keys;
        OnPropertyChanged(nameof(SshKeyOptions));
        OnPropertyChanged(nameof(HasSshKeyOptions));
        OnPropertyChanged(nameof(ShowKeyPicker));
        OnPropertyChanged(nameof(ShowMissingKeyHint));
        OnPropertyChanged(nameof(SelectedSshKey));
    }

    public bool ShowTotp
    {
        get => _showTotp;
        private set => Set(ref _showTotp, value);
    }

    public string TotpPreview
    {
        get => _totpPreview;
        private set
        {
            if (Set(ref _totpPreview, value))
            {
                OnPropertyChanged(nameof(HasTotpPreview));
            }
        }
    }

    public bool HasTotpPreview => ShowTotp && TotpPreview.Length > 0;

    public bool IsBlank => ToModel().IsBlank;

    public string DisplayName => UsernameField.Value.Trim().Length > 0 ? UsernameField.Value.Trim() : "未填写用户名的账号";

    public void Configure(AccountLabels labels, bool showAuth, bool showTotp)
    {
        UsernameField.Relabel(labels.Username);
        PasswordField.Relabel(labels.Password);
        ShowAuth = showAuth;
        if (!showAuth && _useKey)
        {
            _useKey = false;
            _sshKeyId = null;
            ShowPassword = true;
            NotifyAuth();
        }

        ShowTotp = showTotp;
        RefreshTotp();
    }

    public void Clear()
    {
        UsernameField.Value = "";
        PasswordField.Value = "";
        TotpField.Value = "";
        Note = "";
        _useKey = false;
        _sshKeyId = null;
        ShowPassword = true;
        NotifyAuth();
    }

    public void RefreshTotp()
    {
        var secret = TotpField.Value;
        if (!ShowTotp || string.IsNullOrWhiteSpace(secret))
        {
            TotpPreview = "";
            OnPropertyChanged(nameof(HasTotpPreview));
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TotpPreview = TotpGenerator.TryGenerateFromBase32(secret, now, out var code)
            ? $"{code}    剩余 {TotpGenerator.RemainingSeconds(now)} 秒"
            : "密钥无法识别";
        OnPropertyChanged(nameof(HasTotpPreview));
    }

    public VaultAccount ToModel() => new()
    {
        Id = Id,
        Username = UsernameField.Value,
        Password = ShowPassword ? PasswordField.Value : "",
        Note = Note,
        TotpSecret = ShowTotp && !string.IsNullOrWhiteSpace(TotpField.Value) ? TotpField.Value.Trim() : null,
        Auth = ShowAuth && _useKey ? FieldKeys.AuthSshKey : "",
        SshKeyId = ShowAuth && _useKey ? _sshKeyId : null
    };

    private void NotifyAuth()
    {
        OnPropertyChanged(nameof(UseKeyAuth));
        OnPropertyChanged(nameof(UsePasswordAuth));
        OnPropertyChanged(nameof(ShowKeyPicker));
        OnPropertyChanged(nameof(ShowMissingKeyHint));
        OnPropertyChanged(nameof(SelectedSshKey));
    }
}

public sealed class NodeEditorView : ViewModelBase
{
    private readonly Action _changed;
    private string _name;
    private string _role;
    private string _host;
    private string _port;
    private string _service;
    private int _index;

    public NodeEditorView(ClusterNode node, int index, Action changed)
    {
        _name = node.Name;
        _role = node.Role;
        _host = node.Host;
        _port = node.Port;
        _service = node.Service;
        _index = index;
        _changed = changed;
    }

    public string Title => $"节点 {_index}";

    public int Index
    {
        get => _index;
        set
        {
            if (Set(ref _index, value))
            {
                OnPropertyChanged(nameof(Title));
            }
        }
    }

    public string Name
    {
        get => _name;
        set => Update(ref _name, value);
    }

    public string Role
    {
        get => _role;
        set => Update(ref _role, value);
    }

    public string Host
    {
        get => _host;
        set => Update(ref _host, value);
    }

    public string Port
    {
        get => _port;
        set => Update(ref _port, value);
    }

    public string Service
    {
        get => _service;
        set => Update(ref _service, value);
    }

    public string Address => Port.Trim().Length == 0 ? Host.Trim() : $"{Host.Trim()}:{Port.Trim()}";

    public ClusterNode ToModel() => new()
    {
        Name = Name.Trim(),
        Role = Role.Trim(),
        Host = Host.Trim(),
        Port = Port.Trim(),
        Service = Service.Trim()
    };

    private void Update(ref string field, string? value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Set(ref field, value ?? "", name))
        {
            OnPropertyChanged(nameof(Address));
            _changed();
        }
    }
}

public sealed class KeyOptionView
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required string Detail { get; init; }
}
