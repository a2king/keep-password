using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;

namespace KeepPassword.App.Views;

/// <summary>
/// 密码类输入框：聚焦时把中日韩输入法切到英文模式（不换键盘布局），其他非英文键盘才切换到英文布局；
/// 禁用控件的 IME 组词，键入和粘贴仅接受可打印 ASCII；失焦或窗口失活时还原输入法状态。
/// </summary>
public static class AsciiInput
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(AsciiInput));

    private static TextBox? _active;
    private static TopLevel? _activeTop;
    private static WindowsIme.State? _saved;

    static AsciiInput()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>(OnChanged);
    }

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);

    private static void OnChanged(TextBox box, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            InputMethod.SetIsInputMethodEnabled(box, false);
            box.GotFocus += OnGotFocus;
            box.LostFocus += OnLostFocus;
            box.DetachedFromVisualTree += OnDetached;
            box.PastingFromClipboard += OnPaste;
            box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        }
        else
        {
            OnLostFocus(box, new RoutedEventArgs());
            box.ClearValue(InputMethod.IsInputMethodEnabledProperty);
            box.GotFocus -= OnGotFocus;
            box.LostFocus -= OnLostFocus;
            box.DetachedFromVisualTree -= OnDetached;
            box.PastingFromClipboard -= OnPaste;
            box.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        }
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) =>
        OnLostFocus(sender, new RoutedEventArgs());

    private static void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is not TextBox box || TopLevel.GetTopLevel(box) is not { } top)
        {
            return;
        }

        ReleaseActive();
        _active = box;
        _activeTop = top;
        if (top is Window window)
        {
            window.Activated += OnWindowActivated;
            window.Deactivated += OnWindowDeactivated;
        }

        SwitchToEnglish(top);
    }

    private static void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || !ReferenceEquals(box, _active))
        {
            return;
        }

        ReleaseActive();
    }

    private static void ReleaseActive()
    {
        if (_activeTop is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
        }

        _active = null;
        Restore(_activeTop);
        _activeTop = null;
    }

    private static void OnWindowActivated(object? sender, EventArgs e)
    {
        if (_active is { IsFocused: true } && sender is TopLevel top)
        {
            SwitchToEnglish(top);
        }
    }

    private static void OnWindowDeactivated(object? sender, EventArgs e) => Restore(sender as TopLevel);

    private static void SwitchToEnglish(TopLevel top)
    {
        if (!OperatingSystem.IsWindows() || _saved is not null || top.TryGetPlatformHandle()?.Handle is not { } hwnd)
        {
            return;
        }

        _saved = WindowsIme.SwitchToEnglish(hwnd);
    }

    private static void Restore(TopLevel? top)
    {
        if (_saved is not { } saved)
        {
            return;
        }

        _saved = null;
        if (OperatingSystem.IsWindows() && top?.TryGetPlatformHandle()?.Handle is { } hwnd)
        {
            WindowsIme.Restore(hwnd, saved);
        }
    }

    private static void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text))
        {
            return;
        }

        if (!IsPrintableAscii(e.Text))
        {
            e.Handled = true;
        }
    }

    private static bool IsPrintableAscii(string text) => text.All(c => c is >= ' ' and <= '~');

    private static async void OnPaste(object? sender, RoutedEventArgs e)
    {
        // TextBox.Paste bypasses TextInput. Reject the whole operation instead of silently
        // changing a password by removing unsupported characters.
        e.Handled = true;
        if (sender is not TextBox { IsReadOnly: false } box || TopLevel.GetTopLevel(box) is not { Clipboard: { } clipboard } top)
        {
            return;
        }

        var before = box.Text;
        var start = box.SelectionStart;
        var end = box.SelectionEnd;
        try
        {
            var text = await clipboard.TryGetTextAsync();
            if (string.IsNullOrEmpty(text) || !IsPrintableAscii(text)
                || !GetIsEnabled(box) || !box.IsFocused || !box.IsEffectivelyVisible || box.IsReadOnly
                || TopLevel.GetTopLevel(box) != top || box.Text != before
                || box.SelectionStart != start || box.SelectionEnd != end)
            {
                return;
            }
            box.SelectedText = text;
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException)
        {
            // A busy/unavailable clipboard must leave the password unchanged.
        }
    }

    private static class WindowsIme
    {
        private const int LangEnglish = 0x09;
        private const int LangChinese = 0x04;
        private const int LangJapanese = 0x11;
        private const int LangKorean = 0x12;
        private const uint WmImeControl = 0x0283;
        private const int ImcGetConversionMode = 0x0001;
        private const int ImcSetConversionMode = 0x0002;
        private const int ImcGetOpenStatus = 0x0005;
        private const int ImcSetOpenStatus = 0x0006;

        public sealed record State(IntPtr Layout, bool LayoutChanged, IntPtr OpenStatus, IntPtr ConversionMode, bool ModeChanged);

        public static State SwitchToEnglish(IntPtr hwnd)
        {
            var layout = GetKeyboardLayout(0);
            if (IsEnglish(layout))
            {
                return new State(layout, false, IntPtr.Zero, IntPtr.Zero, false);
            }

            if (!HasImeMode(layout))
            {
                if (FindEnglishLayout() is { } english && ActivateKeyboardLayout(english, 0) != IntPtr.Zero)
                {
                    return new State(layout, true, IntPtr.Zero, IntPtr.Zero, false);
                }

                return new State(layout, false, IntPtr.Zero, IntPtr.Zero, false);
            }

            var imeWindow = ImmGetDefaultIMEWnd(hwnd);
            if (imeWindow == IntPtr.Zero)
            {
                return new State(layout, false, IntPtr.Zero, IntPtr.Zero, false);
            }

            var open = SendMessage(imeWindow, WmImeControl, ImcGetOpenStatus, IntPtr.Zero);
            var mode = SendMessage(imeWindow, WmImeControl, ImcGetConversionMode, IntPtr.Zero);
            SendMessage(imeWindow, WmImeControl, ImcSetConversionMode, IntPtr.Zero);
            SendMessage(imeWindow, WmImeControl, ImcSetOpenStatus, IntPtr.Zero);
            return new State(layout, false, open, mode, true);
        }

        public static void Restore(IntPtr hwnd, State state)
        {
            if (state.LayoutChanged)
            {
                ActivateKeyboardLayout(state.Layout, 0);
            }
            else if (state.ModeChanged && ImmGetDefaultIMEWnd(hwnd) is var imeWindow && imeWindow != IntPtr.Zero)
            {
                SendMessage(imeWindow, WmImeControl, ImcSetOpenStatus, state.OpenStatus);
                SendMessage(imeWindow, WmImeControl, ImcSetConversionMode, state.ConversionMode);
            }
        }

        private static bool IsEnglish(IntPtr layout) => ((long)layout & 0x3FF) == LangEnglish;

        private static bool HasImeMode(IntPtr layout) => ((long)layout & 0x3FF) is LangChinese or LangJapanese or LangKorean;

        private static IntPtr? FindEnglishLayout()
        {
            var count = GetKeyboardLayoutList(0, null);
            if (count <= 0)
            {
                return null;
            }

            var layouts = new IntPtr[count];
            GetKeyboardLayoutList(count, layouts);
            return layouts.Where(IsEnglish).Select(layout => (IntPtr?)layout).FirstOrDefault();
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint threadId);

        [DllImport("user32.dll")]
        private static extern int GetKeyboardLayoutList(int count, IntPtr[]? layouts);

        [DllImport("user32.dll")]
        private static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);

        [DllImport("imm32.dll")]
        private static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, nint wParam, IntPtr lParam);
    }
}
