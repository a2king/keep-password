using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using KeepPassword.App.ViewModels;
using SegmentedControl = KeepPassword.App.Controls.SegmentedControl;

namespace KeepPassword.App.Controls.Details;

public sealed class AccountSection : ContentControl
{
    public static readonly StyledProperty<bool> CanAddProperty = AvaloniaProperty.Register<AccountSection, bool>(nameof(CanAdd), true);
    public static readonly StyledProperty<string> HeaderTextProperty = AvaloniaProperty.Register<AccountSection, string>(nameof(HeaderText), "账号");
    /// <summary>放在账号页签下方、账号字段上方的内容（快捷操作），随当前账号一起变化。</summary>
    public static readonly StyledProperty<object?> ActionsProperty = AvaloniaProperty.Register<AccountSection, object?>(nameof(Actions));
    public object? Actions { get => GetValue(ActionsProperty); set => SetValue(ActionsProperty, value); }
    public bool CanAdd { get => GetValue(CanAddProperty); set => SetValue(CanAddProperty, value); }
    public string HeaderText { get => GetValue(HeaderTextProperty); private set => SetValue(HeaderTextProperty, value); }
    protected override Type StyleKeyOverride => typeof(AccountSection);
    private SegmentedControl? _tabs;
    private IReadOnlyList<DetailAccountTab> _items = [];
    private AccountEditorView? _selected;
    private bool _updating;
    private Grid? _tabStrip;
    private MenuFlyout? _menu;
    public event Action? AddRequested;
    public event Action<AccountEditorView>? RemoveRequested;
    public event Action<AccountEditorView>? AccountSelected;

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _tabs = e.NameScope.Find<SegmentedControl>("PART_Accounts");
        _tabStrip = e.NameScope.Find<Grid>("PART_TabStrip");
        if (_tabs is not null)
        {
            _tabs.ItemTemplate = new FuncDataTemplate<DetailAccountTab>((item, _) =>
            {
                var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130 };
                text.Bind(TextBlock.TextProperty, new Binding(nameof(DetailAccountTab.Label)) { Source = item });
                text.Bind(ToolTip.TipProperty, new Binding(nameof(DetailAccountTab.Label)) { Source = item });
                return text;
            });
            _tabs.SelectionChanged += (_, _) =>
            {
                if (!_updating && _tabs.SelectedItem is DetailAccountTab tab) AccountSelected?.Invoke(tab.Account);
            };
        }
        if (e.NameScope.Find<Button>("PART_Add") is { } add) add.Click += (_, _) => AddRequested?.Invoke();
        if (e.NameScope.Find<Button>("PART_AddBelow") is { } below) below.Click += (_, _) => AddRequested?.Invoke();
        if (e.NameScope.Find<Button>("PART_Remove") is { } remove)
        {
            var item = new MenuItem { Header = "删除当前账号…" };
            item.Click += (_, _) => { if (_selected is not null) RemoveRequested?.Invoke(_selected); };
            _menu?.Hide();
            _menu = new MenuFlyout { Items = { item } };
            remove.Flyout = _menu;
        }
        ApplyItems();
    }

    public void SetAccounts(IReadOnlyList<DetailAccountTab> tabs, AccountEditorView? selected)
    {
        _items = tabs;
        HeaderText = tabs.Count > 1 ? $"账号（{tabs.Count}）" : "账号";
        _selected = selected;
        ApplyItems();
    }

    public void CloseMenu() => _menu?.Hide();

    private void ApplyItems()
    {
        if (_tabs is null) return;
        _updating = true;
        try
        {
            // 只有页签文字或对象真的变化才替换，输入密码不会重建账号选择器。
            if (_tabs.ItemsSource is not IReadOnlyList<DetailAccountTab> previous || !previous.SequenceEqual(_items)) _tabs.ItemsSource = _items;
            _tabs.SelectedItem = _items.FirstOrDefault(tab => ReferenceEquals(tab.Account, _selected));
            _tabs.IsVisible = _items.Count > 1;
            if (_tabStrip is not null) _tabStrip.IsVisible = _items.Count > 1;
        }
        finally { _updating = false; }
    }
}
