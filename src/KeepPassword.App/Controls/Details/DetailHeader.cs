using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace KeepPassword.App.Controls.Details;

public sealed class DetailHeader : TemplatedControl
{
    public static readonly StyledProperty<string> TitleProperty = AvaloniaProperty.Register<DetailHeader, string>(nameof(Title), "");
    public static readonly StyledProperty<string> SubtitleProperty = AvaloniaProperty.Register<DetailHeader, string>(nameof(Subtitle), "");
    public static readonly StyledProperty<string> KindLabelProperty = AvaloniaProperty.Register<DetailHeader, string>(nameof(KindLabel), "登录账号");
    public static readonly StyledProperty<Geometry?> KindIconProperty = AvaloniaProperty.Register<DetailHeader, Geometry?>(nameof(KindIcon));
    public static readonly StyledProperty<bool> IsFavoriteProperty = AvaloniaProperty.Register<DetailHeader, bool>(nameof(IsFavorite));
    public static readonly StyledProperty<bool> IsEditingProperty = AvaloniaProperty.Register<DetailHeader, bool>(nameof(IsEditing));
    public static readonly StyledProperty<string> ValidationMessageProperty = AvaloniaProperty.Register<DetailHeader, string>(nameof(ValidationMessage), "");
    private TextBox? _editor;
    private MenuFlyout? _menu;
    private string _beforeEdit = "";
    public string Title { get => GetValue(TitleProperty); set => SetValue(TitleProperty, value); }
    public string Subtitle { get => GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }
    public string KindLabel { get => GetValue(KindLabelProperty); set => SetValue(KindLabelProperty, value); }
    public Geometry? KindIcon { get => GetValue(KindIconProperty); set => SetValue(KindIconProperty, value); }
    public bool IsFavorite { get => GetValue(IsFavoriteProperty); set => SetValue(IsFavoriteProperty, value); }
    public bool IsEditing { get => GetValue(IsEditingProperty); set => SetValue(IsEditingProperty, value); }
    public string ValidationMessage { get => GetValue(ValidationMessageProperty); set => SetValue(ValidationMessageProperty, value); }
    public event Action? CloseRequested;
    public event Action? FavoriteRequested;
    public event Action? DeleteRequested;
    public TextBox? TitleEditor => _editor;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _editor = e.NameScope.Find<TextBox>("PART_TitleEditor");
        if (e.NameScope.Find<Button>("PART_TitleButton") is { } title) title.Click += (_, _) => BeginEdit();
        if (e.NameScope.Find<Button>("PART_CloseButton") is { } close) close.Click += (_, _) => CloseRequested?.Invoke();
        if (e.NameScope.Find<Button>("PART_Favorite") is { } favorite) favorite.Click += (_, _) => FavoriteRequested?.Invoke();
        if (e.NameScope.Find<Button>("PART_More") is { } more)
        {
            var delete = new MenuItem { Header = "删除条目…" };
            delete.Click += (_, _) => DeleteRequested?.Invoke();
            _menu?.Hide();
            _menu = new MenuFlyout { Items = { delete } };
            more.Flyout = _menu;
        }
        if (_editor is not null)
        {
            _editor.KeyDown += (_, args) =>
            {
                if (args.Key is Key.Enter or Key.Escape)
                {
                    EndEdit(args.Key == Key.Escape);
                    args.Handled = true;
                }
            };
            _editor.LostFocus += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (_editor?.IsFocused != true) EndEdit();
            });
        }
        UpdateStates();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsEditingProperty || change.Property == IsFavoriteProperty || change.Property == ValidationMessageProperty) UpdateStates();
    }

    private void UpdateStates()
    {
        PseudoClasses.Set(":editing", IsEditing);
        PseudoClasses.Set(":favorite", IsFavorite);
        PseudoClasses.Set(":invalid", !string.IsNullOrEmpty(ValidationMessage));
    }

    public void BeginEdit()
    {
        if (!IsEditing) _beforeEdit = Title;
        IsEditing = true;
        Dispatcher.UIThread.Post(() => { _editor?.Focus(); _editor?.SelectAll(); });
    }

    public void EndEdit(bool cancel = false)
    {
        if (IsEditing && cancel) SetCurrentValue(TitleProperty, _beforeEdit);
        IsEditing = false;
        _beforeEdit = "";
    }

    public void CloseMenu() => _menu?.Hide();
}
