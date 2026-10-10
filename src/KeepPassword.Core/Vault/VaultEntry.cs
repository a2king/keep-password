namespace KeepPassword.Core.Vault;

public sealed class VaultEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string Url { get; set; } = "";

    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    public string Note { get; set; } = "";

    public string? TotpSecret { get; set; }

    public string Space { get; set; } = "";

    public List<string> Tags { get; set; } = [];

    public VaultEntry Clone() => new()
    {
        Id = Id,
        Name = Name,
        Url = Url,
        Username = Username,
        Password = Password,
        Note = Note,
        TotpSecret = TotpSecret,
        Space = Space,
        Tags = Tags.ToList()
    };
}
