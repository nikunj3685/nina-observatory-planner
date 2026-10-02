using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace NINA.ObservatoryPlanner.UI {

    /// <summary>Reads and writes RA/Dec the way astronomers type them: 21h40m19.4s, 21 40 19.4, 21:40:19, or decimal.</summary>
    public static class CoordinateText {
        private static readonly Regex Numbers = new(@"[-+−]?\d+(?:[.,]\d+)?");

        public static string FormatRa(double hours) {
            var total = Math.Round(((hours % 24) + 24) % 24 * 3600, 1);
            var h = (int)(total / 3600); total -= h * 3600;
            var m = (int)(total / 60); total -= m * 60;
            return string.Create(CultureInfo.InvariantCulture, $"{h:00}h{m:00}m{total:00.0}s");
        }

        public static string FormatDec(double degrees) {
            var sign = degrees < 0 ? "-" : "+";
            var total = Math.Round(Math.Abs(degrees) * 3600, 1);
            var d = (int)(total / 3600); total -= d * 3600;
            var m = (int)(total / 60); total -= m * 60;
            return string.Create(CultureInfo.InvariantCulture, $"{sign}{d:00}°{m:00}'{total:00.0}\"");
        }

        private static double[] Parts(string text) {
            var matches = Numbers.Matches(text ?? "");
            var parts = new double[matches.Count];
            for (int i = 0; i < matches.Count; i++) {
                parts[i] = Math.Abs(double.Parse(matches[i].Value.Replace(',', '.').Replace("−", "-"), CultureInfo.InvariantCulture));
            }
            return parts;
        }

        public static bool TryParseRa(string text, out double hours) {
            hours = 0;
            var p = Parts(text);
            if (p.Length == 0) { return false; }
            hours = p[0] + (p.Length > 1 ? p[1] / 60 : 0) + (p.Length > 2 ? p[2] / 3600 : 0);
            return hours >= 0 && hours < 24;
        }

        public static bool TryParseDec(string text, out double degrees) {
            degrees = 0;
            var p = Parts(text);
            if (p.Length == 0) { return false; }
            var negative = (text ?? "").TrimStart().StartsWith("-") || (text ?? "").TrimStart().StartsWith("−");
            degrees = (p[0] + (p.Length > 1 ? p[1] / 60 : 0) + (p.Length > 2 ? p[2] / 3600 : 0)) * (negative ? -1 : 1);
            return Math.Abs(degrees) <= 90;
        }
    }
}
