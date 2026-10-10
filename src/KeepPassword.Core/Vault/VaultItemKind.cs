namespace KeepPassword.Core.Vault;

public enum VaultItemKind
{
    Login,
    Server,
    Database,
    SshKey,
    ApiCredential,
    Cluster
}

public static class VaultItemKinds
{
    public static IReadOnlyList<VaultItemKind> All { get; } =
    [
        VaultItemKind.Login,
        VaultItemKind.Server,
        VaultItemKind.Database,
        VaultItemKind.SshKey,
        VaultItemKind.ApiCredential,
        VaultItemKind.Cluster
    ];

    public static string Code(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Server => "server",
        VaultItemKind.Database => "database",
        VaultItemKind.SshKey => "sshKey",
        VaultItemKind.ApiCredential => "api",
        VaultItemKind.Cluster => "cluster",
        _ => "login"
    };

    public static VaultItemKind Parse(string? code) => code switch
    {
        "server" => VaultItemKind.Server,
        "database" => VaultItemKind.Database,
        "sshKey" => VaultItemKind.SshKey,
        "api" => VaultItemKind.ApiCredential,
        "cluster" => VaultItemKind.Cluster,
        _ => VaultItemKind.Login
    };

    public static string DisplayName(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Server => "服务器",
        VaultItemKind.Database => "数据库",
        VaultItemKind.SshKey => "SSH 密钥",
        VaultItemKind.ApiCredential => "API 凭据",
        VaultItemKind.Cluster => "集群服务",
        _ => "登录"
    };

    public static string Badge(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Server => "SRV",
        VaultItemKind.Database => "DB",
        VaultItemKind.SshKey => "KEY",
        VaultItemKind.ApiCredential => "API",
        VaultItemKind.Cluster => "CLU",
        _ => "WEB"
    };

    public static bool UsesSshKey(VaultItemKind kind) => kind is VaultItemKind.Server or VaultItemKind.Cluster;
}
