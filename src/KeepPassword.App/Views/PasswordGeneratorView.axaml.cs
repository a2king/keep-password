using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    public void ClearSecret()
    {
        _password = "";
        ResultText.Text = "";
        CopyStatus.Text = "";
    }

    private void OnRandom(object? sender, RoutedEventArgs e) => ApplyKind(PasswordGeneratorKind.Random, resetValue: true);

    private void OnMemorable(object? sender, RoutedEventArgs e) => ApplyKind(PasswordGeneratorKind.Memorable, resetValue: true);

    private void OnPin(object? sender, RoutedEventArgs e) => ApplyKind(PasswordGeneratorKind.Pin, resetValue: true);

    private void OnRefresh(object? sender, RoutedEventArgs e) => Generate();

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_password) || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
        {
            return;
        }

        CopyStatus.Text = "已复制。若 30 秒后剪贴板仍是该内容，会自动清除。";
        await SecretClipboard.CopyAsync(clipboard, _password);
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
        SetSegment(RandomButton, kind == PasswordGeneratorKind.Random);
        SetSegment(MemorableButton, kind == PasswordGeneratorKind.Memorable);
        SetSegment(PinButton, kind == PasswordGeneratorKind.Pin);
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
    }

    private static void SetSegment(Button button, bool selected)
    {
        if (selected)
        {
            button.Classes.Add("selected");
            return;
        }

        button.Classes.Remove("selected");
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
