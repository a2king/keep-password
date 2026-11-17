using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace KeepPassword.App.Controls.Details;

/// <summary>
/// 画在窗口覆盖层里的 Flyout。桌面平台默认把弹层做成独立的小窗，位置只受屏幕边缘约束，
/// 目标靠近窗口底部时会伸出应用窗口。放进覆盖层后由窗口约束：下方放不下就翻到上方，左右放不下就平移。
/// </summary>
public class InWindowFlyout : Flyout
{
    public InWindowFlyout()
    {
        Popup.ShouldUseOverlayLayer = true;
    }

    /// <summary>测试用：确认弹层确实在窗口覆盖层里。</summary>
    internal bool IsUsingOverlayLayer => Popup.IsUsingOverlayLayer;
}
