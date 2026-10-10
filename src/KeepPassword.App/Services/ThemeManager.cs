using Avalonia;
using Avalonia.Styling;
using KeepPassword.Core.Paths;

namespace KeepPassword.App.Services;

public static class ThemeManager
{
    public const string SystemTheme = "System";
    public const string LightTheme = "Light";
    public const string DarkTheme = "Dark";

    public static event Action<string>? ThemeChanged;

    public static string CurrentTheme { get; private set; } = SystemTheme;

    public static void Initialize()
    {
        var file = CacheDirectory.SettingsFile();
        CurrentTheme = CacheDirectory.GetTheme(file);
        ApplyTheme(CurrentTheme);
    }

    public static void SetTheme(string theme)
    {
        CurrentTheme = theme is LightTheme or DarkTheme ? theme : SystemTheme;
        var file = CacheDirectory.SettingsFile();
        CacheDirectory.SetTheme(file, CurrentTheme);
        ApplyTheme(CurrentTheme);
        ThemeChanged?.Invoke(CurrentTheme);
    }

    public static string CycleTheme()
    {
        var next = CurrentTheme switch
        {
            LightTheme => DarkTheme,
            DarkTheme => SystemTheme,
            _ => LightTheme
        };
        SetTheme(next);
        return next;
    }

    public static string GetThemeDisplayName(string? theme = null)
    {
        return (theme ?? CurrentTheme) switch
        {
            LightTheme => "浅色模式",
            DarkTheme => "深色模式",
            _ => "跟随系统"
        };
    }

    private static void ApplyTheme(string theme)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = theme switch
        {
            LightTheme => ThemeVariant.Light,
            DarkTheme => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };
    }
}
