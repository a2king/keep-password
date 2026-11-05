namespace KeepPassword.Core.Install;

public sealed class InstallOptions
{
    public bool CreateStartMenuShortcut { get; init; } = true;

    public bool CreateDesktopShortcut { get; init; } = true;

    public bool LaunchAfterInstall { get; init; } = true;

    public bool RegisterWithSystem { get; init; } = true;

    /// <summary>
    /// 是否开机自启动。null 表示不动现有设置（用于静默重装、测试）；
    /// 安装向导总是传明确的值，默认不勾选，所以全新安装不会悄悄加入开机启动。
    /// </summary>
    public bool? LaunchAtStartup { get; init; }

    /// <summary>仅供测试：把注册表 / 快捷方式 / 设置文件指向隔离位置，避免碰到真实系统。</summary>
    internal InstallLocations? Locations { get; init; }

    /// <summary>仅供测试：跳过扫描浏览器配置目录，改用固定的扩展来源列表。</summary>
    internal IReadOnlyList<string>? DiscoveredOrigins { get; init; }
}

/// <summary>一次安装实际改动了什么。重复安装同一版本时，注册表与快捷方式的"写入"应为 0。</summary>
public sealed record InstallSummary(
    int Files,
    int RegistryWrites,
    int RegistryUnchanged,
    int ShortcutsWritten,
    int ShortcutsUnchanged,
    bool AutostartChanged);

public sealed record ExistingInstall(string Directory, string? Version, bool HasStartMenuShortcut, bool HasDesktopShortcut);

public sealed class InstallProgress
{
    public InstallProgress(double percent, string message, string? log = null)
    {
        Percent = Math.Clamp(percent, 0, 100);
        Message = message;
        Log = log;
    }

    public double Percent { get; }

    public string Message { get; }

    public string? Log { get; }
}
