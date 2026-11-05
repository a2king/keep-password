using System.Text;
using System.Text.RegularExpressions;

namespace KeepPassword.Release;

/// <summary>读取 / 更新 UpdateTrust.cs 里 BEGIN/END 标记之间的受信公钥列表。</summary>
internal static partial class TrustFile
{
    private const string Begin = "// BEGIN-TRUSTED-KEYS";
    private const string End = "// END-TRUSTED-KEYS";

    public static string Locate(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            return File.Exists(explicitPath) ? explicitPath : throw new FileNotFoundException("找不到 UpdateTrust.cs。", explicitPath);
        }

        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "KeepPassword.Core", "Update", "UpdateTrust.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("在当前目录向上找不到 src/KeepPassword.Core/Update/UpdateTrust.cs，请用 --trust-file 指定。");
    }

    public static IReadOnlyList<string> ReadKeys(string path)
    {
        var region = Region(File.ReadAllText(path));
        return KeyPattern().Matches(region).Select(match => match.Groups[1].Value).ToList();
    }

    /// <summary>追加一把公钥（已存在则不重复），保留旧公钥以便换钥期间新旧并存。</summary>
    public static void AddKey(string path, string publicKeyBase64)
    {
        var text = File.ReadAllText(path);
        var keys = ReadKeys(path).ToList();
        if (!keys.Contains(publicKeyBase64, StringComparer.Ordinal))
        {
            keys.Add(publicKeyBase64);
        }

        var body = new StringBuilder();
        body.Append("    public static readonly IReadOnlyList<string> PublicKeys =\n    [\n");
        foreach (var key in keys)
        {
            body.Append("        \"").Append(key).Append("\",\n");
        }

        body.Append("    ];\n");
        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        var rebuilt = text[..(begin + Begin.Length)] + "\n" + body.ToString().Replace("\n", newline).TrimEnd('\r', '\n')
                      + newline + "    " + text[end..];
        File.WriteAllText(path, rebuilt.Replace("\r\n", "\n").Replace("\n", newline), new UTF8Encoding(false));
    }

    private static string Region(string text)
    {
        var begin = text.IndexOf(Begin, StringComparison.Ordinal);
        var end = text.IndexOf(End, StringComparison.Ordinal);
        return begin < 0 || end < begin
            ? throw new FormatException("UpdateTrust.cs 缺少 BEGIN-TRUSTED-KEYS / END-TRUSTED-KEYS 标记。")
            : text[begin..end];
    }

    [GeneratedRegex("\"([A-Za-z0-9+/=]{40,})\"")]
    private static partial Regex KeyPattern();
}
