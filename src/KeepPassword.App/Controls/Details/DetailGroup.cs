using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace KeepPassword.App.Controls.Details;

/// <summary>一组字段共用圆角外框；用代码联动相邻分割线，避免不支持的 CSS 相邻选择器。</summary>
public class DetailGroup : ContentControl
{
    private readonly List<FieldRow> _rows = [];
    protected override Type StyleKeyOverride => typeof(DetailGroup);
    public DetailGroup()
    {
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Loaded);
        DetachedFromVisualTree += (_, _) => ClearRows();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty && this.GetVisualRoot() is not null)
            Dispatcher.UIThread.Post(RebuildRows, DispatcherPriority.Loaded);
    }
    private void RebuildRows()
    {
        ClearRows();
        if (this.GetVisualRoot() is null) return;
        foreach (var row in this.GetVisualDescendants().OfType<FieldRow>())
        {
            _rows.Add(row);
            row.PointerEntered += OnRowPointer;
            row.PointerExited += OnRowPointer;
            row.PropertyChanged += OnRowPropertyChanged;
        }
        UpdateDividers();
    }
    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            row.PointerEntered -= OnRowPointer;
            row.PointerExited -= OnRowPointer;
            row.PropertyChanged -= OnRowPropertyChanged;
        }
        _rows.Clear();
    }
    private void OnRowPointer(object? sender, PointerEventArgs e) => UpdateDividers();
    private void OnRowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == InputElement.IsKeyboardFocusWithinProperty) UpdateDividers();
    }
    private void UpdateDividers()
    {
        for (var i = 0; i < _rows.Count; i++)
        {
            var current = _rows[i];
            var previous = i > 0 ? _rows[i - 1] : null;
            var active = current.IsPointerOver || current.IsKeyboardFocusWithin;
            var previousActive = previous is not null && (previous.IsPointerOver || previous.IsKeyboardFocusWithin);
            current.IsDividerVisible = i > 0 && !active && !previousActive;
        }
    }
}
