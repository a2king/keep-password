using System.Globalization;
using Avalonia.Data.Converters;

namespace KeepPassword.App.Controls;

/// <summary>玻璃控件模板用到的只读转换器，仅影响显示，不写回任何 ViewModel。</summary>
public static class GlassConverters
{
    /// <summary>null 或空字符串视为"无内容"，用于收起开关右侧的空文字区与间距。</summary>
    public static readonly IValueConverter HasContent = new FuncValueConverter<object?, bool>(value =>
        value switch
        {
            null => false,
            string text => text.Length > 0,
            _ => true
        });

    /// <summary>认证方式 bool（UseKeyAuth）→ 分段索引：false=0「密码」，true=1「SSH 密钥」。</summary>
    public static readonly IValueConverter BoolToIndex = new BoolToIndexConverter();

    private sealed class BoolToIndexConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true ? 1 : 0;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is 1;
    }
}
