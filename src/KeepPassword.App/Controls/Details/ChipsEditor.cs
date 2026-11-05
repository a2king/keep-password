using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Controls.Details;

/// <summary>默认只显示已选标签；点击添加才进入小型输入/建议态，不复制业务选择逻辑。</summary>
public sealed class ChipsEditor : TemplatedControl
{
    public static readonly StyledProperty<IReadOnlyList<LabelChipView>?> ItemsProperty = AvaloniaProperty.Register<ChipsEditor, IReadOnlyList<LabelChipView>?>(nameof(Items));
    public static readonly StyledProperty<bool> MultipleProperty = AvaloniaProperty.Register<ChipsEditor, bool>(nameof(Multiple));
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<ChipsEditor, string>(nameof(Label), "标签");
    public static readonly StyledProperty<string> PlaceholderProperty = AvaloniaProperty.Register<ChipsEditor, string>(nameof(Placeholder), "选择或输入标签");
    private readonly List<Control> _chips = [];
    private readonly List<LabelChipView> _observedItems = [];
    private List<LabelChipView> _filtered = [];
    private INotifyCollectionChanged? _collection;
    private WrapPanel? _flow;
    private Button? _add;
    private TextBox? _input;
    private Popup? _popup;
    private StackPanel? _options;
    private bool _editing;
    private bool _attached;
    private int _highlight = -1;

    protected override Type StyleKeyOverride => typeof(ChipsEditor);
    public IReadOnlyList<LabelChipView>? Items { get => GetValue(ItemsProperty); set => SetValue(ItemsProperty, value); }
    public bool Multiple { get => GetValue(MultipleProperty); set => SetValue(MultipleProperty, value); }
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public event Action<string>? Toggled;
    public event Action<string>? Created;

    public ChipsEditor()
    {
        AttachedToVisualTree += (_, _) => { _attached = true; ObserveItems(); RebuildChips(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; ClosePopup(); StopObserving(); };
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_add is not null) _add.Click -= OnAdd;
        if (_input is not null) { _input.TextChanged -= OnTextChanged; _input.KeyDown -= OnInputKeyDown; _input.LostFocus -= OnInputLostFocus; }
        if (_popup is not null) { _popup.Closed -= OnPopupClosed; _popup.IsOpen = false; }
        _chips.Clear();
        base.OnApplyTemplate(e);
        _flow = e.NameScope.Find<WrapPanel>("PART_Flow");
        _add = e.NameScope.Find<Button>("PART_Add");
        _input = e.NameScope.Find<TextBox>("PART_Input");
        _popup = e.NameScope.Find<Popup>("PART_Suggestions");
        _options = e.NameScope.Find<StackPanel>("PART_Options");
        if (_add is not null) _add.Click += OnAdd;
        if (_input is not null) { _input.TextChanged += OnTextChanged; _input.KeyDown += OnInputKeyDown; _input.LostFocus += OnInputLostFocus; }
        if (_popup is not null) _popup.Closed += OnPopupClosed;
        RebuildChips();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty)
        {
            StopObserving();
            if (_attached) ObserveItems();
            RebuildChips();
            RebuildOptions();
        }
        else if (change.Property == MultipleProperty) { RebuildChips(); RebuildOptions(); }
    }
    private void ObserveItems()
    {
        StopObserving();
        _collection = Items as INotifyCollectionChanged;
        if (_collection is not null) _collection.CollectionChanged += OnCollectionChanged;
        foreach (var item in Items ?? []) { _observedItems.Add(item); item.PropertyChanged += OnItemChanged; }
    }
    private void StopObserving()
    {
        if (_collection is not null) _collection.CollectionChanged -= OnCollectionChanged;
        _collection = null;
        foreach (var item in _observedItems) item.PropertyChanged -= OnItemChanged;
        _observedItems.Clear();
    }
    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) { ObserveItems(); RebuildChips(); RebuildOptions(); }
    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LabelChipView.IsSelected) or null) { RebuildChips(); RebuildOptions(); }
    }
    private void RebuildChips()
    {
        if (_flow is null) return;
        // 只替换展示芯片；输入框和建议 Popup 保持原实例，键入时不会丢焦点/光标。
        foreach (var chip in _chips) _flow.Children.Remove(chip);
        _chips.Clear();
        var index = 0;
        foreach (var item in Items?.Where(item => item.IsSelected) ?? [])
        {
            var remove = new Button { Content = "×", Tag = item.Name, Classes = { "detail-chip-remove" } };
            AutomationProperties.SetName(remove, "移除" + item.Name);
            ToolTip.SetTip(remove, "移除" + item.Name);
            remove.Click += OnRemove;
            var chip = new Border
            {
                Classes = { "detail-chip" },
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { new TextBlock { Text = item.Name, Classes = { "detail-chip-text" } }, remove }
                }
            };
            _chips.Add(chip);
            _flow.Children.Insert(index++, chip);
        }
    }
    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string name }) Toggled?.Invoke(name);
    }
    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        _editing = true;
        PseudoClasses.Set(":editing", true);
        _highlight = -1;
        RebuildOptions();
        Dispatcher.UIThread.Post(() =>
        {
            if (!_editing) return;
            _input?.Focus();
            if (_popup is not null) _popup.IsOpen = true;
        }, DispatcherPriority.Input);
    }
    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_editing) return;
        _highlight = -1;
        RebuildOptions();
        if (_popup is not null) _popup.IsOpen = true;
    }
    private void OnInputLostFocus(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!IsKeyboardFocusWithin) ClosePopup();
    }, DispatcherPriority.Input);
    private void OnPopupClosed(object? sender, EventArgs e) => ClosePopup();
    public void ClosePopup()
    {
        _editing = false;
        PseudoClasses.Set(":editing", false);
        if (_popup is not null) _popup.IsOpen = false;
        if (_input is not null) _input.Text = "";
        _highlight = -1;
    }
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { ClosePopup(); _add?.Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
        else if (e.Key is Key.Down or Key.Up)
        {
            if (_filtered.Count > 0)
            {
                var delta = e.Key == Key.Down ? 1 : -1;
                _highlight = _highlight < 0 && delta < 0 ? _filtered.Count - 1 : (_highlight + delta + _filtered.Count) % _filtered.Count;
                RebuildOptions();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Tab) ClosePopup();
    }
    private void Commit()
    {
        var text = LabelName.Normalize(_input?.Text);
        if (_highlight >= 0 && _highlight < _filtered.Count) { Choose(_filtered[_highlight].Name); return; }
        if (text.Length == 0) return;
        var exact = Items?.FirstOrDefault(item => LabelName.Comparer.Equals(item.Name, text));
        if (exact is not null)
        {
            if (!exact.IsSelected) Choose(exact.Name);
            else ClosePopup();
        }
        else { Created?.Invoke(text); FinishChoice(); }
    }
    private void Choose(string name) { Toggled?.Invoke(name); FinishChoice(); }
    private void FinishChoice()
    {
        if (!Multiple) { ClosePopup(); _add?.Focus(); }
        else
        {
            if (_input is not null) { _input.Text = ""; _input.Focus(); }
            RebuildOptions();
        }
    }
    private void RebuildOptions()
    {
        if (_options is null) return;
        _options.Children.Clear();
        var filter = LabelName.Normalize(_input?.Text);
        _filtered = (Items ?? []).Where(item => !item.IsSelected && (filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToList();
        if (_highlight >= _filtered.Count) _highlight = -1;
        for (var index = 0; index < _filtered.Count; index++)
        {
            var item = _filtered[index];
            // 面板与输入框同宽，长名称用省略号收尾，悬停提示完整名称。
            var option = new Button { Content = OptionText(item.Name), Tag = item.Name, Focusable = false, Classes = { "detail-chip-option" } };
            ToolTip.SetTip(option, item.Name);
            option.Classes.Set("highlight", index == _highlight);
            option.Click += (_, _) => Choose(item.Name);
            _options.Children.Add(option);
        }
        if (filter.Length > 0 && !(Items ?? []).Any(item => LabelName.Comparer.Equals(item.Name, filter)))
        {
            var create = new Button { Content = OptionText("+ 创建“" + filter + "”"), Focusable = false, Classes = { "detail-chip-option" } };
            ToolTip.SetTip(create, "创建“" + filter + "”");
            create.Click += (_, _) => { Created?.Invoke(filter); FinishChoice(); };
            _options.Children.Add(create);
        }
        if (_options.Children.Count == 0)
            _options.Children.Add(new TextBlock { Text = "输入名称即可创建", Classes = { "detail-chip-empty" } });
    }

    private static TextBlock OptionText(string text) => new() { Text = text, TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis };
}
