using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Views;

public partial class LabelPicker : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<LabelChipView>?> ItemsProperty =
        AvaloniaProperty.Register<LabelPicker, IReadOnlyList<LabelChipView>?>(nameof(Items));

    public static readonly StyledProperty<bool> MultipleProperty =
        AvaloniaProperty.Register<LabelPicker, bool>(nameof(Multiple));

    public static readonly StyledProperty<string?> WatermarkProperty =
        AvaloniaProperty.Register<LabelPicker, string?>(nameof(Watermark));

    public static readonly StyledProperty<string> CreateTextProperty =
        AvaloniaProperty.Register<LabelPicker, string>(nameof(CreateText), "创建");

    public static readonly StyledProperty<Geometry?> IconProperty =
        AvaloniaProperty.Register<LabelPicker, Geometry?>(nameof(Icon));

    private List<LabelChipView> _filtered = [];
    private int _highlight = -1;

    public LabelPicker()
    {
        InitializeComponent();
        Input.TextChanged += (_, _) =>
        {
            _highlight = -1;
            RebuildOptions();
            if (!string.IsNullOrEmpty(Input.Text))
            {
                Open();
            }
        };
        Input.GotFocus += (_, _) => Open();
        Input.KeyDown += OnInputKeyDown;
        Box.AddHandler(PointerPressedEvent, OnBoxPressed, RoutingStrategies.Tunnel);
        Dropdown.Closed += (_, _) => Input.Text = "";
    }

    public IReadOnlyList<LabelChipView>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public bool Multiple
    {
        get => GetValue(MultipleProperty);
        set => SetValue(MultipleProperty, value);
    }

    public string? Watermark
    {
        get => GetValue(WatermarkProperty);
        set => SetValue(WatermarkProperty, value);
    }

    public string CreateText
    {
        get => GetValue(CreateTextProperty);
        set => SetValue(CreateTextProperty, value);
    }

    public Geometry? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public event Action<string>? Toggled;

    public event Action<string>? Created;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            RebuildChips();
            RebuildOptions();
        }
        else if (change.Property == WatermarkProperty)
        {
            Input.Watermark = Watermark;
        }
        else if (change.Property == IconProperty && Icon is not null)
        {
            Glyph.Data = Icon;
        }
    }

    private string Filter => LabelName.Normalize(Input.Text);

    private void OnBoxPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        Input.Focus();
        Open();
    }

    private void Open()
    {
        if (!IsEffectivelyEnabled)
        {
            return;
        }

        Dropdown.MinWidth = Box.Bounds.Width;
        RebuildOptions();
        Dropdown.IsOpen = true;
    }

    private void Close() => Dropdown.IsOpen = false;

    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Commit();
                break;
            case Key.Escape:
                if (Dropdown.IsOpen)
                {
                    e.Handled = true;
                    Close();
                }

                break;
            case Key.Down:
                e.Handled = true;
                Open();
                MoveHighlight(1);
                break;
            case Key.Up:
                e.Handled = true;
                MoveHighlight(-1);
                break;
            case Key.Tab:
                Close();
                break;
            case Key.Back when string.IsNullOrEmpty(Input.Text) && Multiple:
                if (Items?.LastOrDefault(item => item.IsSelected) is { } last)
                {
                    e.Handled = true;
                    Toggled?.Invoke(last.Name);
                }

                break;
        }
    }

    private void Commit()
    {
        if (_highlight >= 0 && _highlight < _filtered.Count)
        {
            Choose(_filtered[_highlight].Name);
            return;
        }

        var filter = Filter;
        if (filter.Length == 0)
        {
            return;
        }

        var exact = Items?.FirstOrDefault(item => LabelName.Comparer.Equals(item.Name, filter));
        if (exact is not null)
        {
            Choose(exact.Name);
        }
        else if (_filtered.Count > 0)
        {
            Choose(_filtered[0].Name);
        }
        else
        {
            Create(filter);
        }
    }

    private void MoveHighlight(int delta)
    {
        if (_filtered.Count == 0)
        {
            return;
        }

        _highlight = _highlight < 0 && delta < 0
            ? _filtered.Count - 1
            : (_highlight + delta + _filtered.Count) % _filtered.Count;
        RebuildOptions();
    }

    private void Choose(string name)
    {
        Input.Text = "";
        Toggled?.Invoke(name);
        if (!Multiple)
        {
            Close();
        }

        Input.Focus();
    }

    private void Create(string name)
    {
        Input.Text = "";
        Created?.Invoke(name);
        if (!Multiple)
        {
            Close();
        }

        Input.Focus();
    }

    private void RebuildChips()
    {
        Chips.Children.Clear();
        foreach (var item in Items?.Where(item => item.IsSelected) ?? [])
        {
            var remove = new Button { Content = "×", Tag = item.Name };
            remove.Classes.Add("picker-chip-remove");
            ToolTip.SetTip(remove, "移除");
            remove.Click += (_, e) =>
            {
                e.Handled = true;
                Toggled?.Invoke((string)remove.Tag!);
            };
            var chip = new Border
            {
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        Themed(new TextBlock { Text = item.Name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center }, TextBlock.ForegroundProperty, "Kp.Accent"),
                        remove
                    }
                }
            };
            chip.Classes.Add("picker-chip");
            Chips.Children.Add(chip);
        }

        Chips.IsVisible = Chips.Children.Count > 0;
    }

    private void RebuildOptions()
    {
        Options.Children.Clear();
        var filter = Filter;
        var items = Items ?? [];
        _filtered = items
            .Where(item => filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (_highlight >= _filtered.Count)
        {
            _highlight = _filtered.Count - 1;
        }

        for (var i = 0; i < _filtered.Count; i++)
        {
            var item = _filtered[i];
            var button = new Button { Content = OptionContent(item), Tag = item.Name };
            button.Classes.Add("picker-option");
            if (item.IsSelected)
            {
                button.Classes.Add("selected");
            }

            if (i == _highlight)
            {
                button.Classes.Add("highlight");
            }

            button.Click += (_, _) => Choose((string)button.Tag!);
            Options.Children.Add(button);
        }

        var hasExact = items.Any(item => LabelName.Comparer.Equals(item.Name, filter));
        if (filter.Length > 0 && !hasExact)
        {
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            text.Inlines!.Add(Themed(new Run(CreateText + " "), TextElement.ForegroundProperty, "Kp.TextMuted"));
            text.Inlines.Add(new Run(filter) { FontWeight = FontWeight.SemiBold });
            var create = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        Themed(new PathIcon { Data = (Geometry)this.FindResource("Icon.Add")!, Width = 12, Height = 12 }, ForegroundProperty, "Kp.Accent"),
                        text
                    }
                }
            };
            create.Classes.Add("picker-option");
            if (_filtered.Count == 0)
            {
                create.Classes.Add("highlight");
            }

            create.Click += (_, _) => Create(filter);
            Options.Children.Add(create);
        }

        if (Options.Children.Count == 0)
        {
            var empty = new TextBlock { Text = "输入名称即可创建", Margin = new Thickness(10, 8), FontSize = 12.5 };
            empty.Classes.Add("muted");
            Options.Children.Add(empty);
        }
    }

    private Control OptionContent(LabelChipView item)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock { Text = item.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        if (item.IsSelected)
        {
            var check = Themed(
                new PathIcon
                {
                    Data = (Geometry)this.FindResource("Icon.Check")!,
                    Width = 13,
                    Height = 13,
                    Margin = new Thickness(8, 0, 0, 0)
                },
                ForegroundProperty,
                "Kp.Accent");
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);
        }

        return grid;
    }

    private T Themed<T>(T target, AvaloniaProperty property, string key)
        where T : AvaloniaObject
    {
        target.Bind(property, this.GetResourceObservable(key));
        return target;
    }
}
