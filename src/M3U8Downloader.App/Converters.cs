using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace M3U8Downloader;

/// <summary>
/// bool → Visibility。WinUI 3 没有内置转换器（WPF 才自带），所以自己写一个。
/// 传 ConverterParameter="invert" 可反向（true → 折叠）。
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is true;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
