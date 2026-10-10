using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

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
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        var accepted = false;
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 17,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        };
        titleBlock.Classes.Add("h2");

        var text = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            FontSize = 13.5,
            Margin = new Thickness(0, 0, 0, 20)
        };
        text.Classes.Add("muted");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10
        };

        if (confirm)
        {
            var cancel = new Button { Content = "取消", MinWidth = 76 };
            cancel.Classes.Add("ghost");
            cancel.Click += (_, _) => window.Close();
            buttons.Children.Add(cancel);
        }

        var ok = new Button { Content = confirm ? "确定" : "知道了", MinWidth = 76 };
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
            Margin = new Thickness(20),
            Child = new StackPanel { Children = { titleBlock, text, buttons } }
        };

        if (owner.IsVisible)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            window.Topmost = true;
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return !confirm || accepted;
    }
}
