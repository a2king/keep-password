using Avalonia.Media;

namespace KeepPassword.App.Views;

/// <summary>
/// 服务器协议图标，路径取自 Material Design Icons（Apache-2.0）。
/// </summary>
public static class ServerIcons
{
    private static readonly Dictionary<string, (string Path, string Color)> Sources = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ssh"] = ("M17 3a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-4v2h1a1 1 0 0 1 1 1h7v2h-7a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1H2v-2h7a1 1 0 0 1 1-1h1v-2H7a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zM7 7l4 4l-4 4h2.85l3.28-3.28c.37-.39.37-1.02 0-1.42L9.83 7zm10 6h-3v2h3z", "#2563EB"),
        ["sftp"] = ("M3 15V5a2 2 0 0 1 2-2h6l2 2h6a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2h-6v2h1a1 1 0 0 1 1 1h7v2h-7a1 1 0 0 1-1 1h-4a1 1 0 0 1-1-1H2v-2h7a1 1 0 0 1 1-1h1v-2H5a2 2 0 0 1-2-2", "#F59E0B"),
        ["rdp"] = ("M3 2a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h7v2H8v2h8v-2h-2v-2h7a2 2 0 0 0 2-2V4a2 2 0 0 0-2-2M3 4h18v12H3M15 5l-3.5 3.5L15 12l1.4-1.4l-2.1-2.1l2.1-2.1M9 8L7.6 9.4l2.1 2.1l-2.1 2.1L9 15l3.5-3.5", "#0078D4"),
        ["ftp"] = ("M20 6h-8l-2-2H4a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2m-5 10v-2H9v2l-3-3l3-3v2h6v-2l3 3Z", "#EA580C"),
        ["ftps"] = ("M13 20H4a2 2 0 0 1-2-2V6c0-1.11.89-2 2-2h6l2 2h8a2 2 0 0 1 2 2v3.04A4.9 4.9 0 0 0 19 10c-2.6 0-4.8 2.06-4.8 4.5v.24c-.7.6-1.2 1.5-1.2 2.46zm10-2.7v3.5c0 .6-.6 1.2-1.3 1.2h-5.5c-.6 0-1.2-.6-1.2-1.3v-3.5c0-.6.6-1.2 1.2-1.2v-1.5c0-1.4 1.4-2.5 2.8-2.5s2.8 1.1 2.8 2.5V16c.6 0 1.2.6 1.2 1.3m-2.5-2.8c0-.8-.7-1.3-1.5-1.3s-1.5.5-1.5 1.3V16h3z", "#16A34A"),
        ["vnc"] = ("M23 4v12c0 1.11-.89 2-2 2h-6v-2h6V4H3v12h6v2H3a2 2 0 0 1-2-2V4c0-1.11.89-2 2-2h18a2 2 0 0 1 2 2m-10 9h3l-4-4l-4 4h3v7H8v2h8v-2h-3z", "#7C3AED"),
        ["telnet"] = ("M20 19V7H4v12zm0-16a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2zm-7 14v-2h5v2zm-3.42-4L5.57 9H8.4l3.3 3.3c.39.39.39 1.03 0 1.42L8.42 17H5.59z", "#64748B"),
    };

    private static readonly Dictionary<string, IImage> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static IImage? Find(string? protocol)
    {
        if (string.IsNullOrEmpty(protocol) || !Sources.TryGetValue(protocol, out var source))
        {
            return null;
        }

        if (!Cache.TryGetValue(protocol, out var image))
        {
            image = DatabaseIcons.Build([(source.Color, source.Path)]);
            Cache[protocol] = image;
        }

        return image;
    }
}
