using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KeepPassword.App.Controls.Details;

/// <summary>掩码只用于显示，不写回 Value；所有离开字段/窗口/视觉树的路径都重新掩码。</summary>
public class SecretField : FieldRow
{
    public static readonly StyledProperty<bool> RevealedProperty = AvaloniaProperty.Register<SecretField, bool>(nameof(Revealed));
    private Window? _window;
    private readonly List<Control> _ancestors = [];
    protected override Type StyleKeyOverride => typeof(SecretField);
    protected override bool IsValueRevealed => Revealed;
    public bool Revealed { get => GetValue(RevealedProperty); set => SetValue(RevealedProperty, value); }
    public SecretField()
    {
        IsSecret = true;
        IsMono = true;
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Deactivated += OnWindowDeactivated;
            // 页面隐藏通常不会 detach；同时监视祖先，保证返回页面时仍是掩码。
            foreach (var ancestor in this.GetVisualAncestors().OfType<Control>())
            { _ancestors.Add(ancestor); ancestor.PropertyChanged += OnAncestorChanged; }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
            foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorChanged;
            _ancestors.Clear();
            _window = null;
            Conceal();
        };
        LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (!IsKeyboardFocusWithin) Conceal();
        }, DispatcherPriority.Input);
    }
    public void Conceal() => SetCurrentValue(RevealedProperty, false);
    private void OnAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == IsVisibleProperty && !IsEffectivelyVisible) Conceal();
    }
    private void OnWindowDeactivated(object? sender, EventArgs e) => Conceal();
    protected override void OnRevealActivated() => SetCurrentValue(RevealedProperty, !Revealed);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RevealedProperty) UpdatePresentation();
        else if (change.Property == IsVisibleProperty && !IsVisible) Conceal();
    }
}
