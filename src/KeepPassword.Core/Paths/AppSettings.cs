using System.Text.Json;
using System.Text.Json.Serialization;

namespace KeepPassword.Core.Paths;

/// <summary>
/// 固定位置的应用设置（与缓存目录配置同一文件）。
/// </summary>
public static class AppSettings
{
    public const int DefaultAutoLockSeconds = 30;
    public const int MinAutoLockSeconds = 5;
    public const int MaxAutoLockSeconds = 3600;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static int GetAutoLockSeconds(string settingsFile)
    {
        var dto = Read(settingsFile);
        return NormalizeAutoLock(dto.AutoLockSeconds);
    }

    public static void SetAutoLockSeconds(string settingsFile, int seconds)
    {
        var dto = Read(settingsFile);
        dto.AutoLockSeconds = NormalizeAutoLock(seconds);
        Write(settingsFile, dto);
    }

    public static int NormalizeAutoLock(int? seconds)
    {
        if (seconds is null || seconds <= 0)
        {
            return DefaultAutoLockSeconds;
        }

        return Math.Clamp(seconds.Value, MinAutoLockSeconds, MaxAutoLockSeconds);
    }

    internal static SettingsDto Read(string settingsFile)
    {
        if (!File.Exists(settingsFile))
        {
            return new SettingsDto();
        }

        try
        {
            return JsonSerializer.Deserialize<SettingsDto>(File.ReadAllText(settingsFile), Options)
                ?? new SettingsDto();
        }
        catch (JsonException)
        {
            return new SettingsDto();
        }
    }

    internal static void Write(string settingsFile, SettingsDto dto)
    {
        var folder = Path.GetDirectoryName(settingsFile);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        var json = JsonSerializer.Serialize(dto, Options);
        var temp = settingsFile + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, settingsFile, overwrite: true);
    }

    internal sealed class SettingsDto
    {
        public string? Directory { get; set; }

        public int? AutoLockSeconds { get; set; }
    }
}
