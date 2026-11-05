using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using KeepPassword.App.Services;

namespace KeepPassword.App.Views;

/// <summary>
/// 可复用的卡片指针光晕。模板提供 PART_Spotlight 图层、圆角裁剪和透明度过渡；
/// 行为只负责每张卡片自己的渐变画刷、指针位置、选中强度和生命周期清理。
/// </summary>
public static class SpotlightBehavior
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(SpotlightBehavior));

    public static readonly AttachedProperty<double> RadiusProperty =
        AvaloniaProperty.RegisterAttached<Control, double>("Radius", typeof(SpotlightBehavior), 140);

    public static readonly AttachedProperty<bool> IsMotionEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsMotionEnabled", typeof(SpotlightBehavior), true);

    // 弱键表不会因行为状态而延长已经移除的卡片实例的存活时间。
    private static readonly ConditionalWeakTable<Control, State> States = new();

    static SpotlightBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, change) =>
        {
            if (change.NewValue is true)
            {
                States.GetValue(control, target => new State(target)).Enable();
            }
            else if (States.TryGetValue(control, out var state))
            {
                state.Disable();
            }
        });
        RadiusProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (States.TryGetValue(control, out var state))
            {
                state.UpdateRadius();
            }
        });
        IsMotionEnabledProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            if (States.TryGetValue(control, out var state))
            {
                state.RefreshVisibility();
            }
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);
    public static double GetRadius(Control control) => control.GetValue(RadiusProperty);
    public static void SetRadius(Control control, double value) => control.SetValue(RadiusProperty, value);
    public static bool GetIsMotionEnabled(Control control) => control.GetValue(IsMotionEnabledProperty);
    public static void SetIsMotionEnabled(Control control, bool value) => control.SetValue(IsMotionEnabledProperty, value);

    private sealed class State(Control target)
    {
        private readonly List<Control> _ancestors = [];
        // 每张卡片只创建一支可变画刷，避免共享渐变导致多张卡片的光晕一起移动。
        private readonly RadialGradientBrush _brush = new()
        {
            // 光晕位置使用控件内的绝对坐标，尺寸变化不会把指针位置按百分比缩放。
            Center = new RelativePoint(0, 0, RelativeUnit.Absolute),
            GradientOrigin = new RelativePoint(0, 0, RelativeUnit.Absolute),
            GradientStops =
            [
                new GradientStop { Offset = 0 },
                new GradientStop { Offset = 1, Color = Colors.Transparent }
            ]
        };
        private Border? _spotlight;
        private IDisposable? _colorBinding;
        private bool _enabled;
        private bool _attached;
        private bool _hovered;
        private bool _motionAllowed;
        private double _requestedOpacity;

        public void Enable()
        {
            if (_enabled)
            {
                return;
            }

            _enabled = true;
            // 启用时仅注册挂载入口；指针、主题和祖先监听在进入视觉树后才注册。
            target.AttachedToVisualTree += OnAttached;
            target.DetachedFromVisualTree += OnDetached;
            if (target.GetVisualRoot() is not null)
            {
                Attach();
            }
        }

        public void Disable()
        {
            _enabled = false;
            target.AttachedToVisualTree -= OnAttached;
            target.DetachedFromVisualTree -= OnDetached;
            Detach();
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Attach();
        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => Detach();

        private void Attach()
        {
            if (_attached || !_enabled)
            {
                return;
            }

            _attached = true;
            _motionAllowed = MotionPreferences.IsEnabled;
            MotionPreferences.Changed += OnMotionChanged;
            target.PointerEntered += OnPointerEntered;
            target.PointerMoved += OnPointerMoved;
            target.PointerExited += OnPointerExited;
            target.PropertyChanged += OnTargetPropertyChanged;
            if (target is TemplatedControl templated)
            {
                templated.TemplateApplied += OnTemplateApplied;
            }

            // 仅挂载时构建祖先列表；指针移动时复用，避免逐帧遍历和分配集合。
            for (Visual? current = target; current is not null; current = current.GetVisualParent())
            {
                if (current is Control control)
                {
                    _ancestors.Add(control);
                    control.Classes.CollectionChanged += OnClassesChanged;
                    control.PropertyChanged += OnAncestorPropertyChanged;
                }
            }

            // 从当前控件的资源作用域绑定颜色，主题切换时原画刷即可更新，无需重建。
            _colorBinding = _brush.GradientStops[0].Bind(
                GradientStop.ColorProperty, target.GetResourceObservable("CardSpotlightColor"));
            UpdateRadius();
            FindSpotlight();
        }

        private void Detach()
        {
            // 虚拟化回收/禁用时立即熄灭光晕并释放资源订阅，不等待淡出动画。
            _hovered = false;
            ClearSpotlight();
            _colorBinding?.Dispose();
            _colorBinding = null;
            if (!_attached)
            {
                return;
            }

            _attached = false;
            MotionPreferences.Changed -= OnMotionChanged;
            target.PointerEntered -= OnPointerEntered;
            target.PointerMoved -= OnPointerMoved;
            target.PointerExited -= OnPointerExited;
            target.PropertyChanged -= OnTargetPropertyChanged;
            if (target is TemplatedControl templated)
            {
                templated.TemplateApplied -= OnTemplateApplied;
            }

            foreach (var ancestor in _ancestors)
            {
                ancestor.Classes.CollectionChanged -= OnClassesChanged;
                ancestor.PropertyChanged -= OnAncestorPropertyChanged;
            }
            _ancestors.Clear();
        }

        private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
        {
            ClearSpotlight();
            SetSpotlight(e.NameScope.Find<Border>("PART_Spotlight"));
        }

        private void FindSpotlight()
        {
            foreach (var visual in target.GetVisualDescendants())
            {
                if (visual is Border { Name: "PART_Spotlight" } layer
                    && ReferenceEquals(layer.TemplatedParent, target))
                {
                    SetSpotlight(layer);
                    break;
                }
            }
        }

        private void SetSpotlight(Border? layer)
        {
            _spotlight = layer;
            if (layer is null)
            {
                return;
            }

            // TemplateApplied 发生时图层可能仍在应用样式；画刷由行为独占，使用本地值
            // 避免 SetCurrentValue 的临时默认值在样式初始化完成后被覆盖成 null。
            layer.Background = _brush;
            HideImmediately(layer);
        }

        private void ClearSpotlight()
        {
            if (_spotlight is not { } layer)
            {
                return;
            }

            HideImmediately(layer);
            layer.ClearValue(Border.BackgroundProperty);
            _spotlight = null;
        }

        public void UpdateRadius()
        {
            var requested = GetRadius(target);
            var radius = double.IsFinite(requested) && requested > 0 ? requested : 140;
            // 横纵半径均使用像素值，宽卡片和窄卡片都保持相同的 140px 光晕尺寸。
            var absolute = new RelativeScalar(radius, RelativeUnit.Absolute);
            _brush.RadiusX = absolute;
            _brush.RadiusY = absolute;
        }

        private bool CanShow()
        {
            if (!_enabled || !_attached || !target.IsEffectivelyVisible
                || !target.IsEffectivelyEnabled || !_motionAllowed)
            {
                return false;
            }

            foreach (var ancestor in _ancestors)
            {
                if (!GetIsMotionEnabled(ancestor) || ancestor.Classes.Contains("no-motion"))
                {
                    return false;
                }
            }
            return true;
        }

        private bool IsSelected => target.Classes.Contains("selected")
            || target is ListBoxItem { IsSelected: true };

        public void RefreshVisibility()
        {
            if (_spotlight is not { } layer)
            {
                return;
            }

            // 隐藏/禁用可能没有 PointerExited；清掉悬浮标记，防止重新显示时出现残留光晕。
            if (!target.IsEffectivelyVisible || !target.IsEffectivelyEnabled)
            {
                _hovered = false;
            }

            if (!CanShow())
            {
                HideImmediately(layer);
                return;
            }

            // 选中强调由模板负责；光晕减半，避免掩盖选中底色。
            SetOpacity(layer, _hovered ? IsSelected ? 0.5 : 1 : 0);
        }

        private void OnMotionChanged()
        {
            _motionAllowed = MotionPreferences.IsEnabled;
            RefreshVisibility();
        }

        private void OnClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) => RefreshVisibility();

        private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == IsMotionEnabledProperty || e.Property == Visual.IsVisibleProperty)
            {
                RefreshVisibility();
            }
        }

        private void OnTargetPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ListBoxItem.IsSelectedProperty
                || e.Property == Visual.IsVisibleProperty
                || e.Property == InputElement.IsEffectivelyEnabledProperty)
            {
                RefreshVisibility();
            }
        }

        private void OnPointerEntered(object? sender, PointerEventArgs e)
        {
            // 只在进入/偏好变更时读取系统动效设置，避免每次 PointerMoved 都调用 Win32。
            _motionAllowed = MotionPreferences.IsEnabled;
            _hovered = true;
            UpdatePointer(e);
        }

        private void OnPointerMoved(object? sender, PointerEventArgs e)
        {
            if (_hovered)
            {
                UpdatePointer(e);
            }
        }

        private void UpdatePointer(PointerEventArgs e)
        {
            if (_spotlight is not { } layer)
            {
                return;
            }

            if (!CanShow())
            {
                HideImmediately(layer);
                return;
            }

            // RelativePoint 是值类型；逐帧仅改坐标和必要的透明度，不创建画刷、渐变或计时器。
            var center = new RelativePoint(e.GetPosition(layer), RelativeUnit.Absolute);
            _brush.Center = center;
            _brush.GradientOrigin = center;
            SetOpacity(layer, IsSelected ? 0.5 : 1);
        }

        private void OnPointerExited(object? sender, PointerEventArgs e)
        {
            _hovered = false;
            RefreshVisibility();
        }

        private void HideImmediately(Border layer)
        {
            // 临时停用过渡后清零，确保锁定、隐藏和虚拟化回收不会留下正在淡出的光晕。
            _requestedOpacity = 0;
            var transitions = layer.Transitions;
            layer.SetCurrentValue(Animatable.TransitionsProperty, null);
            layer.SetCurrentValue(Visual.OpacityProperty, 0);
            layer.SetCurrentValue(Animatable.TransitionsProperty, transitions);
        }

        private void SetOpacity(Border layer, double opacity)
        {
            // 缓存目标值，移动指针时不反复重设同一透明度，也不重启动画。
            if (_requestedOpacity != opacity)
            {
                _requestedOpacity = opacity;
                layer.SetCurrentValue(Visual.OpacityProperty, opacity);
            }
        }
    }
}
