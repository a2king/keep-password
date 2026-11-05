using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using KeepPassword.App.Controls;
using KeepPassword.App.Services;
using KeepPassword.Core.Security;

namespace KeepPassword.App.Views;

public partial class PasswordGeneratorView : UserControl
{
    private PasswordGeneratorKind _kind = PasswordGeneratorKind.Random;
    private bool _adjusting;
    private string _password = "";

    public PasswordGeneratorView()
    {
        InitializeComponent();
        LengthSlider.PropertyChanged += OnLengthPropertyChanged;
        DigitSwitch.PropertyChanged += OnOptionPropertyChanged;
        SymbolSwitch.PropertyChanged += OnOptionPropertyChanged;
        CapitalSwitch.PropertyChanged += OnOptionPropertyChanged;
        FullWordSwitch.PropertyChanged += OnOptionPropertyChanged;
        Loaded += (_, _) => FixCardWidth();
        ResultBox.SizeChanged += (_, _) => LayoutPassword();
        ApplyKind(PasswordGeneratorKind.Random, resetValue: true);
    }

    public void EnsureGenerated()
    {
        if (string.IsNullOrEmpty(_password))
        {
            Generate();
        }
    }

    public void ClearSecret()
    {
        _password = "";
        ResultText.Text = "";
        CopyStatus.Text = "";
    }

    // 分段切换只负责把选中项翻译成原有的 ApplyKind 调用；程序同步选中项时（_adjusting）不重复触发。
    private void OnKindChanged(object? sender, SelectionChangedEventArgs e)
    {
        // XAML 加载时会先自动选中第 0 项，此时 KindSegments 字段尚未赋值，直接用 sender。
        if (_adjusting || sender is not SegmentedControl segments) return;
        var kind = segments.SelectedIndex switch
        {
            1 => PasswordGeneratorKind.Memorable,
            2 => PasswordGeneratorKind.Pin,
            _ => PasswordGeneratorKind.Random
        };
        if (kind != _kind) ApplyKind(kind, resetValue: true);
    }

    private void OnRefresh(object? sender, RoutedEventArgs e)
    {
        Generate();
        _ = Motion.EnterAsync(ResultBox, 0);
    }

    private void OnCopy(object? sender, RoutedEventArgs e) => _ = CopyPasswordAsync();

    // 点击结果框与"复制密码"按钮共用同一复制流程（含 30 秒自动清除与按钮反馈）。
    private void OnResultTapped(object? sender, TappedEventArgs e) => _ = CopyPasswordAsync();

    private void OnResultKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        _ = CopyPasswordAsync();
    }

    private async Task CopyPasswordAsync()
    {
        if (string.IsNullOrEmpty(_password)
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        CopyStatus.Text = "已复制。若 30 秒后剪贴板仍是该内容，会自动清除。";
        _ = Motion.EnterAsync(CopyStatus, 3);
        await SecretClipboard.CopyAsync(clipboard, _password);
        _ = CopyFeedback.ShowAsync(CopyButton);
    }

    private void OnLengthPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty)
        {
            return;
        }

        LengthValue.Text = ((int)LengthSlider.Value).ToString();
        if (!_adjusting)
        {
            Generate();
        }
    }

    private void OnOptionPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == ToggleSwitch.IsCheckedProperty && !_adjusting)
        {
            Generate();
        }
    }

    private void ApplyKind(PasswordGeneratorKind kind, bool resetValue)
    {
        _adjusting = true;
        _kind = kind;
        RandomOptions.IsVisible = kind == PasswordGeneratorKind.Random;
        MemorableOptions.IsVisible = kind == PasswordGeneratorKind.Memorable;
        KindSegments.SelectedIndex = kind switch
        {
            PasswordGeneratorKind.Memorable => 1,
            PasswordGeneratorKind.Pin => 2,
            _ => 0
        };
        if (resetValue)
        {
            switch (kind)
            {
                case PasswordGeneratorKind.Memorable:
                    LengthSlider.Minimum = 3;
                    LengthSlider.Maximum = 10;
                    LengthSlider.Value = 4;
                    break;
                case PasswordGeneratorKind.Pin:
                    LengthSlider.Minimum = 4;
                    LengthSlider.Maximum = 12;
                    LengthSlider.Value = 6;
                    break;
                default:
                    LengthSlider.Minimum = 8;
                    LengthSlider.Maximum = 64;
                    LengthSlider.Value = 16;
                    DigitSwitch.IsChecked = true;
                    SymbolSwitch.IsChecked = false;
                    break;
            }
        }

        LengthValue.Text = ((int)LengthSlider.Value).ToString();
        _adjusting = false;
        Generate();
        _ = Motion.EnterAsync(ResultBox, 0);
    }

    private void FixCardWidth()
    {
        var family = ResultText.FontFamily ?? new FontFamily("Inter, Segoe UI Variable, Microsoft YaHei UI, sans-serif");
        var formatted = new FormattedText(
            new string('M', 32),
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, ResultText.FontStyle, ResultText.FontWeight),
            ResultText.FontSize,
            null);
        var cardPad = Card.Padding.Left + Card.Padding.Right;
        if (cardPad <= 0)
        {
            cardPad = 64;
        }

        var boxPad = ResultBox.Padding.Left + ResultBox.Padding.Right
            + ResultBox.BorderThickness.Left + ResultBox.BorderThickness.Right;
        if (boxPad <= 0)
        {
            boxPad = 26;
        }

        var width = Math.Ceiling(formatted.Width + cardPad + boxPad + Card.BorderThickness.Left + Card.BorderThickness.Right);
        Card.Width = width;
        Card.MinWidth = width;
        Card.MaxWidth = width;
    }

    private void Generate()
    {
        CopyStatus.Text = "";
        var length = Math.Max((int)LengthSlider.Value, 1);
        var password = PasswordGenerator.Generate(new PasswordGeneratorOptions
        {
            Kind = _kind,
            Length = length,
            Lowercase = true,
            Uppercase = true,
            Digits = _kind == PasswordGeneratorKind.Random && DigitSwitch.IsChecked == true,
            Symbols = _kind == PasswordGeneratorKind.Random && SymbolSwitch.IsChecked == true,
            ExcludeAmbiguous = true,
            Capitalize = CapitalSwitch.IsChecked == true,
            FullWords = FullWordSwitch.IsChecked != false
        });
        _password = password;
        LayoutPassword();
    }

    private void LayoutPassword()
    {
        var inner = ResultBox.Bounds.Width
            - ResultBox.Padding.Left - ResultBox.Padding.Right
            - ResultBox.BorderThickness.Left - ResultBox.BorderThickness.Right;
        ResultText.Text = inner > 1 ? FitToWidth(_password, inner) : _password;
    }

    private string FitToWidth(string text, double maxWidth)
    {
        if (text.Length == 0 || Measure(text) <= maxWidth)
        {
            return text;
        }

        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var fit = 1;
            var low = 1;
            var high = text.Length - start;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (Measure(text.Substring(start, mid)) <= maxWidth)
                {
                    fit = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            lines.Add(text.Substring(start, fit));
            start += fit;
        }

        return string.Join('\n', lines);
    }

    private double Measure(string text)
    {
        var family = ResultText.FontFamily ?? new FontFamily("Inter, Segoe UI Variable, Microsoft YaHei UI, sans-serif");
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, ResultText.FontStyle, ResultText.FontWeight),
            ResultText.FontSize,
            null);
        return formatted.Width;
    }
}
