namespace KeepPassword.Core.Update;

public sealed record UpdateRelease(
    string TagName,
    string Version,
    string SetupDownloadUrl,
    string Sha256DownloadUrl);

public sealed record UpdateCheckResult(
    string CurrentVersion,
    UpdateRelease? Latest,
    bool UpdateAvailable,
    string? Error);

public sealed record UpdateDownloadResult(string SetupPath, string Sha256Hex);
