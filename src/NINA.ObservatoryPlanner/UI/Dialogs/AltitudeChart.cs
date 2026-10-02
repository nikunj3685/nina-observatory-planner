using NINA.ObservatoryPlanner.Core;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    /// <summary>
    /// Tonight's altitude of a target, as in the mockup's Planning tools: twilight shading from the Sun's real altitude,
    /// the horizon limit, the target curve with its highest point, Start/End lines, the capturing block and "now".
    /// Left click sets the start time, right click the end time.
    /// </summary>
    public sealed class AltitudeChart : FrameworkElement {
        private const double PadLeft = 38, PadRight = 10, PadTop = 12, PadBottom = 26;
        private static readonly Typeface Mono = new("Consolas");
        private static readonly Brush Grid = Frozen(Color.FromArgb(0x73, 0xFF, 0xFF, 0xFF));
        private static readonly Brush Label = Frozen(Color.FromRgb(0xC9, 0xCE, 0xD4));
        private static readonly Brush Curve = Frozen(Color.FromRgb(0xFF, 0x8C, 0x1A));
        private static readonly Brush Horizon = Frozen(Color.FromRgb(0xF2, 0xE3, 0x00));
        private static readonly Brush Block = Frozen(Color.FromArgb(0x38, 0x57, 0xB9, 0x7C));
        private static readonly Brush NowBrush = Frozen(Color.FromRgb(0xFF, 0x3B, 0x30));

        public NightPlan Night { get; set; }
        public PlannerTarget Target { get; set; }
        public bool HideDay { get; set; }
        public bool HideNow { get; set; }
        public bool HideBlock { get; set; }
        public double HorizonAltitude { get; set; } = 5;
        public DateTime? Cursor_ { get; private set; }

        /// <summary>(isStart, local time) when the user clicks: left = start, right = end.</summary>
        public event Action<bool, DateTime> Picked;
        public event Action CursorMoved;

        public AltitudeChart() {
            ClipToBounds = true;
            Cursor = Cursors.Cross;
        }

        private double X(DateTime t) => PadLeft + (t - Night.ChartFrom).TotalMinutes / (Night.ChartTo - Night.ChartFrom).TotalMinutes * (ActualWidth - PadLeft - PadRight);
        private DateTime T(double x) => Night.ChartFrom + TimeSpan.FromMinutes((x - PadLeft) / (ActualWidth - PadLeft - PadRight) * (Night.ChartTo - Night.ChartFrom).TotalMinutes);
        private double Y(double alt) => PadTop + (1 - alt / 90.0) * (ActualHeight - PadTop - PadBottom);

        private DateTime? TimeAt(Point p) => p.X < PadLeft || p.X > ActualWidth - PadRight ? null : T(p.X);

        protected override void OnMouseMove(MouseEventArgs e) {
            Cursor_ = Night == null ? null : TimeAt(e.GetPosition(this));
            CursorMoved?.Invoke();
            InvalidateVisual();
        }

        protected override void OnMouseLeave(MouseEventArgs e) {
            Cursor_ = null;
            CursorMoved?.Invoke();
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) {
            if (Night != null && TimeAt(e.GetPosition(this)) is DateTime t) { Picked?.Invoke(true, t); InvalidateVisual(); }
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e) {
            if (Night != null && TimeAt(e.GetPosition(this)) is DateTime t) { Picked?.Invoke(false, t); InvalidateVisual(); }
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) {
            var w = ActualWidth;
            var h = ActualHeight;
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            if (Night == null || w <= PadLeft + PadRight || h <= PadTop + PadBottom) { return; }
            double x0 = PadLeft, x1 = w - PadRight, yTop = Y(90), yBottom = Y(0);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            // twilight shading from the Sun's altitude: full daylight blue at 0°, black below −18°
            if (!HideDay) {
                for (var x = x0; x < x1; x += 2) {
                    var s = Night.SunAlt(T(x));
                    var k = s >= 0 ? 1 : s <= -18 ? 0 : Math.Pow((s + 18) / 18, 1.8);
                    if (k <= 0) { continue; }
                    var c = Color.FromRgb((byte)(150 * k), (byte)(205 * k), (byte)(232 * k));
                    dc.DrawRectangle(new SolidColorBrush(c), null, new Rect(x, yTop, 2.5, yBottom - yTop));
                }
            }

            var t = Target;
            // capturing block
            if (!HideBlock && t != null) {
                var (s, e) = Night.Window(t);
                if (e > s) { dc.DrawRectangle(Block, null, new Rect(new Point(Math.Max(x0, X(s)), yTop), new Point(Math.Min(x1, X(e)), yBottom))); }
            }

            // grid: hours and altitude
            var gridPen = new Pen(Grid, 1) { DashStyle = new DashStyle(new double[] { 2, 3 }, 0) };
            var firstHour = Night.ChartFrom;
            var step = w < 700 ? 4 : 2;
            var i = 0;
            for (var hour = firstHour; hour <= Night.ChartTo; hour = hour.AddHours(1), i++) {
                var x = X(hour);
                dc.DrawLine(gridPen, new Point(x, yTop), new Point(x, yBottom));
                if (i % step == 0) { Text(dc, hour.ToString("HH:mm"), x, yBottom + 5, Label, 11, TextAlignment.Center, dpi); }
            }
            for (var a = 0; a <= 80; a += 20) {
                var y = Y(a);
                dc.DrawLine(gridPen, new Point(x0, y), new Point(x1, y));
                Text(dc, a.ToString(CultureInfo.InvariantCulture), x0 - 6, y - 7, Label, 11, TextAlignment.Right, dpi);
            }

            // horizon limit
            dc.DrawLine(new Pen(Horizon, 3), new Point(x0, Y(HorizonAltitude)), new Point(x1, Y(HorizonAltitude)));

            if (t == null) { return; }

            // altitude curve
            dc.PushClip(new RectangleGeometry(new Rect(x0, yTop, x1 - x0, yBottom - yTop)));
            var geometry = new StreamGeometry();
            using (var g = geometry.Open()) {
                for (var x = x0; x <= x1; x += 2) {
                    var p = new Point(x, Y(Night.Altitude(t.RaHours, t.DecDegrees, T(x))));
                    if (x == x0) { g.BeginFigure(p, false, false); } else { g.LineTo(p, true, false); }
                }
            }
            geometry.Freeze();
            dc.DrawGeometry(null, new Pen(Curve, 2.5), geometry);
            dc.Pop();

            // highest point
            var top = Night.Highest(t.RaHours, t.DecDegrees, wholeDay: true);
            if (top.Altitude > 0) {
                double px = X(top.At), py = Y(top.Altitude);
                var cross = new Pen(NowBrush, 2.5);
                dc.DrawLine(cross, new Point(px - 5, py - 5), new Point(px + 5, py + 5));
                dc.DrawLine(cross, new Point(px + 5, py - 5), new Point(px - 5, py + 5));
                Text(dc, $"{top.Altitude:0}°", px, py + 8, Brushes.White, 12, TextAlignment.Center, dpi);
            }

            // start / end
            var (start, end) = Night.Window(t);
            if (t.Start.Enabled) { Mark(dc, start, PlannerBrushes.Safe, "START", yTop, yBottom, dpi); }
            if (t.End.Enabled) { Mark(dc, end, PlannerBrushes.Unsafe, "END", yTop, yBottom, dpi); }

            // now
            if (!HideNow && Night.Now >= Night.ChartFrom && Night.Now <= Night.ChartTo) {
                var pen = new Pen(NowBrush, 1.5) { DashStyle = new DashStyle(new double[] { 4, 3 }, 0) };
                dc.DrawLine(pen, new Point(X(Night.Now), yTop), new Point(X(Night.Now), yBottom));
            }

            // cursor
            if (Cursor_ is DateTime c2) {
                var px = X(c2);
                dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)), 1), new Point(px, yTop), new Point(px, yBottom));
                dc.DrawEllipse(Brushes.White, null, new Point(px, Y(Night.Altitude(t.RaHours, t.DecDegrees, c2))), 4, 4);
            }
        }

        private void Mark(DrawingContext dc, DateTime at, Brush brush, string label, double yTop, double yBottom, double dpi) {
            if (at < Night.ChartFrom || at > Night.ChartTo) { return; }
            var x = X(at);
            dc.DrawLine(new Pen(brush, 2), new Point(x, yTop), new Point(x, yBottom));
            Text(dc, label, x, yTop + 2, brush, 12, TextAlignment.Center, dpi, bold: true);
        }

        private static void Text(DrawingContext dc, string text, double x, double y, Brush brush, double size, TextAlignment align, double dpi, bool bold = false) {
            var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                bold ? new Typeface(Mono.FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal) : Mono, size, brush, dpi) { TextAlignment = align };
            dc.DrawText(ft, new Point(x, y));
        }

        private static Brush Frozen(Color c) {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }

    /// <summary>The Moon's phase as a disc (lit part on the right while waxing).</summary>
    public sealed class MoonDisc : FrameworkElement {
        public double Illumination { get; set; }
        public bool Waxing { get; set; }

        protected override void OnRender(DrawingContext dc) {
            var r = Math.Min(ActualWidth, ActualHeight) / 2 - 1;
            if (r <= 0) { return; }
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            var dark = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x35));
            var lit = new SolidColorBrush(Color.FromRgb(0xE9, 0xE6, 0xDC));
            dc.DrawEllipse(dark, null, c, r, r);
            // lit half
            var half = new StreamGeometry();
            using (var g = half.Open()) {
                g.BeginFigure(new Point(c.X, c.Y - r), true, true);
                g.ArcTo(new Point(c.X, c.Y + r), new Size(r, r), 0, false, Waxing ? SweepDirection.Clockwise : SweepDirection.Counterclockwise, true, false);
            }
            dc.DrawGeometry(lit, null, half);
            // terminator: an ellipse that darkens a crescent or lights a gibbous
            var k = Illumination;
            dc.DrawEllipse(k < 0.5 ? dark : lit, null, c, Math.Max(0.1, r * Math.Abs(1 - 2 * k)), r);
        }
    }
}
