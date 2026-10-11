using System.Globalization;
using System.Windows.Data;
namespace SupportDesk.Desktop;
public sealed class CommandLabelConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length == 2 && values[0] is string id && values[1] is IReadOnlyDictionary<string, string> labels ? labels.TryGetValue(id, out var label) ? "Вставка: " + label : "" : "";
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
