using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KeepPassword.App.Controls;

/// <summary>
/// 液态玻璃分段切换。继承 ListBox，ItemsSource / SelectedIndex / SelectedItem / SelectionChanged、
/// 无障碍语义与 ListBox 完全一致，可直接替换原来的 ListBox 或按钮组。
/// 滑块（PART_Thumb）是模板里独立于各选项的一个元素，位置按选中项的实际 Bounds 计算，
/// 所以窗口缩放、文字变化、横向滚动后都会重新对齐。
/// </summary>
public class SegmentedControl : ListBox
{
    protected override Type StyleKeyOverride => typeof(SegmentedControl);

    private Control? _thumb;
    private Visual? _layer;
    private Rect _placed;
    private int _placedIndex = -1;
    private bool _hasPlaced;
    private bool _updateQueued;

    public SegmentedControl()
    {
        // 分段切换总有一项被选中；点击已选项不会取消选择。
        SelectionMode = SelectionMode.Single | SelectionMode.AlwaysSelected;
        // ListBoxItem 会把指针按下标记为已处理，这里用 handledEventsToo 仍能拿到按压态。
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerReleasedEvent, OnAnyPointerReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, true);
        AddHandler(PointerCaptureLostEvent, (_, _) => PseudoClasses.Set(":pressed", false), RoutingStrategies.Bubble, true);
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _thumb = e.NameScope.Find<Control>("PART_Thumb");
        _layer = e.NameScope.Find<Visual>("PART_SelectionLayer");
        _hasPlaced = false;
        QueueUpdate();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += OnLayoutUpdated;
        _hasPlaced = false;
        QueueUpdate();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= OnLayoutUpdated;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedIndexProperty) QueueUpdate();
        else if (change.Property == IsKeyboardFocusWithinProperty && !IsKeyboardFocusWithin) PseudoClasses.Set(":keyboard-focus", false);
    }

    protected override void OnGotFocus(GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        // 焦点环只给键盘用户：Tab / 方向键进入时显示，鼠标点击不显示。
        PseudoClasses.Set(":keyboard-focus", e.NavigationMethod is NavigationMethod.Tab or NavigationMethod.Directional);
    }

    // 窗口缩放、文字变化、滚动都会触发布局；此处只比较矩形，未变化时立即返回，开销很小。
    private void OnLayoutUpdated(object? sender, EventArgs e) => UpdateThumb();

    // 选中变化不一定引起重新布局（例如只换颜色），所以再补一次排队更新。
    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.UIThread.Post(() => { _updateQueued = false; UpdateThumb(); }, DispatcherPriority.Loaded);
    }

    private void UpdateThumb()
    {
        if (_thumb is null || _layer is null || !IsEffectivelyVisible) return;
        var index = SelectedIndex;
        if (index < 0 || ContainerFromIndex(index) is not Control container || container.Bounds.Width <= 0)
        {
            _thumb.IsVisible = false;
            return;
        }
        // 选中项原点换算到滑块层坐标：两者同处一个滚动内容里，横向滚动时一起移动，无需额外修正。
        if (container.TranslatePoint(default, _layer) is not { } origin) return;
        var target = new Rect(origin, container.Bounds.Size);
        if (_hasPlaced && index == _placedIndex && target == _placed) return;
        // 只有"已定位过且选中项变化"才滑动；首次出现、缩放、文字变化都直接落位，避免无意义的位移动画。
        var animate = _hasPlaced && index != _placedIndex;
        _placed = target;
        _placedIndex = index;
        _hasPlaced = true;
        _thumb.IsVisible = true;
        Place(target, animate);
    }

    private void Place(Rect target, bool animate)
    {
        // 不需要动画时先撤掉过渡再赋值（会同时终止进行中的过渡），赋值后恢复主题里的过渡定义。
        if (!animate) _thumb!.Transitions = null;
        _thumb!.Width = target.Width;
        _thumb.Height = target.Height;
        _thumb.RenderTransform = TransformOperations.Parse(FormattableString.Invariant(
            $"translate({target.X:0.###}px, {target.Y:0.###}px)"));
        if (!animate) _thumb.ClearValue(TransitionsProperty);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // ← → 循环切换、Home / End 跳首尾，跳过禁用项；↑ ↓ 等其余按键交给 ListBox 默认处理。
        if (!e.Handled && ItemCount > 0 && e.Key is Key.Left or Key.Right or Key.Home or Key.End)
        {
            var next = e.Key switch
            {
                Key.Home => FirstSelectable(0, 1),
                Key.End => FirstSelectable(ItemCount - 1, -1),
                Key.Left => FirstSelectable(Wrap(SelectedIndex - 1), -1),
                _ => FirstSelectable(Wrap(SelectedIndex + 1), 1)
            };
            if (next >= 0 && next != SelectedIndex)
            {
                SelectedIndex = next;
                (ContainerFromIndex(next) as Control)?.Focus(NavigationMethod.Directional);
            }
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    private int Wrap(int index) => (index % ItemCount + ItemCount) % ItemCount;

    private int FirstSelectable(int start, int step)
    {
        for (var offset = 0; offset < ItemCount; offset++)
        {
            var index = step > 0 ? Wrap(start + offset) : Wrap(start - offset);
            if (ContainerFromIndex(index) is not Control { IsEnabled: false }) return index;
        }
        return -1;
    }

    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // 只有按在可用选项上才进入按压态（滑块放大），按在空白处或禁用项上不变形。
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        PseudoClasses.Set(":pressed", IsEffectivelyEnabled && item is { IsEffectivelyEnabled: true }
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed);
    }

    private void OnAnyPointerReleased(object? sender, PointerReleasedEventArgs e) => PseudoClasses.Set(":pressed", false);
}
