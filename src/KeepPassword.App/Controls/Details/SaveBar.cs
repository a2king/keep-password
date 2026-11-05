using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace KeepPassword.App.Controls.Details;

public sealed class SaveBar : TemplatedControl
{
    public static readonly StyledProperty<bool> IsNewProperty = AvaloniaProperty.Register<SaveBar, bool>(nameof(IsNew));
    public static readonly StyledProperty<bool> IsShownProperty = AvaloniaProperty.Register<SaveBar, bool>(nameof(IsShown));
    public static readonly StyledProperty<bool> CanSaveProperty = AvaloniaProperty.Register<SaveBar, bool>(nameof(CanSave));
    public static readonly StyledProperty<string> MessageProperty = AvaloniaProperty.Register<SaveBar, string>(nameof(Message), "");
    public static readonly StyledProperty<string> SaveLabelProperty = AvaloniaProperty.Register<SaveBar, string>(nameof(SaveLabel), "保存修改");
    public static readonly StyledProperty<string> CancelLabelProperty = AvaloniaProperty.Register<SaveBar, string>(nameof(CancelLabel), "撤销修改");
    public bool IsNew { get => GetValue(IsNewProperty); set => SetValue(IsNewProperty, value); }
    public bool IsShown { get => GetValue(IsShownProperty); set => SetValue(IsShownProperty, value); }
    public bool CanSave { get => GetValue(CanSaveProperty); set => SetValue(CanSaveProperty, value); }
    public string Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public string SaveLabel { get => GetValue(SaveLabelProperty); private set => SetValue(SaveLabelProperty, value); }
    public string CancelLabel { get => GetValue(CancelLabelProperty); private set => SetValue(CancelLabelProperty, value); }
    public SaveBar() { IsEnabled = false; IsHitTestVisible = false; }
    public event Action? SaveRequested;
    public event Action? CancelRequested;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<Button>("PART_SaveButton") is { } save) save.Click += (_, _) => SaveRequested?.Invoke();
        if (e.NameScope.Find<Button>("PART_Cancel") is { } cancel) cancel.Click += (_, _) => CancelRequested?.Invoke();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsShownProperty)
        {
            PseudoClasses.Set(":shown", IsShown);
            IsEnabled = IsShown;
            IsHitTestVisible = IsShown;
        }
        if (change.Property != IsNewProperty) return;
        SaveLabel = IsNew ? "创建条目" : "保存修改";
        CancelLabel = IsNew ? "取消" : "撤销修改";
    }
}
