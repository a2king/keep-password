using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KeepPassword.App.Views;

public abstract class FindingsView : UserControl
{
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };

    protected FindingsView(string title, string subtitle)
    {
        var heading = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold };
        heading.Classes.Add("h1");
        var caption = new TextBlock { Text = subtitle, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };
        caption.Classes.Add("muted");
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 18) };
        header.Children.Add(new StackPanel { Spacing = 4, Children = { heading, caption } });
        Grid.SetColumn(_actions, 1);
        header.Children.Add(_actions);

        var layout = new DockPanel { Margin = new Thickness(28, 24), MaxWidth = 760 };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(new ScrollViewer { Content = _body });
        Content = layout;
    }

    protected Button AddAction(string text, bool accent, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button { Content = text };
        button.Classes.Add(accent ? "accent" : "soft");
        button.Click += click;
        _actions.Children.Add(button);
        return button;
    }

    protected void ClearBody() => _body.Children.Clear();

    protected void AddBlock(Control control) => _body.Children.Add(control);

    protected void AddNotice(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
        block.Classes.Add("muted");
        AddBlock(Card(block));
    }

    protected void AddSummary(string title, string description, bool danger)
    {
        var heading = new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        heading.Classes.Add(danger ? "danger" : "success");
        var text = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        text.Classes.Add("muted");
        AddBlock(Card(new StackPanel { Spacing = 8, Children = { heading, text } }));
    }

    protected void AddFindings(IEnumerable<(string Name, string Domain, string Advice)> findings)
    {
        var list = new StackPanel { Spacing = 10 };
        foreach (var (name, domain, advice) in findings)
        {
            var title = new TextBlock
            {
                Text = string.IsNullOrEmpty(domain) ? name : name + " · " + domain,
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap
            };
            var detail = new TextBlock { Text = advice, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 };
            detail.Classes.Add("muted");
            list.Children.Add(Card(new StackPanel { Spacing = 4, Children = { title, detail } }));
        }

        AddBlock(list);
    }

    private static Border Card(Control child)
    {
        var card = new Border { Child = child };
        card.Classes.Add("audit-card");
        return card;
    }
}
