namespace KeepPassword.Core.Vault;

public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public VaultItemKind Kind { get; set; } = VaultItemKind.Login;

    public bool Favorite { get; set; }

    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    public string Note { get; set; } = "";

    public List<VaultAccount> Accounts { get; set; } = [];

    public string Space { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    public Dictionary<string, string> Fields { get; set; } = new(StringComparer.Ordinal);

    public List<VaultCustomField> CustomFields { get; set; } = [];

    public Guid? SshKeyId { get; set; }

    public List<ClusterNode> Nodes { get; set; } = [];

    /// <summary>条目最后修改时间（UTC）。同步合并时以较新者为准。</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>非空表示墓碑：条目已删除，仍参与同步合并。</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;

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

    public void Touch(DateTimeOffset? at = null) => UpdatedAt = at ?? DateTimeOffset.UtcNow;

    public void MarkDeleted(DateTimeOffset? at = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        DeletedAt = when;
        UpdatedAt = when;
        Name = "";
        Url = "";
        Note = "";
        Favorite = false;
        Accounts.Clear();
        Space = "";
        Tags.Clear();
        Fields.Clear();
        CustomFields.Clear();
        SshKeyId = null;
        Nodes.Clear();
    }

    public VaultEntry Clone() => new()
    {
        Id = Id,
        Kind = Kind,
        Favorite = Favorite,
        Name = Name,
        Url = Url,
        Note = Note,
        Accounts = Accounts.Select(account => account.Clone()).ToList(),
        Space = Space,
        Tags = Tags.ToList(),
        Fields = new Dictionary<string, string>(Fields, StringComparer.Ordinal),
        CustomFields = CustomFields.Select(field => field.Clone()).ToList(),
        SshKeyId = SshKeyId,
        Nodes = Nodes.Select(node => node.Clone()).ToList(),
        UpdatedAt = UpdatedAt,
        DeletedAt = DeletedAt
    };
}

public sealed class VaultAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Note { get; set; } = "";

    public string? TotpSecret { get; set; }

    /// <summary>空表示密码认证。SSH 密钥认证只用于支持密钥的服务器账号和集群账号。</summary>
    public string Auth { get; set; } = "";

    public Guid? SshKeyId { get; set; }

    public bool IsBlank => Username.Length == 0 && Password.Length == 0 && Note.Length == 0 && string.IsNullOrWhiteSpace(TotpSecret)
        && SshKeyId is null && Auth != FieldKeys.AuthSshKey;

    public VaultAccount Clone() => new()
    {
        Id = Id,
        Username = Username,
        Password = Password,
        Note = Note,
        TotpSecret = TotpSecret,
        Auth = Auth,
        SshKeyId = SshKeyId
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
