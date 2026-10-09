using System;
using System.Globalization;
using System.Windows.Data;

namespace PolarAlignLive.UI {

    /// <summary>
    /// Takes (width, height) of the available area and returns the side of the largest square that fits,
    /// but never less than Floor (below that the panel scrolls instead of shrinking further).
    /// </summary>
    public class SquareSizeConverter : IMultiValueConverter {
        public double Floor { get; set; } = 320;

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) {
            double w = double.PositiveInfinity, h = double.PositiveInfinity;
            if (values != null && values.Length > 0 && values[0] is double a && !double.IsNaN(a)) w = a;
            if (values != null && values.Length > 1 && values[1] is double b && !double.IsNaN(b)) h = b;
            double side = Math.Min(w, h);
            if (double.IsInfinity(side)) side = Floor;
            return Math.Max(Floor, Math.Floor(side));
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
