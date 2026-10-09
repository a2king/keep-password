using Avalonia.Controls;
using Avalonia.Layout;

namespace KeepPassword.App.Views;

public static class Dialogs
{
    public static Task AlertAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, confirm: false);

    public static Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, confirm: true);

    private static async Task<bool> ShowAsync(Window owner, string title, string message, bool confirm)
    {
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var accepted = false;
        var text = new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Avalonia.Thickness(0, 0, 0, 16)
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8
        };
        if (confirm)
        {
            var cancel = new Button { Content = "取消", MinWidth = 72 };
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = confirm ? "确定" : "知道了", MinWidth = 72 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        buttons.Children.Add(ok);
        window.Content = new Border
        {
            Classes = { "card" },
            Margin = new Avalonia.Thickness(20),
            Child = new StackPanel { Children = { text, buttons } }
        };
        await window.ShowDialog(owner);
        return !confirm || accepted;
    }
}
