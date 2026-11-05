using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace KeepPassword.App.Controls.Details;

public sealed record DetailQuickAction(string Label, string IconResource, Func<KeyModifiers, Task> Invoke, string? Hint = null);

/// <summary>仅渲染可用操作；值在点击时读取，切换账号后不会复制上一个账号。</summary>
public sealed class QuickActionBar : ContentControl
{
    protected override Type StyleKeyOverride => typeof(QuickActionBar);
    private readonly List<Button> _buttons = [];
    private IReadOnlyList<DetailQuickAction> _actions = [];

    public void SetActions(IReadOnlyList<DetailQuickAction> actions)
    {
        var unchanged = _actions.Select(action => action.Label).SequenceEqual(actions.Select(action => action.Label));
        _actions = actions;
        IsVisible = actions.Count > 0;
        if (unchanged && _buttons.Count == actions.Count) return;
        _buttons.Clear();
        var grid = new UniformGrid { Rows = 1 };
        for (var i = 0; i < actions.Count; i++)
        {
            var index = i;
            var action = actions[i];
            var icon = new ShapePath { Width = 14, Height = 14, StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round, Stretch = Stretch.Uniform, Fill = Brushes.Transparent };
            icon.Bind(ShapePath.DataProperty, this.GetResourceObservable(action.IconResource));
            icon.Bind(ShapePath.StrokeProperty, this.GetResourceObservable("Detail.AccentText"));
            // 图标与文字横排，卡片更矮；文字过长时省略，不撑宽相邻卡片。
            var body = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
            icon.VerticalAlignment = VerticalAlignment.Center;
            body.Children.Add(icon);
            body.Children.Add(new TextBlock { Text = action.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Avalonia.Thickness(3, 0) };
            button.Classes.Add("detail-quick-action");
            ToolTip.SetTip(button, action.Hint ?? action.Label);
            var modifiers = KeyModifiers.None;
            button.PointerPressed += (_, e) => modifiers = e.KeyModifiers;
            button.KeyDown += (_, e) => modifiers = e.KeyModifiers;
            button.Click += async (_, _) =>
            {
                var current = modifiers;
                modifiers = KeyModifiers.None;
                if (index < _actions.Count) await _actions[index].Invoke(current);
            };
            _buttons.Add(button);
            grid.Children.Add(button);
        }
        Content = grid;
    }
}
