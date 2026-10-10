namespace KeepPassword.Core.Vault;

public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public VaultItemKind Kind { get; set; } = VaultItemKind.Login;

    public bool Favorite { get; set; }

    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Note { get; set; } = "";

    public string? TotpSecret { get; set; }

    public string Space { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    public List<VaultCustomField> CustomFields { get; set; } = [];

    public Guid? SshKeyId { get; set; }

    public List<ClusterNode> Nodes { get; set; } = [];

    public string Field(string key) => Fields.TryGetValue(key, out var value) ? value : "";

    public void SetField(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Fields.Remove(key);
        }
        else
        {
            Fields[key] = value;
        }
    }

    public VaultEntry Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Favorite = Favorite,
        Name = Name,
        Url = Url,
        Username = Username,
        Password = Password,
        Note = Note,
        TotpSecret = TotpSecret,
        Space = Space,
        Tags = Tags.ToList(),
        Fields = new Dictionary<string, string>(Fields, StringComparer.Ordinal),
        CustomFields = CustomFields.Select(field => field.Clone()).ToList(),
        SshKeyId = SshKeyId,
        Nodes = Nodes.Select(node => node.Clone()).ToList()
    };
}

public sealed class VaultCustomField
{
    public const int MaxNameLength = 40;

    public string Name { get; set; } = "";

    public string Value { get; set; } = "";

    public bool Sensitive { get; set; }

    public VaultCustomField Clone() => new() { Name = Name, Value = Value, Sensitive = Sensitive };
}

public sealed class ClusterNode
{
    public string Name { get; set; } = "";

    public string Role { get; set; } = "";

    public string Host { get; set; } = "";

    public string Port { get; set; } = "";

    public string Service { get; set; } = "";

    public ClusterNode Clone() => new() { Name = Name, Role = Role, Host = Host, Port = Port, Service = Service };
}
