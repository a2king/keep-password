using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace KeepPassword.App.Controls.Details;

/// <summary>尺寸稳定的查看/编辑字段；只写入草稿，持久化由详情宿主负责。</summary>
public class FieldRow : TemplatedControl
{
    public static readonly StyledProperty<string> LabelProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(Label), "");
    public static readonly StyledProperty<string> ValueProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(Value), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> PlaceholderProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(Placeholder), "添加内容");
    public static readonly StyledProperty<string> CopyValueProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(CopyValue), "");
    public static readonly StyledProperty<string> ValidationMessageProperty = AvaloniaProperty.Register<FieldRow, string>(nameof(ValidationMessage), "");
    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(IsEditing));
    public static readonly StyledProperty<bool> IsMonoProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(IsMono));
    public static readonly StyledProperty<bool> IsSecretProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(IsSecret));
    public static readonly StyledProperty<bool> MultilineProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(Multiline));
    public static readonly StyledProperty<bool> CanCopyProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(CanCopy), true);
    public static readonly StyledProperty<bool> CanEditProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(CanEdit), true);
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<FieldRow, object?>(nameof(Actions));
    public static readonly StyledProperty<object?> ValueContentProperty = AvaloniaProperty.Register<FieldRow, object?>(nameof(ValueContent));
    public static readonly StyledProperty<bool> IsDividerVisibleProperty = AvaloniaProperty.Register<FieldRow, bool>(nameof(IsDividerVisible), true);
    public static readonly DirectProperty<FieldRow, string> DisplayValueProperty = AvaloniaProperty.RegisterDirect<FieldRow, string>(nameof(DisplayValue), row => row.DisplayValue);
    public static readonly DirectProperty<FieldRow, string> EffectiveCopyValueProperty = AvaloniaProperty.RegisterDirect<FieldRow, string>(nameof(EffectiveCopyValue), row => row.EffectiveCopyValue);
    public static readonly DirectProperty<FieldRow, string?> ValueToolTipProperty = AvaloniaProperty.RegisterDirect<FieldRow, string?>(nameof(ValueToolTip), row => row.ValueToolTip);
    public static readonly DirectProperty<FieldRow, char> EditorPasswordCharProperty = AvaloniaProperty.RegisterDirect<FieldRow, char>(nameof(EditorPasswordChar), row => row.EditorPasswordChar);
    public static readonly DirectProperty<FieldRow, bool> HasValidationProperty = AvaloniaProperty.RegisterDirect<FieldRow, bool>(nameof(HasValidation), row => row.HasValidation);

    private TextBox? _editor;
    private Button? _valueButton;
    private Button? _editButton;
    private Button? _revealButton;
    private CopyButton? _copyButton;
    private string _beforeEdit = "";
    private string _displayValue = "";
    private string _effectiveCopyValue = "";
    private string? _valueToolTip;
    private char _editorPasswordChar;
    private bool _hasValidation;
    private bool _ending;

    protected override Type StyleKeyOverride => typeof(FieldRow);
    public string Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public string Placeholder { get => GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }
    public string CopyValue { get => GetValue(CopyValueProperty); set => SetValue(CopyValueProperty, value); }
    public string ValidationMessage { get => GetValue(ValidationMessageProperty); set => SetValue(ValidationMessageProperty, value); }
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }
    public bool IsMono { get => GetValue(IsMonoProperty); set => SetValue(IsMonoProperty, value); }
    public bool IsSecret { get => GetValue(IsSecretProperty); set => SetValue(IsSecretProperty, value); }
    public bool Multiline { get => GetValue(MultilineProperty); set => SetValue(MultilineProperty, value); }
    public bool CanCopy { get => GetValue(CanCopyProperty); set => SetValue(CanCopyProperty, value); }
    public bool CanEdit { get => GetValue(CanEditProperty); set => SetValue(CanEditProperty, value); }
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public object? ValueContent { get => GetValue(ValueContentProperty); set => SetValue(ValueContentProperty, value); }
    public bool IsDividerVisible { get => GetValue(IsDividerVisibleProperty); set => SetValue(IsDividerVisibleProperty, value); }
    public string DisplayValue => _displayValue;
    public string EffectiveCopyValue => _effectiveCopyValue;
    public string? ValueToolTip => _valueToolTip;
    public char EditorPasswordChar => _editorPasswordChar;
    public bool HasValidation => _hasValidation;
    protected virtual bool IsValueRevealed => false;
    protected virtual bool SupportsInlineEditing => ValueContent is null;
    protected virtual string GetCopyValue() => string.IsNullOrEmpty(CopyValue) ? Value ?? "" : CopyValue;

    public event EventHandler? EditStarted;
    public event EventHandler? EditCommitted;
    public event EventHandler? EditCancelled;

    public FieldRow()
    {
        Focusable = true;
        LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            // 焦点在眼睛/复制/操作插槽之间移动时不结束编辑。
            if (!IsKeyboardFocusWithin && IsEditing) EndEdit();
        }, DispatcherPriority.Input);
        DetachedFromVisualTree += (_, _) => _beforeEdit = "";
        UpdatePresentation();
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_valueButton is not null) _valueButton.Click -= OnValueClick;
        if (_editButton is not null) _editButton.Click -= OnEditClick;
        if (_revealButton is not null) _revealButton.Click -= OnRevealClick;
        if (_editor is not null) _editor.KeyDown -= OnEditorKeyDown;
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_Editor");
        _valueButton = e.NameScope.Find<Button>("PART_ValueButton");
        _editButton = e.NameScope.Find<Button>("PART_EditButton");
        _revealButton = e.NameScope.Find<Button>("PART_RevealButton");
        _copyButton = e.NameScope.Find<CopyButton>("PART_CopyButton");
        if (_valueButton is not null) _valueButton.Click += OnValueClick;
        if (_editButton is not null) _editButton.Click += OnEditClick;
        if (_revealButton is not null) _revealButton.Click += OnRevealClick;
        if (_editor is not null) _editor.KeyDown += OnEditorKeyDown;
        UpdatePresentation();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 清除针对旧值的错误；结束编辑和保存时仍由原校验规则重新检查。
        if (change.Property == ValueProperty && !string.IsNullOrEmpty(ValidationMessage))
            SetCurrentValue(ValidationMessageProperty, "");
        if (change.Property == IsEditingProperty && !_ending)
        {
            if (IsEditing)
            {
                _beforeEdit = Value;
                EditStarted?.Invoke(this, EventArgs.Empty);
            }
        }
        if (change.Property == ValueProperty || change.Property == PlaceholderProperty
            || change.Property == CopyValueProperty || change.Property == IsSecretProperty
            || change.Property == IsEditingProperty || change.Property == IsMonoProperty
            || change.Property == MultilineProperty || change.Property == ValidationMessageProperty
            || change.Property == ValueContentProperty || change.Property == CanEditProperty)
            UpdatePresentation();
    }

    protected void UpdatePresentation()
    {
        var value = Value ?? "";
        var shown = value.Length == 0 ? Placeholder : IsSecret && !IsValueRevealed ? "••••••••••" : value;
        SetAndRaise(DisplayValueProperty, ref _displayValue, shown ?? "");
        SetAndRaise(EffectiveCopyValueProperty, ref _effectiveCopyValue, GetCopyValue());
        SetAndRaise(ValueToolTipProperty, ref _valueToolTip, IsSecret || value.Length == 0 ? null : value);
        SetAndRaise(EditorPasswordCharProperty, ref _editorPasswordChar, IsSecret && !IsValueRevealed ? '•' : '\0');
        SetAndRaise(HasValidationProperty, ref _hasValidation, !string.IsNullOrWhiteSpace(ValidationMessage));
        PseudoClasses.Set(":editing", IsEditing);
        PseudoClasses.Set(":empty", value.Length == 0);
        PseudoClasses.Set(":secret", IsSecret);
        PseudoClasses.Set(":mono", IsMono);
        PseudoClasses.Set(":multiline", Multiline);
        PseudoClasses.Set(":invalid", HasValidation);
        PseudoClasses.Set(":custom-value", ValueContent is not null);
        PseudoClasses.Set(":revealed", IsValueRevealed);
        PseudoClasses.Set(":readonly", !CanEdit || !SupportsInlineEditing);
    }

    public void BeginEdit()
    {
        if (!CanEdit || !SupportsInlineEditing) return;
        if (!IsEditing) SetCurrentValue(IsEditingProperty, true);
        Dispatcher.UIThread.Post(() =>
        {
            if (IsEditing && IsEffectivelyVisible) { _editor?.Focus(); _editor?.SelectAll(); }
        }, DispatcherPriority.Input);
    }

    public virtual void EndEdit(bool cancel = false)
    {
        if (!IsEditing || _ending) return;
        _ending = true;
        try
        {
            if (cancel) SetCurrentValue(ValueProperty, _beforeEdit);
            SetCurrentValue(IsEditingProperty, false);
            if (cancel) EditCancelled?.Invoke(this, EventArgs.Empty);
            else EditCommitted?.Invoke(this, EventArgs.Empty);
        }
        finally { _beforeEdit = ""; _ending = false; }
    }

    protected virtual void OnValueActivated() => BeginEdit();
    protected virtual void OnRevealActivated() { }
    protected Task<bool> CopyCurrentAsync() => _copyButton?.CopyAsync() ?? Task.FromResult(false);
    private void OnValueClick(object? sender, RoutedEventArgs e) => OnValueActivated();
    private void OnEditClick(object? sender, RoutedEventArgs e) => BeginEdit();
    private void OnRevealClick(object? sender, RoutedEventArgs e) => OnRevealActivated();
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { EndEdit(true); Focus(); e.Handled = true; }
        else if (e.Key == Key.Enter && (!Multiline || e.KeyModifiers.HasFlag(KeyModifiers.Control)))
        { EndEdit(); Focus(); e.Handled = true; }
    }
}
