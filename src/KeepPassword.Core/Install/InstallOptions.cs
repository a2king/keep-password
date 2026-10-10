namespace KeepPassword.Core.Install;

public sealed class InstallOptions
{
    public bool CreateStartMenuShortcut { get; init; } = true;

    public bool CreateDesktopShortcut { get; init; } = true;

    public bool LaunchAfterInstall { get; init; } = true;

    public bool RegisterWithSystem { get; init; } = true;
}

public sealed record ExistingInstall(string Directory, string? Version, bool HasStartMenuShortcut, bool HasDesktopShortcut);

public sealed class InstallProgress
{
    public InstallProgress(double percent, string message)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Message = message;
    }

    public double Percent { get; }

    public string Message { get; }
}
