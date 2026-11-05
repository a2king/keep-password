using System.Runtime.Versioning;
using Microsoft.Win32;

namespace KeepPassword.Core.Install;

/// <summary>
/// 只在值真的变化时才写注册表（先只读打开比较，一致就不写）。
/// 安全软件拦截的是"写入"动作：每次启动、每次重装都无条件写同一个值，就会反复弹"注册表修改"提示。
/// 这里把写入收敛为"确有变化才写"，并统计写入 / 跳过次数，方便在日志与测试里验证。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class RegistryWriter
{
    public int Writes { get; private set; }

    public int Unchanged { get; private set; }

    public bool SetString(string subKey, string name, string value)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(subKey))
        {
            if (existing?.GetValue(name) is string current
                && string.Equals(current, value, StringComparison.Ordinal)
                && existing.GetValueKind(name) == RegistryValueKind.String)
            {
                Unchanged++;
                return false;
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value, RegistryValueKind.String);
        Writes++;
        return true;
    }

    public bool SetDword(string subKey, string name, int value)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(subKey))
        {
            if (existing?.GetValue(name) is int current
                && current == value
                && existing.GetValueKind(name) == RegistryValueKind.DWord)
            {
                Unchanged++;
                return false;
            }
        }

        using var key = Registry.CurrentUser.CreateSubKey(subKey);
        key.SetValue(name, value, RegistryValueKind.DWord);
        Writes++;
        return true;
    }

    public string? GetString(string subKey, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return key?.GetValue(name) as string;
    }

    /// <summary>值不存在就什么都不做，不会为了"删除"去打开可写句柄。</summary>
    public bool DeleteValue(string subKey, string name)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(subKey))
        {
            if (existing?.GetValue(name) is null)
            {
                Unchanged++;
                return false;
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
        Writes++;
        return true;
    }

    public bool DeleteTree(string subKey)
    {
        using (var existing = Registry.CurrentUser.OpenSubKey(subKey))
        {
            if (existing is null)
            {
                Unchanged++;
                return false;
            }
        }

        Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
        Writes++;
        return true;
    }
}
