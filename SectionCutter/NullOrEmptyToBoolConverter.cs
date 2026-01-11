using System;
using System.Globalization;
using System.Windows.Data;

namespace SectionCutter.Converters
{
    public class NullOrEmptyToBoolConverter : IValueConverter
    {
        public bool Invert { get; set; } = false;

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            bool isNullOrEmpty = value switch
            {
                null => true,
                string s => string.IsNullOrWhiteSpace(s),
                _ => false
            };

            bool result = !isNullOrEmpty;
            return Invert ? !result : result;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
