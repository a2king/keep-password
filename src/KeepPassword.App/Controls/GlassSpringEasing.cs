using Avalonia.Animation.Easings;

namespace KeepPassword.App.Controls;

/// <summary>
/// 带可调回弹幅度的 BackEaseOut（Penner easeOutBack 公式）。
/// Avalonia 自带的 BackEaseOut 回弹约 38%，对 3px 边距的开关会越界、对分段滑块也显得夸张；
/// 这里默认 Overshoot=1.2，峰值约超出 5%，"轻微回弹"且不会让滑块跑出轨道。
/// 调参：在 GlassTheme.axaml 的 Glass.SpringEasing 资源上改 Overshoot（0 = 无回弹）。
/// </summary>
public sealed class GlassSpringEasing : Easing
{
    public double Overshoot { get; set; } = 1.2;

    public override double Ease(double progress)
    {
        var s = Math.Max(0, Overshoot);
        var t = progress - 1;
        return t * t * ((s + 1) * t + s) + 1;
    }
}
