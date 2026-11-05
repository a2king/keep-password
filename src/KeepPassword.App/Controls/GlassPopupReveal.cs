using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KeepPassword.App.Controls;

/// <summary>
/// 下拉面板的打开动画触发器。Popup 内容每次打开都会重新挂到新的弹出窗口上，
/// 直接用 :dropdownopen 触发过渡时，样式可能在内容尚未挂载前就已生效，动画会被跳过。
/// 这里在内容挂载后的下一帧才加上 revealed 类，让 Opacity/RenderTransform 过渡稳定播放；
/// 卸载时先关闭过渡再移除类，保证下次打开从初始态开始。
/// 关闭不做淡出：Popup 在 IsOpen=false 时立即销毁窗口，Avalonia 无法在其后播放过渡。
/// </summary>
public static class GlassPopupReveal
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(GlassPopupReveal));

    static GlassPopupReveal()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, args) =>
        {
            control.AttachedToVisualTree -= OnAttached;
            control.DetachedFromVisualTree -= OnDetached;
            if (args.NewValue is not true) return;
            control.AttachedToVisualTree += OnAttached;
            control.DetachedFromVisualTree += OnDetached;
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control control) return;
        Reset(control);
        Dispatcher.UIThread.Post(() =>
        {
            if (control.GetVisualRoot() is not null) control.Classes.Add("revealed");
        }, DispatcherPriority.Render);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Reset(control);
    }

    private static void Reset(Control control)
    {
        // 先清空过渡再移除类，初始态立即落位，不会从上一次的 1.0 反向播放。
        control.Transitions = null;
        control.Classes.Remove("revealed");
        control.ClearValue(Animatable.TransitionsProperty);
    }
}
