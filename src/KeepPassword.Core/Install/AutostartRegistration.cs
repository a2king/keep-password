using System.Runtime.Versioning;
using System.Text;

namespace KeepPassword.Core.Install;

internal enum AutostartPlatform
{
    Windows,
    Mac,
    Linux,
    Unsupported
}

/// <summary>
/// 开机自启动的登记 / 取消 / 查询，供主程序、安装向导和卸载程序共用。
/// 所有写入都是"先读后写、没变化就不动"，所以启动时反复调用不会再触发安全软件的注册表拦截。
/// </summary>
public static class AutostartRegistration
{
    public const string MinimizedArg = "--minimized";

    public static string BuildCommand(string exe, bool startMinimized)
    {
        var quoted = exe.Contains(' ', StringComparison.Ordinal)
            ? "\"" + exe.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : exe;
        return startMinimized ? $"{quoted} {MinimizedArg}" : quoted;
    }

    /// <summary>登记或取消自启动。返回 true 表示系统里的内容确实被改动过。</summary>
    public static bool Apply(string exe, bool enabled, bool startMinimized) =>
        Apply(exe, enabled, startMinimized, AutostartLocations.Default(), CurrentPlatform());

    /// <summary>用户没明确选过时：沿用系统里已有的登记状态，全新安装则默认不开启。</summary>
    public static bool ResolveEnabled(bool? stored) => stored ?? IsRegistered();

    public static bool IsRegistered() => IsRegistered(AutostartLocations.Default(), CurrentPlatform());

    public static bool Remove() => Apply("", false, false, AutostartLocations.Default(), CurrentPlatform());

    internal static AutostartPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? AutostartPlatform.Windows
        : OperatingSystem.IsMacOS() ? AutostartPlatform.Mac
        : OperatingSystem.IsLinux() ? AutostartPlatform.Linux
        : AutostartPlatform.Unsupported;

    internal static bool Apply(string exe, bool enabled, bool startMinimized, AutostartLocations where, AutostartPlatform platform)
    {
        switch (platform)
        {
            case AutostartPlatform.Windows when OperatingSystem.IsWindows():
                return ApplyWindows(exe, enabled, startMinimized, where);
            case AutostartPlatform.Mac:
                return ApplyFile(where.MacLaunchAgent, enabled ? MacPlist(exe, startMinimized) : null);
            case AutostartPlatform.Linux:
                return ApplyFile(where.LinuxDesktopFile, enabled ? LinuxDesktop(exe, startMinimized) : null);
            default:
                return false;
        }
    }

    internal static bool IsRegistered(AutostartLocations where, AutostartPlatform platform) => platform switch
    {
        AutostartPlatform.Windows when OperatingSystem.IsWindows() =>
            new RegistryWriter().GetString(where.RunKey, where.RunValueName) is { Length: > 0 },
        AutostartPlatform.Mac => File.Exists(where.MacLaunchAgent),
        AutostartPlatform.Linux => File.Exists(where.LinuxDesktopFile),
        _ => false
    };

    [SupportedOSPlatform("windows")]
    private static bool ApplyWindows(string exe, bool enabled, bool startMinimized, AutostartLocations where)
    {
        var writer = new RegistryWriter();
        return enabled
            ? writer.SetString(where.RunKey, where.RunValueName, BuildCommand(exe, startMinimized))
            : writer.DeleteValue(where.RunKey, where.RunValueName);
    }

    /// <summary>content 为 null 表示删除文件；内容与现有文件一致时不重复写入。</summary>
    private static bool ApplyFile(string path, string? content)
    {
        if (content is null)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }

        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return false;
        }

        var folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllText(path, content);
        return true;
    }

    // 文件内容与旧版 StartupPreferences 生成的逐字一致，升级后不会白白重写一次。
    private static string MacPlist(string exe, bool startMinimized)
    {
        var args = startMinimized
            ? $"""
              <string>{EscapeXml(exe)}</string>
              <string>{MinimizedArg}</string>
              """
            : $"<string>{EscapeXml(exe)}</string>";
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>com.keeppassword.app</string>
              <key>ProgramArguments</key>
              <array>
                {args}
              </array>
              <key>RunAtLoad</key>
              <true/>
            </dict>
            </plist>
            """;
    }

    private static string LinuxDesktop(string exe, bool startMinimized) => $"""
        [Desktop Entry]
        Type=Application
        Version=1.0
        Name=Keep Password
        Comment=Keep Password
        Exec={BuildCommand(exe, startMinimized)}
        Terminal=false
        X-GNOME-Autostart-enabled=true
        """;

    private static string EscapeXml(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(ch switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ => ch.ToString()
            });
        }

        return sb.ToString();
    }
}
