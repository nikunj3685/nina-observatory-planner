using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace NINA.ObservatoryPlanner.UI.Dialogs {

    /// <summary>Planning tools for one target (opened from Target Settings). Clicks on the chart set its start/end time.</summary>
    public partial class PlanningToolsWindow : Window {
        private readonly TargetSettingsVM vm;
        private readonly TextBlock[,] cells = new TextBlock[4, 3];

        public PlanningToolsWindow(TargetSettingsVM vm) {
            InitializeComponent();
            this.vm = vm;
            Owner = Application.Current?.MainWindow is { IsLoaded: true } main ? main : null;
            if (Owner == null) { WindowStartupLocation = WindowStartupLocation.CenterScreen; }
            Title = $"Planning tools · {vm.Name}";
            var n = vm.Night;
            chart.Night = n;
            chart.Target = vm.Preview();
            chart.Picked += (isStart, t) => {
                (isStart ? vm.Start : vm.End).SetTime(t);
                chart.Target = vm.Preview();
                Refresh();
            };
            chart.CursorMoved += Refresh;

            string[] rows = { "Cursor", "Transit", "Start", "End" };
            for (var r = 0; r < 4; r++) {
                var label = new TextBlock { Text = rows[r], FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
                Grid.SetRow(label, r + 1);
                readouts.Children.Add(label);
                for (var c = 0; c < 3; c++) {
                    var text = new TextBlock { FontFamily = new System.Windows.Media.FontFamily("Consolas"), HorizontalAlignment = HorizontalAlignment.Center };
                    var cell = new Border { Child = text, Style = (Style)FindResource("Cell") };
                    Grid.SetRow(cell, r + 1);
                    Grid.SetColumn(cell, c + 1);
                    readouts.Children.Add(cell);
                    cells[r, c] = text;
                }
            }

            static string F(DateTime? t) => t?.ToString("HH:mm") ?? "—";
            sunText.Text = $"Dawn     {F(n.Dawn)}\nSunrise  {F(n.Sunrise)}\nSunset   {F(n.Sunset)}\nDusk     {F(n.Dusk)}";
            moonText.Text = $"Phase {n.MoonPhaseName}\nRise  {F(n.Moonrise)}\nSet   {F(n.Moonset)}\nIllum {n.MoonIllumination * 100:0}%";
            moon.Illumination = n.MoonIllumination;
            moon.Waxing = n.MoonWaxing;
            siteLine.Text = $"Tonight, {n.NightStart:yyyy-MM-dd} · Site {vm.Site.LatitudeDeg:0.00}°, {vm.Site.LongitudeDeg:0.00}° from your NINA profile. " +
                "Dusk and dawn are astronomical (Sun 18° below the horizon). Green band: when this target will be imaged.";
            Refresh();
        }

        private void Refresh() {
            var t = chart.Target;
            var n = vm.Night;
            var (start, end) = n.Window(t);
            var top = n.Highest(t.RaHours, t.DecDegrees, wholeDay: true);
            Row(0, chart.Cursor_);
            Row(1, top.At, top.Altitude);
            Row(2, t.Start.Enabled ? start : null);
            Row(3, t.End.Enabled ? end : null);

            void Row(int r, DateTime? at, double? alt = null) {
                cells[r, 0].Text = at?.ToString("HH:mm") ?? "NA";
                cells[r, 1].Text = at?.ToString("yyyy-MM-dd") ?? "NA";
                var a = alt ?? (at is DateTime x ? n.Altitude(t.RaHours, t.DecDegrees, x) : (double?)null);
                cells[r, 2].Text = a is double v ? $"{v:0}°" : "NA";
            }
        }

        private void Option_Click(object sender, RoutedEventArgs e) {
            chart.HideDay = hideDay.IsChecked == true;
            chart.HideNow = hideNow.IsChecked == true;
            chart.HideBlock = hideBlock.IsChecked == true;
            chart.InvalidateVisual();
        }

        private void Horizon_Changed(object sender, TextChangedEventArgs e) {
            if (chart == null) { return; }
            if (double.TryParse(horizon.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var h)) {
                chart.HorizonAltitude = Math.Max(0, Math.Min(90, h));
                chart.InvalidateVisual();
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Close();
    }
}
