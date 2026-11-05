using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using KeepPassword.App.Controls;

namespace KeepPassword.App.Tests;

public class LiquidGlassInteractionTests
{
    [AvaloniaFact]
    public async Task Segmented_ThumbSlidesToSelectionAndFollowsResize()
    {
        var segments = new SegmentedControl { ItemsSource = new[] { "随机字符", "容易记住", "PIN" }, Margin = new Thickness(24) };
        var window = new Window { Width = 600, Height = 140, Content = segments };
        try
        {
            window.Show();
            await Frames(window, 60);
            var items = Enumerable.Range(0, 3).Select(i => (Control)segments.ContainerFromIndex(i)!).ToArray();
            var thumb = Thumb(segments);
            var before = items.Select(item => item.Bounds).ToArray();
            Assert.Equal(X(items[0], thumb), thumb.RenderTransform!.Value.M31, precision: 1);

            // 选中变化：中途位于两项之间，结束后对齐新选中项；各选项本身不移动、不改尺寸。
            segments.SelectedIndex = 2;
            await Frames(window, 90);
            Assert.InRange(thumb.RenderTransform!.Value.M31, X(items[0], thumb) + 0.5, X(items[2], thumb) + 20);
            await Frames(window, 700);
            Assert.Equal(X(items[2], thumb), thumb.RenderTransform!.Value.M31, precision: 1);
            Assert.Equal(items[2].Bounds.Width, thumb.Bounds.Width, precision: 1);
            Assert.Equal(before, items.Select(item => item.Bounds));

            // 窗口缩放后滑块立即跟随选中项（不播放位移动画）。
            window.Width = 420;
            await Frames(window, 60);
            Assert.Equal(X(items[2], thumb), thumb.RenderTransform!.Value.M31, precision: 1);
            Assert.Equal(items[2].Bounds.Width, thumb.Bounds.Width, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Segmented_KeyboardMovesSelectionAndReducedMotionJumpsWithoutSpring()
    {
        var segments = new SegmentedControl { ItemsSource = new[] { "浅色", "深色", "跟随系统" } };
        var window = new Window { Width = 520, Height = 140, Content = new Border { Padding = new Thickness(24), Child = segments }, Classes = { "no-motion" } };
        try
        {
            window.Show();
            await Frames(window, 60);
            var thumb = Thumb(segments);
            (segments.ContainerFromIndex(0) as Control)!.Focus(NavigationMethod.Tab);
            await Frames(window, 30);
            // 键盘进入时显示焦点环（伪类不对外公开，检查焦点环本身）。
            Assert.Equal(1, segments.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_FocusRing").Opacity);

            Press(window, PhysicalKey.ArrowRight);
            Assert.Equal(1, segments.SelectedIndex);
            Press(window, PhysicalKey.End);
            Assert.Equal(2, segments.SelectedIndex);
            Press(window, PhysicalKey.ArrowRight); // 末项再按右键回到首项
            Assert.Equal(0, segments.SelectedIndex);
            Press(window, PhysicalKey.End);
            Press(window, PhysicalKey.Home);
            Assert.Equal(0, segments.SelectedIndex);

            // 减少动画：一次布局后就已落在目标，没有中间帧，也没有回弹。
            Press(window, PhysicalKey.ArrowLeft);
            await Frames(window, 40);
            Assert.Equal(2, segments.SelectedIndex);
            var target = X((Control)segments.ContainerFromIndex(2)!, thumb);
            Assert.Equal(target, thumb.RenderTransform!.Value.M31, precision: 1);
            await Frames(window, 200);
            Assert.Equal(target, thumb.RenderTransform!.Value.M31, precision: 1);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Toggle_KeepsKeyboardDisabledAndContentBehavior()
    {
        var toggle = new ToggleSwitch { OnContent = "开机启动", OffContent = "关闭" };
        var plain = new ToggleSwitch();
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 16, Children = { toggle, plain } };
        var window = new Window { Width = 400, Height = 240, Content = panel, Classes = { "no-motion" } };
        try
        {
            window.Show();
            await Frames(window, 30);
            var width = toggle.Bounds.Width;
            toggle.Focus(NavigationMethod.Tab);
            Press(window, PhysicalKey.Space);
            Assert.True(toggle.IsChecked);
            await Frames(window, 30);
            // 开/关文字叠放，切换不改变控件宽度；滑块 12px 位移落在轨道内。
            Assert.Equal(width, toggle.Bounds.Width);
            var thumb = toggle.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "PART_Thumb");
            Assert.Equal(12, thumb.RenderTransform!.Value.M31, precision: 1);
            // 未设置 OnContent/OffContent 时不显示 Fluent 默认的英文 On/Off。
            Assert.DoesNotContain(plain.GetVisualDescendants().OfType<TextBlock>(), t => t.IsEffectivelyVisible && t.Text is ("On" or "Off"));

            toggle.IsEnabled = false;
            Press(window, PhysicalKey.Space);
            Assert.True(toggle.IsChecked);
            Assert.Equal(0.45, toggle.Opacity, precision: 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Combo_ChevronRotatesButCheckMarksStayUpright()
    {
        var select = new ComboBox { ItemsSource = new[] { "SSH", "RDP", "VNC" }, SelectedIndex = 0, Width = 240 };
        var window = new Window { Width = 400, Height = 360, Content = new Border { Padding = new Thickness(24), Child = select }, Classes = { "no-motion" } };
        try
        {
            window.Show();
            await Frames(window, 30);
            var chevron = select.GetVisualDescendants().OfType<Path>().Single(p => p.Name == "PART_Chevron");
            select.Focus(NavigationMethod.Tab);
            Press(window, PhysicalKey.ArrowDown);
            Assert.Equal(1, select.SelectedIndex);

            select.IsDropDownOpen = true;
            await Frames(window, 60);
            // 触发器 chevron 旋转 180°（矩阵 M11 = cos180° = -1）。
            Assert.Equal(-1, chevron.RenderTransform!.Value.M11, precision: 2);
            var items = Enumerable.Range(0, 3).Select(i => (ComboBoxItem)select.ContainerFromIndex(i)!).ToArray();
            var checks = items.Select(item => item.GetVisualDescendants().OfType<Path>().Single(p => p.Name == "PART_Check")).ToArray();
            // 下拉项里的 ✓ 不受旋转规则影响；未选中项透明但仍占位，所有项文字起点一致。
            Assert.All(checks, check => Assert.True(check.RenderTransform is null || check.RenderTransform.Value.IsIdentity));
            Assert.Equal(1, checks[1].Opacity);
            Assert.Equal(0, checks[0].Opacity);
            Assert.All(checks, check => Assert.Equal(16, check.Bounds.Width));
            // 面板宽度与触发器一致，圆角面板外留有阴影边距。
            var surface = items[0].GetVisualAncestors().OfType<Border>().First(b => b.Name == "PART_PopupSurface");
            Assert.Equal(select.Bounds.Width, surface.Bounds.Width, precision: 1);
            Assert.Equal(18, surface.CornerRadius.TopLeft);

            Press(window, PhysicalKey.Escape);
            Assert.False(select.IsDropDownOpen);
            await Frames(window, 30);
            Assert.Equal(1, chevron.RenderTransform!.Value.M11, precision: 2);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task WrapTabs_ShowEveryAccountWithoutScrollingAndThumbFollowsAcrossRows()
    {
        var names = new[] { "admin", "readonly-user", "deploy", "ops", "backup-agent", "monitor", "ci-runner", "audit" };
        var tabs = new SegmentedControl { ItemsSource = names, SelectedIndex = 0, Classes = { "compact", "wrap" } };
        var window = new Window { Width = 360, Height = 300, Content = new Border { Padding = new Thickness(16), Child = tabs }, Classes = { "no-motion" } };
        try
        {
            window.Show();
            await Frames(window, 60);
            // 没有滚动区：所有页签都在控件范围内完整可见，并且确实换了行。
            Assert.Empty(tabs.GetVisualDescendants().OfType<ScrollViewer>());
            var items = Enumerable.Range(0, names.Length).Select(i => (Control)tabs.ContainerFromIndex(i)!).ToArray();
            var host = new Rect(tabs.Bounds.Size);
            Assert.All(items, item => Assert.True(host.Contains(item.TranslatePoint(default, tabs)!.Value), "页签被裁掉"));
            Assert.True(items.Select(item => item.TranslatePoint(default, tabs)!.Value.Y).Distinct().Count() > 1);

            // 选中下一行的页签，滑块同时跟到对应的 X 和 Y。
            tabs.SelectedIndex = names.Length - 1;
            await Frames(window, 60);
            var thumb = Thumb(tabs);
            var target = items[^1].TranslatePoint(default, (Visual)thumb.GetVisualParent()!)!.Value;
            Assert.Equal(target.X, thumb.RenderTransform!.Value.M31, precision: 1);
            Assert.Equal(target.Y, thumb.RenderTransform!.Value.M32, precision: 1);
            Assert.Equal(items[^1].Bounds.Width, thumb.Bounds.Width, precision: 1);
        }
        finally { window.Close(); }
    }

    private static Panel Thumb(SegmentedControl segments) =>
        segments.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "PART_Thumb");

    // 选中项在滑块层（滑块的父 Canvas）坐标系里的 X。
    private static double X(Control item, Control thumb) =>
        item.TranslatePoint(default, (Visual)thumb.GetVisualParent()!)!.Value.X;

    private static void Press(Window window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        window.KeyReleaseQwerty(key, RawInputModifiers.None);
    }

    private static async Task Frames(Window window, int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            await Task.Delay(15);
            window.UpdateLayout();
        } while (clock.ElapsedMilliseconds < milliseconds);
    }
}
