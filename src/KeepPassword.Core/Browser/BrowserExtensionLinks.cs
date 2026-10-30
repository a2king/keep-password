namespace KeepPassword.Core.Browser;

/// <summary>浏览器扩展商店入口。Edge 已上架；Chrome 待补充。</summary>
public static class BrowserExtensionLinks
{
    public const string EdgeCrxId = "lbkhkkkmnbhnnmimhadmjappokdfkpgd";
    public const string EdgeStoreId = "0RDCKGQT5LVS";
    public const string EdgeProductId = "86f41962-78cf-4dae-a65f-19bd2bad5e8a";

    public const string EdgeStoreUrl =
        "https://microsoftedge.microsoft.com/addons/detail/lbkhkkkmnbhnnmimhadmjappokdfkpgd";

    /// <summary>私有扩展：需通过商店页连接/邀请后才能打开。</summary>
    public const bool EdgeStoreIsPrivate = true;
}
