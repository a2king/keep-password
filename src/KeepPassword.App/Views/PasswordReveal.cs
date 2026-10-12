using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace KeepPassword.App.Views;

/// <summary>
/// 在密码输入框右侧加入“显示/隐藏密码”的眼睛图标按钮。
/// </summary>
public static class PasswordReveal
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(PasswordReveal));

    static PasswordReveal()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnChanged);
    }

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true)
        {
            box.InnerRightContent = null;
            box.RevealPassword = false;
            return;
        }

        var icon = new PathIcon { Width = 15, Height = 15 };
        var button = new Button
        {
            Classes = { "icon" },
            Focusable = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Content = icon
        };

        void Refresh()
        {
            var key = box.RevealPassword ? "Icon.EyeOff" : "Icon.Eye";
            if (Application.Current?.TryFindResource(key, out var data) == true && data is Geometry geometry)
            {
                icon.Data = geometry;
            }

            ToolTip.SetTip(button, box.RevealPassword ? "隐藏密码" : "显示密码");
        }

        button.Click += (_, _) =>
        {
            box.RevealPassword = !box.RevealPassword;
            Refresh();
        };
        box.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBox.RevealPasswordProperty)
            {
                Refresh();
            }
            else if (change.Property == Visual.IsVisibleProperty && change.NewValue is false)
            {
                box.RevealPassword = false;
            }
        };
        box.DetachedFromVisualTree += (_, _) => box.RevealPassword = false;
        box.InnerRightContent = button;
        Refresh();
    }
}
