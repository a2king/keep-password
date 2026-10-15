using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace KeepPassword.App.Views;

public static class Dialogs
{
    public static Task AlertAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, confirm: false);

    public static Task<bool> ConfirmAsync(Window owner, string title, string message) =>
        ShowAsync(owner, title, message, confirm: true);

    public static Task<bool> ConfirmAsync(Window owner, string title, string message, string okText) =>
        ShowAsync(owner, title, message, confirm: true, okText);

    /// <summary>返回所选项的下标，取消时返回 -1。</summary>
    public static async Task<int> ChooseAsync(Window owner, string title, string message, IReadOnlyList<string> options, string okText)
    {
        var list = new ListBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            MaxHeight = 240,
            Margin = new Thickness(0, 0, 0, 20)
        };
        var accepted = await ShowAsync(owner, title, message, confirm: true, okText, list);
        return accepted ? list.SelectedIndex : -1;
    }

    private static async Task<bool> ShowAsync(Window owner, string title, string message, bool confirm, string? okText = null, Control? extra = null)
    {
        var window = new Window
        {
            Title = title,
            Width = 488,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
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
            Margin = new Thickness(0, 0, 0, extra is null ? 20 : 12)
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

        var ok = new Button { Content = okText ?? (confirm ? "确定" : "知道了"), MinWidth = 76 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            accepted = true;
            window.Close();
        };
        buttons.Children.Add(ok);

        var content = new StackPanel { Children = { titleBlock, text } };
        if (extra is not null)
        {
            content.Children.Add(extra);
        }

        content.Children.Add(buttons);
        var card = new Border
        {
            Classes = { "card" },
            Margin = new Thickness(24, 16, 24, 32),
            BoxShadow = BoxShadows.Parse("0 8 24 0 #33000000"),
            Child = content
        };
        card.PointerPressed += (_, e) =>
        {
            if (extra is not null && e.Source is Visual source && (source == extra || extra.IsVisualAncestorOf(source)))
            {
                return;
            }

            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
            {
                window.BeginMoveDrag(e);
            }
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                window.Close();
            }
            else if (e.Key == Key.Enter)
            {
                e.Handled = true;
                accepted = true;
                window.Close();
            }
        };
        window.Opened += (_, _) => ok.Focus();
        window.Content = card;
        Motion.AttachWindow(window);

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
