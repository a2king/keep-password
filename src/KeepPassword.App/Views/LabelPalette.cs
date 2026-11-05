using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

/// <summary>
/// 空间 / 标签的配色：按名称稳定地映射到 8 个色相之一（资源见 Styles/LabelPaletteTheme.axaml）。
/// 不用 string.GetHashCode：它在 .NET 里每次启动都会随机化，同一个标签每次打开会换颜色。
/// 这里用 FNV-1a 对规范化后的名称（忽略大小写、首尾空白）求哈希，结果跨进程、跨机器一致。
/// </summary>
public static class LabelPalette
{
    public const int Count = 8;

    public static int IndexOf(string name)
    {
        var key = LabelName.Normalize(name).ToUpperInvariant();
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in key)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            return (int)(hash % Count);
        }
    }

    public static string Background(string name) => $"Label.{IndexOf(name)}.Bg";
    public static string Foreground(string name) => $"Label.{IndexOf(name)}.Fg";
    public static string Border(string name) => $"Label.{IndexOf(name)}.Border";
}
