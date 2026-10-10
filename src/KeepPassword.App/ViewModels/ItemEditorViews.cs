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

    public string MaskedText => $"已隐藏（{Value.Length} 个字符），点「显示」后可查看或编辑";

    public string RevealText => Revealed ? "隐藏" : "显示";

    public abstract string Label { get; }
}

public sealed class FieldEditorView : SecretFieldView
{
    public FieldEditorView(FieldSpec spec, string value, bool canGenerate, Action<SecretFieldView> changed)
        : base(value, spec.Sensitive, spec.Multiline, changed)
    {
        Spec = spec;
        CanGenerate = canGenerate;
    }

    public FieldSpec Spec { get; }

    public string Key => Spec.Key;

    public override string Label => Spec.Label;

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
