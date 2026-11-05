using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using KeepPassword.App.Controls.Details;
using KeepPassword.App.Services;
using KeepPassword.App.ViewModels;

namespace KeepPassword.App.Tests;

public class DetailFieldControlTests
{
    [AvaloniaFact]
    public void CustomSelector_RemainsInteractiveWithoutReplacingItWithTextEditor()
    {
        var selector = new ComboBox { ItemsSource = new[] { "SSH", "RDP" }, SelectedIndex = 0 };
        var field = new FieldRow { Label = "协议", ValueContent = selector, CanCopy = false };
        var window = new Window { Width = 480, Height = 180, Content = field };
        window.Show();
        try
        {
            window.UpdateLayout();
            var edit = field.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "PART_EditButton");
            var custom = field.GetVisualDescendants().OfType<ContentPresenter>().Single(control => control.Name == "PART_CustomValue");
            Assert.False(edit.IsVisible);
            Assert.True(custom.IsHitTestVisible);
            field.BeginEdit();
            Assert.False(field.IsEditing);
            Assert.True(selector.IsEffectivelyVisible);
            selector.SelectedIndex = 1;
            Assert.Equal("RDP", selector.SelectedItem);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void CorrectedValue_ClearsStaleValidationAndTotpIsCheckedAgainOnCommit()
    {
        var field = new TotpField { Value = "invalid!" };
        field.BeginEdit();
        field.EndEdit();
        Assert.True(field.HasValidation);
        Assert.True(field.IsEditing);
        field.Value = "still-invalid!";
        Assert.False(field.HasValidation);
        field.EndEdit();
        Assert.True(field.HasValidation);
        field.Value = "JBSWY3DPEHPK3PXP";
        Assert.False(field.HasValidation);
        field.EndEdit();
        Assert.False(field.HasValidation);
        Assert.False(field.IsEditing);
    }

    [AvaloniaFact]
    public void SecretField_HidingItsAncestorConcealsTheValue()
    {
        var field = new SecretField { Value = "test-secret" };
        var panel = new StackPanel { Children = { field } };
        var window = new Window { Width = 480, Height = 180, Content = panel };
        window.Show();
        try
        {
            window.UpdateLayout();
            field.Revealed = true;
            panel.IsVisible = false;
            Assert.False(field.Revealed);
            panel.IsVisible = true;
            Assert.Equal("••••••••••", field.DisplayValue);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task InlineEdit_CancelRestoresValueAndKeepsTheSameRowSize()
    {
        var field = new FieldRow { Label = "主机", Value = "db.example", ValidationMessage = "" };
        var window = new Window { Width = 480, Height = 180, Content = field };
        window.Show();
        try
        {
            window.UpdateLayout();
            var before = field.Bounds;
            field.BeginEdit();
            await Task.Yield();
            field.Value = "changed.example";
            field.ValidationMessage = "地址格式无效";
            window.UpdateLayout();
            Assert.Equal(before, field.Bounds);
            field.EndEdit(cancel: true);
            window.UpdateLayout();
            Assert.Equal("db.example", field.Value);
            Assert.False(field.IsEditing);
            Assert.Equal(before, field.Bounds);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TotpTick_UsesStoredParametersAndNeverOverwritesTheSecret()
    {
        const string secret = "otpauth://totp/Demo?secret=JBSWY3DPEHPK3PXP&digits=8&period=60";
        var baseline = TotpTicker.SubscriberCount;
        var field = new TotpField { Value = secret };
        var window = new Window { Width = 480, Height = 180, Content = field };
        window.Show();
        try
        {
            window.UpdateLayout();
            TotpTicker.Publish(DateTimeOffset.FromUnixTimeSeconds(1234));
            Assert.Equal(secret, field.Value);
            Assert.Equal(secret, field.Secret);
            Assert.Equal(8, field.CopyValue.Length);
            Assert.DoesNotContain(" ", field.CopyValue);
            Assert.Equal(26, field.RemainingSeconds);
            Assert.Equal(156, field.RemainingSweep, precision: 2);
            Assert.Equal(baseline + 1, TotpTicker.SubscriberCount);
        }
        finally { window.Close(); }
        Assert.Equal(baseline, TotpTicker.SubscriberCount);
    }

    [AvaloniaFact]
    public async Task ChipsTypingAndChoiceUpdates_PreserveTheInlineInputInstance()
    {
        var selected = new LabelChipView { Name = "生产", IsSelected = true };
        var choice = new LabelChipView { Name = "开发" };
        var editor = new ChipsEditor { Multiple = true, Items = new[] { selected, choice } };
        var window = new Window { Width = 480, Height = 180, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            var input = editor.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
            Assert.False(input.IsVisible);
            var add = editor.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "PART_Add");
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Yield();
            input.Text = "开";
            choice.IsSelected = true;
            Assert.Same(input, editor.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input"));
            Assert.Equal("开", input.Text);
            editor.ClosePopup();
            Assert.False(input.IsVisible);
            Assert.Equal("", input.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task ChipsSuggestions_AreExactlyAsWideAsTheInputAndLeftAligned()
    {
        var editor = new ChipsEditor { Multiple = true, Items = new[] { new LabelChipView { Name = "开发" }, new LabelChipView { Name = "一个很长很长很长很长很长的标签名称" } } };
        var window = new Window { Width = 520, Height = 360, Content = editor };
        window.Show();
        try
        {
            window.UpdateLayout();
            editor.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "PART_Add").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60); window.UpdateLayout();
            var input = editor.GetVisualDescendants().OfType<TextBox>().Single(control => control.Name == "PART_Input");
            var popup = editor.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single(control => control.Name == "PART_Suggestions");
            Assert.True(popup.IsOpen);
            var panel = Assert.IsType<Border>(popup.Child);
            await Task.Delay(60); panel.UpdateLayout();
            // 可见面板与输入框同宽，左边缘对齐（外层透明边距由 HorizontalOffset 抵消）。
            Assert.Equal(input.Bounds.Width, panel.Bounds.Width, precision: 1);
            var inputLeft = input.PointToScreen(default).X;
            var panelLeft = panel.PointToScreen(default).X;
            Assert.InRange(Math.Abs(panelLeft - inputLeft), 0, 1);
        }
        finally { window.Close(); }
    }
}
