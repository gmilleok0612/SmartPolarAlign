using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PolarAlignLive.UI {

    /// <summary>
    /// Polar Align Pro style display: bullseye = celestial pole, dot = where the RA axis currently points,
    /// chevrons at the edges show which way to move the axis (more chevrons = further).
    /// Night-vision red. Auto-ranging rings.
    /// </summary>
    public class PolarDisplay : FrameworkElement {

        public static readonly DependencyProperty MoveUpProperty = DependencyProperty.Register(
            nameof(MoveUp), typeof(double), typeof(PolarDisplay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MoveEastProperty = DependencyProperty.Register(
            nameof(MoveEast), typeof(double), typeof(PolarDisplay),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HasSolutionProperty = DependencyProperty.Register(
            nameof(HasSolution), typeof(bool), typeof(PolarDisplay),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TintProperty = DependencyProperty.Register(
            nameof(Tint), typeof(Color), typeof(PolarDisplay),
            new FrameworkPropertyMetadata(Color.FromRgb(0xB0, 0x18, 0x18), FrameworkPropertyMetadataOptions.AffectsRender));

        public Color Tint { get => (Color)GetValue(TintProperty); set => SetValue(TintProperty, value); }

        /// <summary>Arcmin the axis must move up (negative = down).</summary>
        public double MoveUp { get => (double)GetValue(MoveUpProperty); set => SetValue(MoveUpProperty, value); }
        /// <summary>Arcmin the axis must move east (negative = west). East is to the right when facing north.</summary>
        public double MoveEast { get => (double)GetValue(MoveEastProperty); set => SetValue(MoveEastProperty, value); }
        public bool HasSolution { get => (bool)GetValue(HasSolutionProperty); set => SetValue(HasSolutionProperty, value); }

        private static readonly Typeface Face = new Typeface("Segoe UI");

        private static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

        protected override void OnRender(DrawingContext dc) {
            double w = ActualWidth, h = ActualHeight;
            if (w < 20 || h < 20) return;
            var c = new Point(w / 2, h / 2);
            double rMax = Math.Min(w, h) / 2 - 28;
            if (rMax < 12) rMax = Math.Max(6, Math.Min(w, h) / 2 - 4);   // very small window: drop the label margin

            var tint = Tint;
            Brush Red = new SolidColorBrush(tint);
            Brush DimRed = new SolidColorBrush(Color.FromRgb((byte)(tint.R * 0.55), (byte)(tint.G * 0.55), (byte)(tint.B * 0.55)));
            var RingPen = new Pen(DimRed, 1.5);
            var BoldPen = new Pen(Red, 3);
            var ChevronPen = new Pen(Red, 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

            // Auto range: outer ring = nice number >= 1.3 * current error.
            double err = HasSolution ? Math.Sqrt(MoveUp * MoveUp + MoveEast * MoveEast) : 20;
            double range = NiceCeil(Math.Max(2.0, err * 1.3));
            double pxPerArcmin = rMax / range;

            // Rings at 1/3, 2/3 and 3/3 of range, plus the bullseye.
            for (int i = 1; i <= 3; i++) {
                double r = rMax * i / 3.0;
                dc.DrawEllipse(null, RingPen, c, r, r);
                Label(dc, Fmt(range * i / 3.0), new Point(c.X + r * 0.71 + 4, c.Y - r * 0.71 - 12), 11, DimRed);
            }
            dc.DrawLine(RingPen, new Point(c.X - rMax, c.Y), new Point(c.X + rMax, c.Y));
            dc.DrawLine(RingPen, new Point(c.X, c.Y - rMax), new Point(c.X, c.Y + rMax));
            dc.DrawEllipse(null, BoldPen, c, 9, 9); // pole bullseye

            Label(dc, "N", new Point(c.X - 5, c.Y - rMax - 22), 13, Red);
            Label(dc, "E", new Point(c.X + rMax + 6, c.Y - 9), 13, Red);
            Label(dc, "W", new Point(c.X - rMax - 20, c.Y - 9), 13, Red);

            if (!HasSolution) return;

            // Axis position relative to the pole is the negative of the required move. East = right.
            var dot = new Point(c.X - MoveEast * pxPerArcmin, c.Y + MoveUp * pxPerArcmin);
            // Clamp to outer ring so a huge error stays visible.
            var dv = dot - c;
            if (dv.Length > rMax) { dv.Normalize(); dv *= rMax; dot = c + dv; }

            dc.DrawEllipse(Red, null, dot, 7, 7);
            dc.DrawLine(new Pen(Red, 1.5) { DashStyle = DashStyles.Dash }, dot, c);

            // Chevrons along each crosshair arm pointing the way to push the axis. East = +x, up = -y (screen).
            DrawChevrons(dc, ChevronPen, c, rMax, MoveEast > 0 ? 1 : -1, 0, Math.Abs(MoveEast));
            DrawChevrons(dc, ChevronPen, c, rMax, 0, MoveUp > 0 ? -1 : 1, Math.Abs(MoveUp));
        }

        private static int Count(double arcmin) => arcmin < 0.5 ? 0 : arcmin < 3 ? 1 : arcmin < 12 ? 2 : 3;

        private static void DrawChevrons(DrawingContext dc, Pen pen, Point c, double rMax, double dx, double dy, double mag) {
            int n = Count(mag);
            for (int i = 0; i < n; i++) {
                double dist = rMax * 0.45 + 24 * i;
                Chevron(dc, pen, new Point(c.X + dx * dist, c.Y + dy * dist), dx, dy);
            }
        }

        private static void Chevron(DrawingContext dc, Pen pen, Point tip, double dx, double dy) {
            // Draw ">" pointing along (dx,dy).
            double s = 12;
            var f = new Vector(dx, dy);
            var n = new Vector(-dy, dx);
            var a = tip - f * s + n * s;
            var b = tip - f * s - n * s;
            var g = new StreamGeometry();
            using (var ctx = g.Open()) {
                ctx.BeginFigure(a, false, false);
                ctx.LineTo(tip, true, true);
                ctx.LineTo(b, true, true);
            }
            g.Freeze();
            dc.DrawGeometry(null, pen, g);
        }

        private static double NiceCeil(double v) {
            double[] steps = { 2, 5, 10, 20, 30, 60, 120, 300, 600, 1800, 3600 };
            foreach (var s in steps) if (v <= s) return s;
            return 3600;
        }

        private static string Fmt(double arcmin) =>
            arcmin >= 60 ? (arcmin / 60.0).ToString("0.#", CultureInfo.InvariantCulture) + "°"
                         : arcmin.ToString("0.#", CultureInfo.InvariantCulture) + "′";

        private void Label(DrawingContext dc, string text, Point p, double size, Brush brush) {
            var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, size, brush,
                                       VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, p);
        }
    }
}
