using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace KeepPassword.App.Views;

/// <summary>
/// 密码类输入框：聚焦时把输入法切到英文，失焦或窗口失活时还原，并丢弃键入的非 ASCII 字符。
/// </summary>
public static class AsciiInput
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(AsciiInput));

    private static TextBox? _active;
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
            box.GotFocus += OnGotFocus;
            box.LostFocus += OnLostFocus;
            box.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
        }
        else
        {
            box.GotFocus -= OnGotFocus;
            box.LostFocus -= OnLostFocus;
            box.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
        }
    }

    private static void OnGotFocus(object? sender, GotFocusEventArgs e)
    {
        if (sender is not TextBox box || TopLevel.GetTopLevel(box) is not { } top)
        {
            return;
        }

        if (_active is { } previous && TopLevel.GetTopLevel(previous) is Window old)
        {
            old.Activated -= OnWindowActivated;
            old.Deactivated -= OnWindowDeactivated;
        }

        _active = box;
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

        if (TopLevel.GetTopLevel(box) is Window window)
        {
            window.Activated -= OnWindowActivated;
            window.Deactivated -= OnWindowDeactivated;
        }

        _active = null;
        Restore(TopLevel.GetTopLevel(box));
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

        var kept = new string(e.Text.Where(c => c is >= ' ' and <= '~').ToArray());
        if (kept.Length == 0)
        {
            e.Handled = true;
        }
        else if (kept.Length != e.Text.Length)
        {
            e.Text = kept;
        }
    }

    private static class WindowsIme
    {
        private const int LangEnglish = 0x09;
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

            if (FindEnglishLayout() is { } english && ActivateKeyboardLayout(english, 0) != IntPtr.Zero)
            {
                return new State(layout, true, IntPtr.Zero, IntPtr.Zero, false);
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
