using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using KeepPassword.App.Controls;
using KeepPassword.App.Controls.Details;
using KeepPassword.App.ViewModels;
using KeepPassword.App.Views.Details;
using KeepPassword.Core.Crypto;
using KeepPassword.Core.Vault;

namespace KeepPassword.App.Tests;

public class DetailPanelInteractionTests
{
    [AvaloniaTheory]
    [InlineData(440, false)]
    [InlineData(520, true)]
    public async Task InlineEditingAndValidation_KeepMeasuredSizeAndEscapeRestoresDraft(int width, bool dark)
    {
        var row = new FieldRow { Label = "用户名", Value = "alice@example.com", Placeholder = "添加用户名" };
        var window = new Window { Width = width, Height = 180, Content = new StackPanel { Children = { row } },
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show(); window.UpdateLayout();
            var before = row.Bounds;
            Assert.InRange(before.Height, 44, 64);
            row.BeginEdit();
            await Task.Delay(35); window.UpdateLayout();
            var editor = row.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Editor");
            editor.Text = "modified@example.com";
            Assert.Equal("modified@example.com", row.Value);
            row.ValidationMessage = "请检查输入";
            window.UpdateLayout();
            Assert.Equal(before, row.Bounds);
            row.EndEdit(cancel: true);
            window.UpdateLayout();
            Assert.Equal("alice@example.com", row.Value);
            Assert.Equal(before, row.Bounds);
            Assert.False(row.IsEditing);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SecretField_MasksFixedLengthAndConcealsWhenFocusLeaves()
    {
        var secret = new SecretField { Label = "密码", Value = "short" };
        var next = new Button { Content = "下一项" };
        var window = new Window { Width = 480, Height = 220,
            Content = new StackPanel { Children = { secret, next } } };
        try
        {
            window.Show(); window.UpdateLayout();
            Assert.Equal("••••••••••", secret.DisplayValue);
            Assert.Null(secret.ValueToolTip);
            secret.Focus(); secret.Revealed = true;
            Assert.Equal("short", secret.DisplayValue);
            next.Focus(); await Task.Delay(35);
            Assert.False(secret.Revealed);
            Assert.Equal("••••••••••", secret.DisplayValue);
            Assert.Equal("short", secret.Value);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task TotpRendering_DoesNotWriteGeneratedCodeIntoSecretBinding()
    {
        const string raw = "otpauth://totp/Test?secret=JBSWY3DPEHPK3PXP&digits=8&period=60";
        var field = new FieldEditorView(new FieldSpec(FieldKeys.Totp, "验证码", Sensitive: true), raw, false, _ => { });
        var totp = new TotpField();
        totp.Bind(FieldRow.ValueProperty, new Binding(nameof(FieldEditorView.Value)) { Source = field, Mode = BindingMode.TwoWay });
        var window = new Window { Width = 480, Height = 180, Content = totp };
        try
        {
            window.Show(); window.UpdateLayout(); await Task.Delay(1100);
            Assert.Equal(raw, field.Value);
            Assert.Equal(raw, totp.Value);
            Assert.Equal(8, totp.CopyValue.Length);
            Assert.Equal(9, totp.Code.Length);
            Assert.InRange(totp.RemainingSeconds, 1, 60);
            Assert.InRange(totp.RemainingSweep, 0, 360);
            Assert.Null(totp.ValueToolTip);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AccountTabs_ShowOnlyCurrentAccountWithoutDirtyingSourceAndResetOnEntryChange()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        Assert.False(fixture.Source.IsDirty);
        var username = UsernameRow(fixture.Panel);
        Assert.Equal("admin-user", username.Value);
        var tabs = fixture.Panel.AccountsControl.GetVisualDescendants().OfType<SegmentedControl>().Single();
        tabs.SelectedIndex = 1;
        // 切换账号后刷新排在后台优先级；冷启动时 50ms 可能不够，轮询到结果出现（最多 2 秒）。
        await WaitUntil(fixture.Window, () => UsernameRow(fixture.Panel).Value == "readonly-user");
        Assert.Equal("readonly-user", UsernameRow(fixture.Panel).Value);
        Assert.False(fixture.Source.IsDirty);
        var secret = fixture.Panel.GetVisualDescendants().OfType<SecretField>().Single(row => row.Label == "密码");
        secret.Revealed = true;
        fixture.Source.Load(fixture.Second.Id);
        fixture.Panel.Refresh(resetState: true);
        await Task.Delay(40); fixture.Window.UpdateLayout();
        Assert.Equal("second-entry-user", UsernameRow(fixture.Panel).Value);
        Assert.All(fixture.Panel.GetVisualDescendants().OfType<SecretField>(), item => Assert.False(item.Revealed));
        Assert.Equal(0, fixture.Panel.Scroller.Offset.Y);
        Assert.False(fixture.Source.IsDirty);
    }

    [AvaloniaFact]
    public async Task FieldIcons_AreVisibleWithoutHoverAndCompactWithNoEditButton()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(40); fixture.Window.UpdateLayout();
        var row = UsernameRow(fixture.Panel);
        // 鼠标不在行上：操作图标照样可见；编辑按钮不再显示（点击值本身进入编辑）。
        Assert.False(row.IsPointerOver);
        var actions = row.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "PART_Actions");
        Assert.Equal(1, actions.Opacity);
        Assert.False(row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_EditButton").IsVisible);
        var copy = row.GetVisualDescendants().OfType<CopyButton>().Single();
        Assert.True(copy.IsEffectivelyVisible);
        Assert.Equal(24, copy.Bounds.Width, precision: 1);
        Assert.Equal(24, copy.Bounds.Height, precision: 1);
    }

    [AvaloniaFact]
    public async Task ClickingAValue_EntersEditModeForEveryEditableRow()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(40); fixture.Window.UpdateLayout();
        var report = new List<string>();
        foreach (var row in fixture.Panel.GetVisualDescendants().OfType<FieldRow>().Where(r => r.CanEdit && r.ValueContent is null).ToList())
        {
            row.BringIntoView();
            fixture.Window.UpdateLayout();
            var value = row.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ValueButton");
            var point = value.TranslatePoint(new Point(12, value.Bounds.Height / 2), fixture.Window)!.Value;
            fixture.Window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            fixture.Window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await WaitUntil(fixture.Window, () => row.IsEditing, 500);
            report.Add($"{row.Label}={(row.IsEditing ? "edit" : "NO")}");
            row.EndEdit(cancel: true);
            fixture.Window.UpdateLayout();
        }
        Assert.NotEmpty(report);
        Assert.All(report, line => Assert.EndsWith("=edit", line));
    }

    [AvaloniaFact]
    public async Task ClickingTheTotpCode_EditsTheSecretAndFocusesTheEditor()
    {
        var totp = new TotpField { Label = "验证码", Value = "JBSWY3DPEHPK3PXP" };
        var window = new Window { Width = 480, Height = 180, Content = totp };
        try
        {
            window.Show(); window.UpdateLayout(); await Task.Delay(50);
            var value = totp.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "PART_ValueButton");
            var point = value.TranslatePoint(new Point(12, value.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            await WaitUntil(window, () => totp.IsEditing, 500);
            Assert.True(totp.IsEditing);
            var editor = totp.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "PART_Editor");
            await WaitUntil(window, () => editor.IsFocused, 500);
            Assert.True(editor.IsFocused);
            Assert.Equal("JBSWY3DPEHPK3PXP", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task HoveringRowsAndButtons_ChangesNothingVisually()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(40); fixture.Window.UpdateLayout();
        Point Center(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width / 2, visual.Bounds.Height / 2), fixture.Window)!.Value;

        // 鼠标停在字段行的复制按钮上：整行不置灰，按钮底色仍是透明。
        var row = UsernameRow(fixture.Panel);
        var copy = row.GetVisualDescendants().OfType<CopyButton>().Single();
        fixture.Window.MouseMove(Center(copy), RawInputModifiers.None);
        await Task.Delay(250); fixture.Window.UpdateLayout();
        Assert.True(copy.IsPointerOver);
        Assert.Equal(0, row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Hover").Opacity);
        var surface = copy.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_ButtonSurface");
        Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);

        // 鼠标停在快捷操作上：内部 ContentPresenter 的底色与按钮本身一致，不换灰。
        var quick = fixture.Panel.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("detail-quick-action"));
        fixture.Window.MouseMove(Center(quick), RawInputModifiers.None);
        await Task.Delay(250); fixture.Window.UpdateLayout();
        Assert.True(quick.IsPointerOver);
        var presenter = quick.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First();
        Assert.Equal(((ISolidColorBrush)quick.Background!).Color, ((ISolidColorBrush)presenter.Background!).Color);
    }

    [AvaloniaFact]
    public async Task EditingARow_OnlyTheInputChangesNotTheWholeRow()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(40); fixture.Window.UpdateLayout();
        var row = UsernameRow(fixture.Panel);
        var shown = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PART_ValueText");
        var before = shown.TranslatePoint(default, fixture.Window)!.Value;
        var rowBox = new Rect(row.TranslatePoint(default, fixture.Window)!.Value, row.Bounds.Size);

        row.BeginEdit();
        await WaitUntil(fixture.Window, () => row.IsEditing, 500);
        await Task.Delay(300); fixture.Window.UpdateLayout();   // 等过渡动画走完

        // 1) 整行不变灰
        Assert.Equal(0, row.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Hover").Opacity);
        // 2) 输入框自己有 1px 中性边框和独立底色（不是强调色，所以点进去不会出现蓝框）
        var editor = row.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Editor");
        Assert.True(editor.IsVisible);
        Assert.Equal(new Thickness(1), editor.BorderThickness);
        var border = Assert.IsAssignableFrom<ISolidColorBrush>(editor.BorderBrush);
        Assert.True(editor.TryFindResource("Detail.Focus", editor.ActualThemeVariant, out var focus), "找不到 Detail.Focus");
        Assert.NotEqual(((ISolidColorBrush)focus!).Color, border.Color);
        // 3) 进入编辑后文字没有位移：输入框的文字起点与原来显示文字的起点一致（边框画在文字外面）
        var presenter = editor.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.TextPresenter>().First();
        var after = presenter.TranslatePoint(default, fixture.Window)!.Value;
        Assert.InRange(Math.Abs(after.X - before.X), 0, 1.5);
        // 4) 整个编辑框仍在这一行之内，没有顶到别的行
        var editorBox = new Rect(editor.TranslatePoint(default, fixture.Window)!.Value, editor.Bounds.Size);
        Assert.InRange(editorBox.Top, rowBox.Top - 0.5, rowBox.Bottom);
        Assert.InRange(editorBox.Bottom, rowBox.Top, rowBox.Bottom + 0.5);
    }

    [AvaloniaFact]
    public async Task SwitchingBetweenEntriesOfTheSameKind_RebuildsConnectionFields()
    {
        // 两个条目同为"登录"类型：切换后连接信息（网址）必须换成第二个条目的值。
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(40); fixture.Window.UpdateLayout();
        string[] Values() => fixture.Panel.GetVisualDescendants().OfType<FieldRow>().Select(row => row.Value ?? "").ToArray();
        Assert.Contains("https://example.com", Values());

        fixture.Source.Load(fixture.Second.Id);
        fixture.Panel.Refresh(resetState: true);
        await Task.Delay(40); fixture.Window.UpdateLayout();
        Assert.Contains("https://another.example.com", Values());
        Assert.DoesNotContain("https://example.com", Values());
    }

    [AvaloniaFact]
    public async Task QuickActions_SitBetweenAccountTabsAndFieldsAndStayCompact()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        await Task.Delay(50); fixture.Window.UpdateLayout();
        var tabs = fixture.Panel.AccountsControl.GetVisualDescendants().OfType<SegmentedControl>().Single();
        var bar = fixture.Panel.GetVisualDescendants().OfType<QuickActionBar>().Single();
        double Top(Visual visual) => visual.TranslatePoint(default, fixture.Panel)!.Value.Y;
        // 顺序：账号页签 → 快捷操作 → 当前账号的字段；快捷操作复制的是所选账号的值。
        Assert.True(Top(bar) >= Top(tabs) + tabs.Bounds.Height, "快捷操作应在账号页签下方");
        Assert.True(Top(UsernameRow(fixture.Panel)) >= Top(bar) + bar.Bounds.Height, "快捷操作应在账号字段上方");
        var buttons = bar.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("detail-quick-action")).ToList();
        Assert.NotEmpty(buttons);
        Assert.All(buttons, button => Assert.InRange(button.Bounds.Height, 30, 40));
    }

    private static FieldRow UsernameRow(DetailPanelView panel) => panel.GetVisualDescendants().OfType<FieldRow>()
        .Single(row => row.Label == "用户名");

    private static async Task WaitUntil(Window window, Func<bool> condition, int timeoutMs = 2000)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && clock.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
            window.UpdateLayout();
        }
    }

    [AvaloniaFact]
    public async Task TypingInRealDetailEditor_PreservesTextBoxFocusAndCaretAcrossRefreshes()
    {
        using var fixture = new PanelFixture();
        fixture.Show();
        var row = UsernameRow(fixture.Panel);
        row.BeginEdit();
        await Task.Delay(40);
        var editor = row.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Editor");
        fixture.Window.KeyTextInput("new");
        await Task.Delay(65); fixture.Window.UpdateLayout();
        Assert.Same(editor, UsernameRow(fixture.Panel).GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PART_Editor"));
        Assert.True(editor.IsFocused);
        Assert.Equal("new", editor.Text);
        Assert.Equal(3, editor.CaretIndex);
        fixture.Window.KeyTextInput("-user");
        await Task.Delay(65); fixture.Window.UpdateLayout();
        Assert.True(editor.IsFocused);
        Assert.Equal("new-user", editor.Text);
        Assert.Equal(8, editor.CaretIndex);
        Assert.Equal("new-user", fixture.Source.Accounts[0].UsernameField.Value);
        row.EndEdit(cancel: true);
        Assert.Equal("admin-user", fixture.Source.Accounts[0].UsernameField.Value);
        Assert.False(fixture.Source.IsDirty);
    }

    private sealed class PanelFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "keep-password-detail-tests", Guid.NewGuid().ToString("N"));
        private readonly VaultSession _session;
        public VaultEntry First { get; } = new() { Name = "测试登录", Url = "https://example.com",
            Accounts = [new VaultAccount { Username = "admin-user", Password = "test-password-one", Note = "管理员" },
                new VaultAccount { Username = "readonly-user", Password = "test-password-two", Note = "只读" }] };
        public VaultEntry Second { get; } = new() { Name = "另一个条目", Url = "https://another.example.com",
            Accounts = [new VaultAccount { Username = "second-entry-user", Password = "test-password-three" }] };
        public MainViewModel Source { get; }
        public DetailPanelView Panel { get; } = new();
        public Window Window { get; }
        public PanelFixture()
        {
            Directory.CreateDirectory(_folder);
            _session = new VaultStore(KdfProfile.Fast).CreateSession(Path.Combine(_folder, "vault.kp"), "test", "master-password", "short-key");
            _session.Upsert(First); _session.Upsert(Second); _session.Save();
            Source = new MainViewModel(_session, false);
            Source.Load(First.Id);
            Window = new Window { Width = 480, Height = 740, Content = Panel };
        }
        public void Show()
        {
            Panel.SetEditor(Source); Window.Show(); Window.UpdateLayout();
        }
        public void Dispose()
        {
            Panel.SetEditor(null); Window.Close(); _session.Dispose();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
