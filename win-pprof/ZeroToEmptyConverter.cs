using System;
using System.Globalization;
using System.Windows.Data;

namespace win_pprof
{
    [ValueConversion(typeof(long), typeof(string))]
    class ZeroToEmptyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            long number = (long)value;
            return (number == 0) ? string.Empty : number.ToString();
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
