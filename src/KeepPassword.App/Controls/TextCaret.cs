using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KeepPassword.App.Controls;

/// <summary>
/// 接管所有 TextBox 的光标绘制。Avalonia 11.3 的 TextPresenter 把光标画成 Floor(x)+0.5 处的 1px 抗锯齿线，
/// 光标在行尾时再左移 1px，于是正常打字时光标总压在最后一个字符的右侧笔画上（中文、d、f、l 尤其明显，
/// 125%/150% 缩放下还会糊成两个半透明像素）。这里把原生光标设为透明，在同一滚动内容里叠一层，
/// 把光标按设备像素对齐画在字符前进位置之后；行尾额外留出光标宽度，长文本滚到末尾时光标不被裁掉。
/// </summary>
public sealed class TextCaret : Control
{
    private const double TrailingRoom = 2;

    // 光标位置（含折行处归属哪一行、输入法预编辑偏移）只有 TextPresenter 内部方法给得准；取不到时退回按字符索引命中测试。
    private static readonly Func<TextPresenter, Rect>? s_cursorRectangle =
        typeof(TextPresenter).GetMethod("GetCursorRectangle", BindingFlags.Instance | BindingFlags.NonPublic, Type.EmptyTypes)
            ?.CreateDelegate<Func<TextPresenter, Rect>>();

    private readonly TextBox _box;
    private readonly TextPresenter _presenter;
    private readonly DispatcherTimer _blink = new();
    private bool _blinkOn = true;

    private TextCaret(TextBox box, TextPresenter presenter)
    {
        _box = box;
        _presenter = presenter;
        IsHitTestVisible = false;
        _blink.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            InvalidateVisual();
        };
    }

    public static void Install() =>
        TemplatedControl.TemplateAppliedEvent.AddClassHandler<TextBox>((box, e) => Attach(box, e.NameScope));

    private static void Attach(TextBox box, INameScope scope)
    {
        // 只处理 Fluent 模板的结构（TextPresenter 放在 ScrollViewer 内的 Panel 里）；自定义模板保持原生光标。
        if (scope.Find<TextPresenter>("PART_TextPresenter") is not { } presenter || presenter.GetVisualParent() is not Panel host) return;
        if (host.Children.OfType<TextCaret>().Any()) return;
        presenter.CaretBrush = Brushes.Transparent;
        presenter.Margin += new Thickness(0, 0, TrailingRoom, 0);
        host.Children.Add(new TextCaret(box, presenter));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _box.PropertyChanged += OnBoxPropertyChanged;
        _presenter.PropertyChanged += OnPresenterPropertyChanged;
        _presenter.CaretBoundsChanged += OnCaretMoved;
        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _box.PropertyChanged -= OnBoxPropertyChanged;
        _presenter.PropertyChanged -= OnPresenterPropertyChanged;
        _presenter.CaretBoundsChanged -= OnCaretMoved;
        _blink.Stop();
    }

    private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsFocusedProperty || e.Property == TextBox.CaretBlinkIntervalProperty) Restart();
        else if (e.Property == TextBox.CaretBrushProperty || e.Property == TextBox.ForegroundProperty) InvalidateVisual();
    }

    private void OnPresenterPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty || e.Property == TextPresenter.SelectionStartProperty || e.Property == TextPresenter.SelectionEndProperty)
            InvalidateVisual();
    }

    private void OnCaretMoved(object? sender, EventArgs e)
    {
        Restart();
        // TextPresenter 自己只把零宽的光标位置滚进视口，行尾那条光标会落在视口外；排在它之后再补一次。
        if (_box.IsFocused) Dispatcher.UIThread.Post(BringCaretIntoView, DispatcherPriority.Background);
    }

    // 获得焦点、移动光标或输入时光标先常亮，再开始闪烁，与系统输入框一致。
    private void Restart()
    {
        _blink.Stop();
        _blinkOn = true;
        var interval = _box.CaretBlinkInterval;
        if (_box.IsFocused && interval > TimeSpan.Zero)
        {
            _blink.Interval = interval;
            _blink.Start();
        }
        InvalidateVisual();
    }

    private void BringCaretIntoView()
    {
        if (!_box.IsFocused || _presenter.GetVisualParent() is null) return;
        var caret = CaretBounds();
        _presenter.BringIntoView(new Rect(caret.X, caret.Y, TrailingRoom, caret.Height));
    }

    private Rect CaretBounds()
    {
        if (s_cursorRectangle is { } cursorRectangle) return cursorRectangle(_presenter);
        var preedit = _presenter.PreeditText;
        var index = _presenter.CaretIndex + (string.IsNullOrEmpty(preedit) ? 0 : _presenter.PreeditTextCursorPosition ?? preedit.Length);
        return _presenter.TextLayout.HitTestTextPosition(index);
    }

    public override void Render(DrawingContext context)
    {
        if (!_blinkOn || !_box.IsFocused || _presenter.SelectionStart != _presenter.SelectionEnd) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel || this.TranslatePoint(default, topLevel) is not { } origin) return;

        var caret = CaretBounds().Translate(_presenter.Bounds.Position);
        if (caret.Height <= 0) return;

        // 左缘取字符前进位置之后的第一个设备像素，宽度取整个设备像素：不压字，也不因抗锯齿发虚。
        var scale = topLevel.RenderScaling;
        var left = Math.Ceiling((origin.X + caret.X) * scale - 0.01);
        var top = Math.Round((origin.Y + caret.Y) * scale);
        var bottom = Math.Round((origin.Y + caret.Bottom) * scale);
        var width = Math.Max(1, Math.Round(scale));
        var rect = new Rect(left / scale - origin.X, top / scale - origin.Y, width / scale, (bottom - top) / scale);
        context.FillRectangle(_box.CaretBrush ?? _box.Foreground ?? Brushes.Black, rect);
    }
}
