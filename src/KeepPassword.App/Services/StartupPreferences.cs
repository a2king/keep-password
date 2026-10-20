using System.Runtime.Versioning;
using System.Text;
using KeepPassword.Core.Paths;
using Microsoft.Win32;

namespace KeepPassword.App.Services;

/// <summary>
/// 开机启动与最小化启动：读写 settings，并同步到各平台自启动位置。
/// </summary>
public static class StartupPreferences
{
    public const string MinimizedArg = "--minimized";
    private const string WindowsRunValueName = "KeepPassword";
    private const string MacLaunchAgentLabel = "com.keeppassword.app";
    private const string LinuxDesktopFileName = "keep-password.desktop";

    public static bool LaunchAtStartup { get; private set; } = true;

    public static bool StartMinimized { get; private set; } = true;

    public static bool RequestedMinimizedStartup { get; private set; }

    public static void Initialize()
    {
        var settings = CacheDirectory.SettingsFile();
        LaunchAtStartup = CacheDirectory.GetLaunchAtStartup(settings);
        StartMinimized = CacheDirectory.GetStartMinimized(settings);
        RequestedMinimizedStartup = Environment.GetCommandLineArgs()
            .Any(arg => string.Equals(arg, MinimizedArg, StringComparison.OrdinalIgnoreCase));

        // 把默认/已保存偏好同步到系统自启动（首次安装也默认开启）。
        try
        {
            ApplyOsRegistration(LaunchAtStartup, StartMinimized);
        }
        catch (Exception)
        {
            // 权限或路径异常时不阻断启动；设置页可再试。
        }
    }

    public static string? SetLaunchAtStartup(bool enabled)
    {
        CacheDirectory.SetLaunchAtStartup(CacheDirectory.SettingsFile(), enabled);
        LaunchAtStartup = enabled;
        return TryApplyOs();
    }

    public static string? SetStartMinimized(bool enabled)
    {
        CacheDirectory.SetStartMinimized(CacheDirectory.SettingsFile(), enabled);
        StartMinimized = enabled;
        return LaunchAtStartup ? TryApplyOs() : null;
    }

    private static string? TryApplyOs()
    {
        try
        {
            ApplyOsRegistration(LaunchAtStartup, StartMinimized);
            return null;
        }
        catch (Exception ex)
        {
            return "无法更新开机启动项：" + ex.Message;
        }
    }

    internal static void ApplyOsRegistration(bool launchAtStartup, bool startMinimized)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            throw new InvalidOperationException("找不到当前程序路径。");
        }

        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(exe, launchAtStartup, startMinimized);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            ApplyMac(exe, launchAtStartup, startMinimized);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            ApplyLinux(exe, launchAtStartup, startMinimized);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindows(string exe, bool launchAtStartup, bool startMinimized)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (!launchAtStartup)
        {
            key.DeleteValue(WindowsRunValueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(WindowsRunValueName, BuildCommand(exe, startMinimized));
    }

    private static void ApplyMac(string exe, bool launchAtStartup, bool startMinimized)
    {
        var path = MacLaunchAgentPath();
        if (!launchAtStartup)
        {
            TryDelete(path);
            return;
        }

        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var args = startMinimized
            ? $"""
              <string>{EscapeXml(exe)}</string>
              <string>{MinimizedArg}</string>
              """
            : $"<string>{EscapeXml(exe)}</string>";
        var plist = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
              <key>Label</key>
              <string>{MacLaunchAgentLabel}</string>
              <key>ProgramArguments</key>
              <array>
                {args}
              </array>
              <key>RunAtLoad</key>
              <true/>
            </dict>
            </plist>
            """;
        File.WriteAllText(path, plist);
    }

    private static void ApplyLinux(string exe, bool launchAtStartup, bool startMinimized)
    {
        var path = LinuxDesktopPath();
        if (!launchAtStartup)
        {
            TryDelete(path);
            return;
        }

        var folder = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(folder);
        var command = BuildCommand(exe, startMinimized);
        var desktop = $"""
            [Desktop Entry]
            Type=Application
            Version=1.0
            Name=Keep Password
            Comment=Keep Password
            Exec={command}
            Terminal=false
            X-GNOME-Autostart-enabled=true
            """;
        File.WriteAllText(path, desktop);
    }

    private static string BuildCommand(string exe, bool startMinimized)
    {
        var quoted = Quote(exe);
        return startMinimized ? $"{quoted} {MinimizedArg}" : quoted;
    }

    private static string Quote(string path) =>
        path.Contains(' ', StringComparison.Ordinal) ? "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : path;

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

    private static string MacLaunchAgentPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", MacLaunchAgentLabel + ".plist");

    private static string LinuxDesktopPath()
    {
        var config = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }

        return Path.Combine(config, "autostart", LinuxDesktopFileName);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
