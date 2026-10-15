using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using KeepPassword.App.Views;

namespace KeepPassword.App.Tests;

public class AsciiInputTests
{
    [AvaloniaTheory]
    [InlineData("NewKeyBox")]
    [InlineData("ConfirmBox")]
    [InlineData("MasterBox")]
    [InlineData("CurrentMasterBox")]
    [InlineData("NewMasterBox")]
    [InlineData("ConfirmMasterBox")]
    public async Task SettingsPasswordFields_RejectNonAsciiTypingAndPaste(string name)
    {
        var settings = new SettingsView();
        var window = new Window { Content = settings, Width = 1000, Height = 900 };
        window.Show();
        try
        {
            var box = settings.FindControl<TextBox>(name)!;
            box.BringIntoView();
            window.UpdateLayout();
            box.Focus();
            window.KeyTextInput("Abc123!@# +/=");
            Assert.Equal("Abc123!@# +/=", box.Text);
            window.KeyTextInput("中文");
            Assert.Equal("Abc123!@# +/=", box.Text);
            var reveal = Assert.IsType<Button>(box.InnerRightContent);
            reveal.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(box.RevealPassword);
            window.KeyTextInput("混合ABC");
            Assert.Equal("Abc123!@# +/=", box.Text);

            // Use only the isolated headless clipboard, never the user's desktop clipboard.
            await window.Clipboard!.SetTextAsync("new中文password");
            box.SelectAll();
            box.Paste();
            await Task.Yield();
            Assert.Equal("Abc123!@# +/=", box.Text);
            Assert.Equal(0, box.SelectionStart);
            Assert.Equal(box.Text!.Length, box.SelectionEnd);

            await window.Clipboard.SetTextAsync("Next-pass_42!");
            box.Paste();
            await Task.Yield();
            Assert.Equal("Next-pass_42!", box.Text);
            Assert.False(InputMethod.GetIsInputMethodEnabled(box));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void OrdinarySettingsFields_StillAcceptChinese()
    {
        var settings = new SettingsView();
        var window = new Window { Content = settings };
        window.Show();
        try
        {
            var box = settings.FindControl<TextBox>("NewTagBox")!;
            box.Focus();
            window.KeyTextInput("工作账号");
            Assert.Equal("工作账号", box.Text);
            Assert.True(InputMethod.GetIsInputMethodEnabled(box));
        }
        finally { window.Close(); }
    }
}
