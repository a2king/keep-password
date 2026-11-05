using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using KeepPassword.App.Controls;
using KeepPassword.App.Controls.Details;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;
using KeepPassword.Core.Security;
using KeepPassword.Core.Totp;
using KeepPassword.Core.Vault;
using ShapePath = Avalonia.Controls.Shapes.Path;

namespace KeepPassword.App.Views.Details;

// 轻量展示对象只参与 DataTemplate 选择；草稿与持久化始终使用原 MainViewModel。
// 必须是普通 class（按引用比较），不能用 record：切换同类型条目时 Owner 和 Source 都没变，
// record 按值判等会认为新旧内容"相等"，ContentControl 不刷新 DataContext，连接信息就停留在上一个条目。
public abstract class DetailPageContent(DetailPanelView owner, MainViewModel source)
{
    public DetailPanelView Owner { get; } = owner;
    public MainViewModel Source { get; } = source;
}
public sealed class LoginDetailContent(DetailPanelView owner, MainViewModel source) : DetailPageContent(owner, source);
public sealed class ServerDetailContent(DetailPanelView owner, MainViewModel source) : DetailPageContent(owner, source);
public sealed class DatabaseDetailContent(DetailPanelView owner, MainViewModel source) : DetailPageContent(owner, source);

public partial class DetailPanelView : UserControl
{
    private MainViewModel? _source;
    private DetailPresentationViewModel? _presentation;
    private IReadOnlyList<FieldEditorView>? _presetFields;
    private AccountEditorView? _account;
    private string _accountLayout = "";
    private IReadOnlyList<CustomFieldEditorView> _customFields = [];
    private readonly List<(string Key, FieldRow Row)> _connectionRows = [];
    private readonly List<(string Key, FieldRow Row)> _accountRows = [];
    private readonly List<(string Key, FieldRow Row)> _customRows = [];
    private readonly List<IDisposable> _bindings = [];
    private readonly List<Flyout> _flyouts = [];
    private Window? _window;
    private Guid _detailId;
    private VaultItemKind? _iconKind;
    private bool _refreshQueued;
    private bool _refreshing;

    public ScrollViewer Scroller => DetailScroll;
    public DetailHeader Header => HeaderControl;
    public AccountSection AccountsControl => AccountsSection;
    public event Action? SaveRequested;
    public event Action? CloseRequested;
    public event Action? DeleteRequested;
    public event Action? FavoriteRequested;
    public event Action<FieldEditorView>? ScanRequested;
    public event Action<AccountEditorView>? RemoveAccountRequested;
    public event Action<CustomFieldEditorView>? RemoveCustomFieldRequested;

    public DetailPanelView()
    {
        InitializeComponent();
        HeaderControl.CloseRequested += () => CloseRequested?.Invoke();
        HeaderControl.FavoriteRequested += () => FavoriteRequested?.Invoke();
        HeaderControl.DeleteRequested += () => DeleteRequested?.Invoke();
        Footer.SaveRequested += RequestSave;
        Footer.CancelRequested += CancelChanges;
        AccountsSection.AccountSelected += account => _presentation?.SelectAccount(account);
        AccountsSection.AddRequested += () =>
        {
            if (_source?.AddAccount() is { } account)
            {
                _presentation?.SelectAccount(account);
                Refresh();
                Dispatcher.UIThread.Post(() => _accountRows.FirstOrDefault(item => item.Key == FieldKeys.Username).Row?.BeginEdit());
            }
        };
        AccountsSection.RemoveRequested += account => RemoveAccountRequested?.Invoke(account);
        SpaceEditor.Toggled += name => _source?.ChooseSpace(name);
        TagsEditor.Toggled += name => _source?.ToggleTag(name);
        SpaceEditor.Created += text => ShowError(_source?.AddSpaceChoice(text));
        TagsEditor.Created += text => ShowError(_source?.AddTagChoice(text));
        AddHandler(CopyButton.CopiedEvent, (_, args) => Toast.Show(args.Message));
        AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Bubble);
        AttachedToVisualTree += (_, _) =>
        {
            _window = TopLevel.GetTopLevel(this) as Window;
            if (_window is not null) _window.Deactivated += OnWindowDeactivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            if (_window is not null) _window.Deactivated -= OnWindowDeactivated;
            _window = null;
            ResetTransientState();
        };
    }

    public void SetEditor(MainViewModel? editor)
    {
        if (ReferenceEquals(_source, editor)) return;
        ResetTransientState();
        if (_source is not null) _source.PropertyChanged -= OnSourceChanged;
        if (_account is not null) _account.PropertyChanged -= OnAccountChanged;
        if (_presentation is not null)
        {
            _presentation.DisplayChanged -= OnDisplayChanged;
            _presentation.PropertyChanged -= OnPresentationChanged;
            _presentation.Dispose();
        }
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        _source = editor;
        _presentation = null;
        _presetFields = null;
        _account = null;
        _accountLayout = "";
        _customFields = [];
        _connectionRows.Clear();
        _accountRows.Clear();
        _customRows.Clear();
        SceneHost.Content = null;
        AccountsSection.Content = null;
        AccountsSection.SetAccounts([], null);
        CustomHost.Children.Clear();
        CustomSection.IsVisible = false;
        _flyouts.Clear();
        if (editor is null)
        {
            // 关闭/锁定后断开所有草稿引用，视图属性也不保留旧敏感内容。
            HeaderControl.Title = "";
            HeaderControl.Subtitle = "";
            NoteEditor.Value = "";
            SpaceEditor.Items = [];
            TagsEditor.Items = [];
            QuickActions.SetActions([]);
            Footer.IsShown = false;
            _detailId = Guid.Empty;
            return;
        }
        _presentation = new DetailPresentationViewModel(editor);
        _presentation.DisplayChanged += OnDisplayChanged;
        _presentation.PropertyChanged += OnPresentationChanged;
        editor.PropertyChanged += OnSourceChanged;
        _bindings.Add(HeaderControl.Bind(DetailHeader.TitleProperty, new Binding(nameof(MainViewModel.Name)) { Source = editor, Mode = BindingMode.TwoWay }));
        _bindings.Add(NoteEditor.Bind(FieldRow.ValueProperty, new Binding(nameof(MainViewModel.Note)) { Source = editor, Mode = BindingMode.TwoWay }));
        Refresh(true);
    }

    public void Refresh(bool resetState = false)
    {
        if (_source is not { } source || _presentation is not { } presentation || source.IsApplyingDetail || _refreshing) return;
        _refreshing = true;
        try
        {
            resetState |= _detailId != source.DetailId;
            if (resetState)
            {
                ResetTransientState();
                _detailId = source.DetailId;
                presentation.Refresh(true);
                DetailScroll.Offset = default;
                NoteEditor.Multiline = !string.IsNullOrWhiteSpace(source.Note);
                NoteEditor.IsEditing = source.IsNewItem;
                HeaderControl.ValidationMessage = "";
            }
            HeaderControl.KindLabel = source.KindName;
            HeaderControl.IsFavorite = source.Favorite;
            if (_iconKind != source.Kind) HeaderControl.KindIcon = Geometry.Parse(source.Kind switch
            {
                VaultItemKind.Server => "M3,4 L21,4 L21,20 L3,20 Z M7,9 L10,12 L7,15 M13,15 L17,15",
                VaultItemKind.Database => "M3,6 C3,2 21,2 21,6 C21,10 3,10 3,6 M3,6 L3,18 C3,22 21,22 21,18 L21,6 M3,12 C3,16 21,16 21,12",
                _ => "M10,14 C4,18 0,11 4,7 C8,3 15,7 11,13 L20,22 M15,17 L18,14 M18,20 L21,17"
            });
            _iconKind = source.Kind;
            var subtitle = new[] { source.SpaceChoices.FirstOrDefault(choice => choice.IsSelected)?.Name,
                source.SelectedProtocol?.Name ?? source.SelectedDriver?.Name }.Where(text => !string.IsNullOrWhiteSpace(text));
            HeaderControl.Subtitle = string.Join(" · ", subtitle);
            SpaceEditor.Items = source.SpaceChoices;
            TagsEditor.Items = source.TagChoices;
            AccountsSection.CanAdd = source.CanAddAccount;
            AccountsSection.IsVisible = source.ShowAccounts;
            AccountsSection.SetAccounts(presentation.AccountTabs, presentation.CurrentAccount);

            // 普通 Value/Name 输入仅更新绑定，只有对象集合或结构变化才拼装子树。
            if (!ReferenceEquals(_presetFields, source.PresetFields))
            {
                _presetFields = source.PresetFields;
                _connectionRows.Clear();
                SceneHost.Content = source.Kind switch
                {
                    VaultItemKind.Server => new ServerDetailContent(this, source),
                    VaultItemKind.Database => new DatabaseDetailContent(this, source),
                    _ => new LoginDetailContent(this, source)
                };
            }
            var current = presentation.CurrentAccount;
            var layout = current is null ? "" : $"{current.ShowAuth}/{current.ShowPassword}/{current.ShowTotp}/{current.UseKeyAuth}/{current.SshKeyOptions.Count}";
            if (!ReferenceEquals(_account, current) || _accountLayout != layout)
            {
                if (!ReferenceEquals(_account, current))
                {
                    foreach (var flyout in _flyouts.ToArray()) flyout.Hide();
                    Toast.Clear();
                }
                foreach (var row in _accountRows.Select(item => item.Row)) ResetRow(row);
                if (_account is not null) _account.PropertyChanged -= OnAccountChanged;
                _account = current;
                _accountLayout = layout;
                if (_account is not null) _account.PropertyChanged += OnAccountChanged;
                PopulateAccount(current);
            }
            if (!_customFields.SequenceEqual(source.CustomFields)) PopulateCustomFields();
            UpdateFooter();
            UpdateQuickActions();
            if (resetState && source.IsNewItem)
            {
                foreach (var row in AllRows().Where(row => row.CanEdit)) row.IsEditing = true;
                Dispatcher.UIThread.Post(() => { if (ReferenceEquals(_source, source) && source.IsNewItem) HeaderControl.BeginEdit(); }, DispatcherPriority.Input);
            }
        }
        finally { _refreshing = false; }
    }

    public void ResetTransientState()
    {
        HeaderControl.EndEdit();
        HeaderControl.CloseMenu();
        AccountsSection.CloseMenu();
        HeaderControl.ValidationMessage = "";
        foreach (var row in AllRows()) ResetRow(row);
        foreach (var flyout in _flyouts.ToArray()) flyout.Hide();
        SpaceEditor.ClosePopup();
        TagsEditor.ClosePopup();
        Toast.Clear();
        foreach (var copy in this.GetVisualDescendants().OfType<CopyButton>()) copy.Reset();
    }

    private static void ResetRow(FieldRow row)
    {
        row.EndEdit();
        row.IsEditing = false;
        if (row is SecretField secret) secret.Conceal();
        row.ValidationMessage = "";
    }

    private IEnumerable<FieldRow> AllRows() => _connectionRows.Concat(_accountRows).Concat(_customRows).Select(item => item.Row).Append(NoteEditor);
    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        foreach (var secret in AllRows().OfType<SecretField>()) secret.Conceal();
        foreach (var flyout in _flyouts.ToArray()) flyout.Hide();
    }
    private void OnDisplayChanged(object? sender, EventArgs e) => QueueRefresh();
    private void OnPresentationChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e) => QueueRefresh();
    private void OnAccountChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AccountEditorView.TotpPreview) or nameof(AccountEditorView.HasTotpPreview))) QueueRefresh();
    }
    private void QueueRefresh()
    {
        if (_refreshQueued || _refreshing || _source?.IsApplyingDetail == true) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() => { _refreshQueued = false; Refresh(); }, DispatcherPriority.Background);
    }

    public void PopulateConnectionFields(StackPanel host, MainViewModel source)
    {
        host.Children.Clear();
        if (!ReferenceEquals(_source, source)) return;
        _connectionRows.Clear();
        if (source.ShowProtocol)
        {
            var selector = CompactCombo("协议");
            selector.ItemsSource = source.Protocols;
            selector.ItemTemplate = new FuncDataTemplate<ServerProtocol>((item, _) => new TextBlock { Text = item?.Name });
            selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedProtocol)) { Source = source, Mode = BindingMode.TwoWay });
            host.Children.Add(new FieldRow { Label = "协议", CanCopy = false, CanEdit = false, ValueContent = selector });
        }
        if (source.ShowDriver)
        {
            var selector = CompactCombo("数据库类型");
            selector.ItemsSource = source.Drivers;
            selector.ItemTemplate = new FuncDataTemplate<DatabaseDriver>((item, _) => new TextBlock { Text = item?.Name });
            selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(MainViewModel.SelectedDriver)) { Source = source, Mode = BindingMode.TwoWay });
            host.Children.Add(new FieldRow { Label = "类型", CanCopy = false, CanEdit = false, ValueContent = selector });
        }
        var portField = source.PresetFields.FirstOrDefault(field => field.Key == FieldKeys.Port);
        var hasHost = source.PresetFields.Any(field => field.Key == FieldKeys.Host);
        foreach (var field in source.PresetFields)
        {
            if (field.Key == FieldKeys.Port && hasHost) continue;
            var row = CreateField(field);
            _connectionRows.Add((field.Key, row));
            if (field.Key == FieldKeys.Host && portField is not null)
            {
                // 主机弹性伸展，端口文本固定 72px；两者各保留独立复制与编辑绑定。
                var endpoint = new Grid { ColumnDefinitions = new ColumnDefinitions("*,1,128") };
                row.Classes.Add("detail-endpoint-host");
                var separator = new Border { Width = 1, Margin = new Thickness(0, 10, 0, 10) };
                separator.Bind(Border.BackgroundProperty, this.GetResourceObservable("Detail.Divider"));
                Grid.SetColumn(separator, 1);
                var port = CreateField(portField);
                port.Placeholder = "端口";
                port.Classes.Add("detail-endpoint-port");
                port.TemplateApplied += (_, args) =>
                {
                    // ColumnDefinitions 是 CLR 属性，不能通过 Avalonia Style Setter 设置。
                    if (args.NameScope.Find<Grid>("PART_Layout") is { } layout)
                    {
                        layout.ColumnDefinitions = new ColumnDefinitions("0,0,72,32");
                        layout.Margin = new Thickness(8, 0, 16, 0);
                    }
                };
                _connectionRows.Add((FieldKeys.Port, port));
                Grid.SetColumn(port, 2);
                endpoint.Children.Add(row);
                endpoint.Children.Add(separator);
                endpoint.Children.Add(port);
                host.Children.Add(endpoint);
            }
            else host.Children.Add(row);
        }
        if (host.Children.LastOrDefault() is FieldRow last) last.IsDividerVisible = false;
    }

    private FieldRow CreateField(SecretFieldView field)
    {
        FieldRow row = field is FieldEditorView { Key: FieldKeys.Totp }
            ? new TotpField() : field.Sensitive ? new SecretField() : new FieldRow();
        row.Label = field is FieldEditorView { Key: FieldKeys.Totp } ? "验证码" : field.Label;
        row.Bind(FieldRow.ValueProperty, new Binding(nameof(SecretFieldView.Value)) { Source = field, Mode = BindingMode.TwoWay });
        row.PropertyChanged += (_, change) => { if (change.Property == FieldRow.ValueProperty) row.ValidationMessage = ""; };
        if (row is not TotpField)
            row.Bind(FieldRow.LabelProperty, new Binding(nameof(SecretFieldView.Label)) { Source = field });
        row.Multiline = field.Multiline;
        row.IsMono = field.Sensitive || field is FieldEditorView { Key: FieldKeys.Host or FieldKeys.Port or FieldKeys.Url or FieldKeys.Username or FieldKeys.Database };
        row.IsEditing = _source?.IsNewItem == true;
        row.Placeholder = field is FieldEditorView spec && spec.Placeholder.Length > 0 ? spec.Placeholder : $"添加{field.Label}";
        if (field is FieldEditorView item)
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
            if (item.Key == FieldKeys.Password)
            {
                var button = IconButton("Detail.Icon.Generate", "生成密码");
                button.Click += (_, _) => ShowPasswordGenerator(button, item);
                actions.Children.Add(button);
            }
            if (item.CanScan)
            {
                var button = IconButton("Detail.Icon.Scan", "扫描屏幕二维码");
                button.Click += (_, _) => ScanRequested?.Invoke(item);
                actions.Children.Add(button);
            }
            if (actions.Children.Count > 0) row.Actions = actions;
        }
        return row;
    }

    private void PopulateAccount(AccountEditorView? account)
    {
        _accountRows.Clear();
        if (account is null) { AccountsSection.Content = null; return; }
        var rows = new StackPanel();
        if (account.ShowAuth)
        {
            var selector = new SegmentedControl { ItemsSource = new[] { "密码", "SSH 密钥" }, SelectedIndex = account.UseKeyAuth ? 1 : 0 };
            selector.Classes.Add("compact");
            AutomationProperties.SetName(selector, "认证方式");
            selector.SelectionChanged += (_, _) => { if (selector.SelectedIndex >= 0) account.SetKeyAuth(selector.SelectedIndex == 1); };
            rows.Children.Add(new FieldRow { Label = "认证", CanCopy = false, CanEdit = false, ValueContent = selector });
        }
        void Add(FieldEditorView field)
        {
            var row = CreateField(field);
            _accountRows.Add((field.Key, row));
            rows.Children.Add(field.Key == FieldKeys.Password ? WithPasswordStrength(row) : row);
        }
        Add(account.UsernameField);
        if (account.ShowPassword) Add(account.PasswordField);
        else
        {
            Control content;
            if (account.SshKeyOptions.Count > 0)
            {
                var selector = CompactCombo("SSH 密钥");
                selector.PlaceholderText = "选择 SSH 密钥";
                selector.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(AccountEditorView.SshKeyOptions)) { Source = account });
                selector.ItemTemplate = new FuncDataTemplate<KeyOptionView>((item, _) => new TextBlock { Text = item?.Name, TextTrimming = TextTrimming.CharacterEllipsis });
                selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(AccountEditorView.SelectedSshKey)) { Source = account, Mode = BindingMode.TwoWay });
                content = selector;
            }
            else
            {
                var hint = new TextBlock { Text = "先创建独立的 SSH 密钥条目", FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                hint.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Detail.TextMuted"));
                content = hint;
            }
            var keyRow = new FieldRow { Label = "SSH 密钥", CanCopy = false, CanEdit = false, ValueContent = content };
            _accountRows.Add((FieldKeys.Auth, keyRow));
            rows.Children.Add(keyRow);
        }
        if (account.ShowTotp) Add(account.TotpField);
        var note = new FieldRow { Label = "说明", Placeholder = "添加账号说明", CanCopy = false, IsDividerVisible = false, IsEditing = _source?.IsNewItem == true };
        note.Bind(FieldRow.ValueProperty, new Binding(nameof(AccountEditorView.Note)) { Source = account, Mode = BindingMode.TwoWay });
        _accountRows.Add((FieldKeys.AccountNote, note));
        rows.Children.Add(note);
        AccountsSection.Content = new DetailGroup { Content = rows, Margin = new Thickness(0) };
    }

    private void PopulateCustomFields()
    {
        _customFields = _source?.CustomFields.ToArray() ?? [];
        _customRows.Clear();
        CustomHost.Children.Clear();
        foreach (var field in _customFields)
        {
            var row = CreateField(field);
            var remove = IconButton("Detail.Icon.Trash", "删除自定义字段");
            remove.Click += (_, _) => RemoveCustomFieldRequested?.Invoke(field);
            row.Actions = remove;
            _customRows.Add((field.Name, row));
            CustomHost.Children.Add(row);
        }
        if (CustomHost.Children.LastOrDefault() is FieldRow last) last.IsDividerVisible = false;
        CustomSection.IsVisible = _customFields.Count > 0;
    }

    private Control WithPasswordStrength(FieldRow row)
    {
        var grid = new Grid();
        var track = new Border { Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(92, 0, 16, 3), IsHitTestVisible = false };
        track.Bind(Border.BackgroundProperty, this.GetResourceObservable("Detail.Divider"));
        var fill = new Border { Height = 2, CornerRadius = new CornerRadius(1), HorizontalAlignment = HorizontalAlignment.Left };
        track.Child = fill;
        IDisposable? color = null;
        void Update()
        {
            var strength = PasswordStrengthEvaluator.Evaluate(row.Value);
            track.IsVisible = strength != PasswordStrength.Empty;
            var fraction = strength == PasswordStrength.Strong ? 1d : strength == PasswordStrength.Fair ? 0.65 : 0.3;
            fill.Width = Math.Max(0, track.Bounds.Width * fraction);
            color?.Dispose();
            color = fill.Bind(Border.BackgroundProperty, this.GetResourceObservable(strength == PasswordStrength.Strong ? "Detail.Success"
                : strength == PasswordStrength.Fair ? "Detail.Warning" : "Detail.Danger"));
        }
        row.PropertyChanged += (_, args) => { if (args.Property == FieldRow.ValueProperty) Update(); };
        track.PropertyChanged += (_, args) => { if (args.Property == BoundsProperty) Update(); };
        grid.Children.Add(row);
        grid.Children.Add(track);
        Update();
        return grid;
    }

    private void UpdateFooter()
    {
        if (_source is not { } source || _presentation is not { } presentation) return;
        Footer.IsNew = source.IsNewItem;
        Footer.IsShown = source.IsNewItem || presentation.IsDirty;
        Footer.CanSave = presentation.CanSave;
        Footer.Message = presentation.ValidationMessage.Length > 0 ? presentation.ValidationMessage
            : source.IsNewItem ? "填写完成后创建条目" : "有未保存的修改";
        if (HeaderControl.ValidationMessage.Length > 0 && !string.IsNullOrWhiteSpace(source.Name)) HeaderControl.ValidationMessage = "";
    }

    private void RequestSave()
    {
        if (_source is not { } source) return;
        HeaderControl.EndEdit();
        foreach (var row in AllRows()) row.EndEdit();
        var rowError = AllRows().FirstOrDefault(row => row.HasValidation);
        if (rowError is not null)
        {
            rowError.BeginEdit();
            rowError.BringIntoView();
            Toast.Show(rowError.ValidationMessage);
            return;
        }
        if (source.GetDetailValidationMessage() is { } error)
        {
            ShowValidation(error);
            return;
        }
        SaveRequested?.Invoke();
    }

    private void ShowValidation(string error)
    {
        Toast.Show(error);
        Footer.Message = error;
        if (_source is null) return;
        if (string.IsNullOrWhiteSpace(_source.Name))
        {
            HeaderControl.ValidationMessage = error;
            HeaderControl.BeginEdit();
            return;
        }
        var accountNumber = Regex.Match(error, @"第 (\d+) 个账号");
        if (accountNumber.Success && int.TryParse(accountNumber.Groups[1].Value, out var number)
            && number > 0 && number <= _source.Accounts.Count)
        {
            _presentation?.SelectAccount(_source.Accounts[number - 1]);
            Refresh();
        }
        var key = error.Contains("端口") ? FieldKeys.Port
            : error.Contains("主机") || error.Contains("Endpoint") ? FieldKeys.Host
            : error.Contains("文件路径") ? FieldKeys.Database
            : error.Contains("SSH 密钥") ? FieldKeys.Auth
            : error.Contains("密码") || error.Contains("Secret") ? FieldKeys.Password
            : error.Contains("用户名") || error.Contains("AccessKey ID") ? FieldKeys.Username : "";
        var row = _connectionRows.Concat(_accountRows).FirstOrDefault(item => item.Key == key).Row;
        if (row is null) { AccountsSection.BringIntoView(); return; }
        row.ValidationMessage = error;
        row.BringIntoView();
        if (row.ValueContent is Control value) value.Focus(); else row.BeginEdit();
    }

    private void CancelChanges()
    {
        if (_source is not { } source) return;
        if (source.IsNewItem) { CloseRequested?.Invoke(); return; }
        ResetTransientState();
        source.Load(source.DetailId);
        Refresh(true);
        Toast.Show("已撤销修改");
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { RequestSave(); e.Handled = true; }
        else if (e.Key == Key.Escape)
        {
            var row = AllRows().FirstOrDefault(item => item.IsEditing && item.IsKeyboardFocusWithin);
            if (HeaderControl.IsEditing) HeaderControl.EndEdit(true);
            else if (row is not null) row.EndEdit(true);
            else CloseRequested?.Invoke();
            e.Handled = true;
        }
    }

    private string FieldValue(string key) => _source?.PresetFields.FirstOrDefault(field => field.Key == key)?.Value ?? "";
    private string? ServerCommand() => DetailQuickActionFormatter.ServerCommand(_source?.SelectedProtocol?.Code,
        FieldValue(FieldKeys.Host), FieldValue(FieldKeys.Port), _presentation?.CurrentAccount?.UsernameField.Value);
    private string? DatabaseConnection(bool includePassword) => DetailQuickActionFormatter.DatabaseConnection(_source?.SelectedDriver?.Code,
        FieldValue(FieldKeys.Host), FieldValue(FieldKeys.Port), FieldValue(FieldKeys.Database),
        _presentation?.CurrentAccount?.UsernameField.Value, _presentation?.CurrentAccount?.PasswordField.Value, includePassword);

    private void UpdateQuickActions()
    {
        if (_source is not { } source) return;
        var actions = new List<DetailQuickAction>();
        var account = _presentation?.CurrentAccount;
        if (source.Kind == VaultItemKind.Login && TryWebsite(out _))
            actions.Add(new("打开网站", "Detail.Icon.External", _ => OpenWebsiteAsync()));
        if (account?.UsernameField.Value.Length > 0)
            actions.Add(new("复制用户名", "Detail.Icon.Copy", _ => CopyAsync(_presentation?.CurrentAccount?.UsernameField.Value, false, "用户名已复制")));
        if (account is { ShowPassword: true } && account.PasswordField.Value.Length > 0)
            actions.Add(new("复制密码", "Detail.Icon.Copy", _ => CopyAsync(_presentation?.CurrentAccount?.PasswordField.Value, true, "密码已复制，稍后自动清除")));
        if (source.Kind == VaultItemKind.Server && ServerCommand() is not null)
            actions.Add(new("复制命令", "Detail.Icon.Terminal", _ => CopyAsync(ServerCommand(), false, "连接命令已复制")));
        if (source.Kind == VaultItemKind.Database && DatabaseConnection(false) is not null)
            actions.Add(new("复制连接串", "Detail.Icon.Database", modifiers => CopyAsync(DatabaseConnection(modifiers.HasFlag(KeyModifiers.Shift)),
                modifiers.HasFlag(KeyModifiers.Shift), modifiers.HasFlag(KeyModifiers.Shift) ? "已复制（含密码），30 秒后清除" : "连接串已复制"), "复制连接串；按住 Shift 包含密码"));
        if (account is { ShowTotp: true } && TotpGenerator.TryParseSecret(account.TotpField.Value, out _))
            actions.Add(new("复制验证码", "Detail.Icon.Copy", _ => CopyTotpAsync()));
        QuickActions.SetActions(actions);
    }

    private bool TryWebsite(out Uri? website)
    {
        var valid = Uri.TryCreate(FieldValue(FieldKeys.Url).Trim(), UriKind.Absolute, out website)
            && website.Scheme is "http" or "https";
        if (!valid) website = null;
        return valid;
    }
    private Task OpenWebsiteAsync()
    {
        if (TryWebsite(out var website))
        {
            try { Process.Start(new ProcessStartInfo(website!.AbsoluteUri) { UseShellExecute = true }); }
            catch { Toast.Show("无法打开网站，请复制网址后重试"); }
        }
        return Task.CompletedTask;
    }
    private Task CopyTotpAsync() => TotpGenerator.TryGenerate(_presentation?.CurrentAccount?.TotpField.Value ?? "",
        DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var code, out _)
        ? CopyAsync(code, true, "验证码已复制") : Task.CompletedTask;
    private async Task CopyAsync(string? value, bool sensitive, string message)
    {
        if (string.IsNullOrEmpty(value)) return;
        var copied = await ClipboardService.CopyAsync(TopLevel.GetTopLevel(this), value, sensitive);
        if (_source is not null) Toast.Show(copied ? message : "复制失败，请重试");
    }
    private void ShowError(string? error) { if (!string.IsNullOrEmpty(error)) Toast.Show(error); }

    private Button IconButton(string resource, string hint)
    {
        // 字段行内的附加图标（生成 / 扫码 / 删除）与复制、显示同尺寸：按钮 24px、图标 14px。
        var icon = new ShapePath { Width = 14, Height = 14, Stretch = Stretch.Uniform, StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Fill = Brushes.Transparent };
        icon.Bind(ShapePath.DataProperty, this.GetResourceObservable(resource));
        icon.Bind(ShapePath.StrokeProperty, this.GetResourceObservable("Detail.TextMuted"));
        var button = new Button { Content = icon, Width = 24, Height = 24, CornerRadius = new CornerRadius(6) };
        button.Classes.Add("detail-icon");
        ToolTip.SetTip(button, hint);
        return button;
    }

    // 详情行值区只有 32px 高，下拉使用 compact 尺寸；其余外观由全局玻璃主题提供。
    private static ComboBox CompactCombo(string name)
    {
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Classes.Add("compact");
        AutomationProperties.SetName(combo, name);
        return combo;
    }

    // 生成器弹层里的字符类型开关：开/关文字相同，点文字也能切换，切换时宽度不变。
    private static ToggleSwitch OptionSwitch(string label, bool isChecked)
    {
        var toggle = new ToggleSwitch { OnContent = label, OffContent = label, IsChecked = isChecked, Margin = new Thickness(0, 4) };
        toggle.Classes.Add("compact");
        AutomationProperties.SetName(toggle, label);
        return toggle;
    }

    private void ShowPasswordGenerator(Button target, FieldEditorView field)
    {
        var sourceAtOpen = _source;
        var accountAtOpen = _presentation?.CurrentAccount;
        var result = new TextBox { IsReadOnly = true, MinWidth = 240, FontSize = 14 };
        result.Bind(TextBox.FontFamilyProperty, this.GetResourceObservable("Detail.Font.Mono"));
        var lengthLabel = new TextBlock();
        var length = new Slider { Minimum = 8, Maximum = 64, Value = 16, TickFrequency = 1, IsSnapToTickEnabled = true };
        var lowercase = OptionSwitch("小写字母", true);
        var uppercase = OptionSwitch("大写字母", true);
        var digits = OptionSwitch("数字", true);
        var symbols = OptionSwitch("包含符号", false);
        var strength = new TextBlock { FontSize = 12 };
        strength.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("Detail.TextMuted"));
        var body = new StackPanel { Spacing = 10, Width = 268 };
        body.Children.Add(new TextBlock { Text = "生成密码", FontSize = 15, FontWeight = FontWeight.SemiBold });
        body.Children.Add(result);
        body.Children.Add(strength);
        body.Children.Add(lengthLabel);
        body.Children.Add(length);
        var options = new UniformGrid { Columns = 2, Rows = 2, Margin = new Thickness(0, 2, 0, 4) };
        options.Children.Add(lowercase);
        options.Children.Add(uppercase);
        options.Children.Add(digits);
        options.Children.Add(symbols);
        body.Children.Add(options);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var regenerate = new Button { Content = "重新生成" };
        regenerate.Classes.Add("soft");
        var apply = new Button { Content = "使用密码" };
        apply.Classes.Add("accent");
        buttons.Children.Add(regenerate);
        buttons.Children.Add(apply);
        body.Children.Add(buttons);
        var flyout = new Flyout { Content = body, Placement = PlacementMode.BottomEdgeAlignedRight };
        _flyouts.Add(flyout);
        void Generate()
        {
            lengthLabel.Text = $"长度  {(int)length.Value}";
            var valid = lowercase.IsChecked == true || uppercase.IsChecked == true || digits.IsChecked == true || symbols.IsChecked == true;
            apply.IsEnabled = valid;
            regenerate.IsEnabled = valid;
            if (!valid) { result.Text = ""; strength.Text = "至少选择一类字符"; return; }
            result.Text = PasswordGenerator.Generate(new PasswordGeneratorOptions { Length = (int)length.Value,
                Lowercase = lowercase.IsChecked == true, Uppercase = uppercase.IsChecked == true,
                Digits = digits.IsChecked == true, Symbols = symbols.IsChecked == true });
            strength.Text = "强度：" + PasswordStrengthEvaluator.Describe(PasswordStrengthEvaluator.Evaluate(result.Text));
        }
        length.PropertyChanged += (_, args) => { if (args.Property == RangeBase.ValueProperty) Generate(); };
        symbols.IsCheckedChanged += (_, _) => Generate();
        lowercase.IsCheckedChanged += (_, _) => Generate();
        uppercase.IsCheckedChanged += (_, _) => Generate();
        digits.IsCheckedChanged += (_, _) => Generate();
        regenerate.Click += (_, _) => Generate();
        apply.Click += (_, _) =>
        {
            if (ReferenceEquals(_source, sourceAtOpen) && ReferenceEquals(_presentation?.CurrentAccount, accountAtOpen))
                field.Value = result.Text ?? "";
            flyout.Hide();
        };
        flyout.Closed += (_, _) => { result.Text = ""; _flyouts.Remove(flyout); };
        Generate();
        flyout.ShowAt(target);
    }
}
