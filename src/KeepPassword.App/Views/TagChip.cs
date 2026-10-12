using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace KeepPassword.App.Views;

/// <summary>
/// 描边样式的标签，颜色按名称稳定散列到主题调色板。
/// </summary>
public sealed class TagChip : Border
{
    public const int PaletteSize = 11;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TagChip, string?>(nameof(Text));

    private readonly TextBlock _label = new()
    {
        FontSize = 11.5,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly List<IDisposable> _colors = [];

    public TagChip()
    {
        Child = _label;
        Height = 20;
        CornerRadius = new CornerRadius(4);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(7, 0);
        ApplyColor();
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public static int ColorIndex(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % PaletteSize);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty)
        {
            _label.Text = Text;
            ApplyColor();
        }
    }

    private void ApplyColor()
    {
        foreach (var binding in _colors)
        {
            binding.Dispose();
        }

        _colors.Clear();
        var key = $"Tag.{ColorIndex(Text ?? "")}";
        _colors.Add(Bind(BackgroundProperty, this.GetResourceObservable(key + ".Bg")));
        _colors.Add(Bind(BorderBrushProperty, this.GetResourceObservable(key + ".Border")));
        _colors.Add(_label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(key + ".Fg")));
    }
}

/// <summary>
/// 单行排列标签：放不下的最后一个标签截断为省略号，其余不显示。
/// </summary>
public sealed class TagStrip : Panel
{
    private const double Spacing = 4;
    private const double MinTruncatedWidth = 36;

    private readonly List<Rect> _slots = [];

    public TagStrip()
    {
        ClipToBounds = true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _slots.Clear();
        var limit = availableSize.Width;
        double x = 0, height = 0;
        var full = false;
        foreach (var child in Children)
        {
            child.Measure(Size.Infinity);
            var size = child.DesiredSize;
            height = Math.Max(height, size.Height);
            var start = x == 0 ? 0 : x + Spacing;
            if (full)
            {
                _slots.Add(default);
            }
            else if (start + size.Width <= limit)
            {
                _slots.Add(new Rect(start, 0, size.Width, size.Height));
                x = start + size.Width;
            }
            else
            {
                full = true;
                var remain = limit - start;
                if (remain >= MinTruncatedWidth)
                {
                    child.Measure(new Size(remain, size.Height));
                    _slots.Add(new Rect(start, 0, remain, size.Height));
                    x = limit;
                }
                else
                {
                    _slots.Add(default);
                }
            }
        }

        return new Size(double.IsInfinity(limit) ? x : Math.Min(x, limit), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < Children.Count; i++)
        {
            var slot = i < _slots.Count ? _slots[i] : default;
            Children[i].Arrange(slot.Width > 0 ? slot.WithHeight(finalSize.Height) : new Rect(finalSize.Width + 1000, 0, 0, 0));
        }

        return finalSize;
    }
}
