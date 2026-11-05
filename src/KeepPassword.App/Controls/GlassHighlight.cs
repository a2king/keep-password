using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace KeepPassword.App.Controls;

/// <summary>
/// 玻璃块顶部的镜面高光：按自身尺寸的比例绘制一条圆角渐变条。
/// 用比例而非固定像素，玻璃块被按压放大或切换 compact 尺寸时高光自动跟随，无需逐处调参。
/// 静态绘制、不参与命中测试、没有任何常驻动画。
/// </summary>
public sealed class GlassHighlight : Control
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<GlassHighlight, IBrush?>(nameof(Fill));

    /// <summary>高光高度占玻璃块高度的比例（规格：约 38%）。</summary>
    public static readonly StyledProperty<double> HeightRatioProperty =
        AvaloniaProperty.Register<GlassHighlight, double>(nameof(HeightRatio), 0.38);

    /// <summary>左右各缩进的比例（规格：12%）。</summary>
    public static readonly StyledProperty<double> InsetRatioProperty =
        AvaloniaProperty.Register<GlassHighlight, double>(nameof(InsetRatio), 0.12);

    /// <summary>距玻璃块顶部的像素偏移，避开 1px 内描边。</summary>
    public static readonly StyledProperty<double> TopOffsetProperty =
        AvaloniaProperty.Register<GlassHighlight, double>(nameof(TopOffset), 1.5);

    static GlassHighlight()
    {
        AffectsRender<GlassHighlight>(FillProperty, HeightRatioProperty, InsetRatioProperty, TopOffsetProperty);
        IsHitTestVisibleProperty.OverrideDefaultValue<GlassHighlight>(false);
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double HeightRatio { get => GetValue(HeightRatioProperty); set => SetValue(HeightRatioProperty, value); }
    public double InsetRatio { get => GetValue(InsetRatioProperty); set => SetValue(InsetRatioProperty, value); }
    public double TopOffset { get => GetValue(TopOffsetProperty); set => SetValue(TopOffsetProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (Fill is not { } fill || size.Width <= 0 || size.Height <= 0) return;
        var inset = size.Width * InsetRatio;
        var height = Math.Max(0, size.Height * HeightRatio);
        var rect = new Rect(inset, TopOffset, Math.Max(0, size.Width - inset * 2), height);
        if (rect.Width <= 0 || rect.Height <= 0) return;
        var radius = rect.Height / 2;
        context.DrawRectangle(fill, null, new RoundedRect(rect, radius));
    }
}
