using Avalonia.Media;
using KeepPassword.App.Controls.Details;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views.Details;

/// <summary>
/// 详情头部的内容，所有条目类型共用：类型图标、类型标签、副标题（空间 · 协议/驱动）和收藏状态。
/// 新版详情和旧编辑器（SSH 密钥、API 凭据、集群服务）都调用这里，保证两边头部一致。
/// </summary>
public static class DetailHeaderContent
{
    private static readonly Dictionary<VaultItemKind, Geometry> Icons = [];

    /// <summary>24×24 线条图标，按头部的描边样式绘制（只描边、圆头）。</summary>
    public static string IconPath(VaultItemKind kind) => kind switch
    {
        VaultItemKind.Server => "M3,4 L21,4 L21,20 L3,20 Z M7,9 L10,12 L7,15 M13,15 L17,15",
        VaultItemKind.Database => "M3,6 C3,2 21,2 21,6 C21,10 3,10 3,6 M3,6 L3,18 C3,22 21,22 21,18 L21,6 M3,12 C3,16 21,16 21,12",
        // 文件里一把小钥匙：私钥/公钥文件。
        VaultItemKind.SshKey => "M6,3 L14,3 L19,8 L19,21 L6,21 Z M14,3 L14,8 L19,8 M8,15 A2,2 0 1,0 12,15 A2,2 0 1,0 8,15 M12,15 L16,15 M15,15 L15,17",
        // 花括号：接口密钥。
        VaultItemKind.ApiCredential => "M8,4 C5,4 6,10 3,12 C6,14 5,20 8,20 M16,4 C19,4 18,10 21,12 C18,14 19,20 16,20 M12,12 L12.01,12",
        // 三个相连的节点：集群。
        VaultItemKind.Cluster => "M9.5,5 A2.5,2.5 0 1,0 14.5,5 A2.5,2.5 0 1,0 9.5,5 M3.5,19 A2.5,2.5 0 1,0 8.5,19 A2.5,2.5 0 1,0 3.5,19 M15.5,19 A2.5,2.5 0 1,0 20.5,19 A2.5,2.5 0 1,0 15.5,19 M10.8,7.2 L7.2,16.8 M13.2,7.2 L16.8,16.8 M8.5,19 L15.5,19",
        _ => "M10,14 C4,18 0,11 4,7 C8,3 15,7 11,13 L20,22 M15,17 L18,14 M18,20 L21,17"
    };

    public static Geometry Icon(VaultItemKind kind)
    {
        if (!Icons.TryGetValue(kind, out var icon))
        {
            icon = Geometry.Parse(IconPath(kind));
            Icons[kind] = icon;
        }

        return icon;
    }

    public static string Subtitle(MainViewModel source) => string.Join(" · ", new[]
    {
        source.SpaceChoices.FirstOrDefault(choice => choice.IsSelected)?.Name,
        source.SelectedProtocol?.Name ?? source.SelectedDriver?.Name
    }.Where(text => !string.IsNullOrWhiteSpace(text)));

    public static void Apply(DetailHeader header, MainViewModel source)
    {
        header.KindLabel = source.KindName;
        header.IsFavorite = source.Favorite;
        header.KindIcon = Icon(source.Kind);
        header.Subtitle = Subtitle(source);
    }
}
