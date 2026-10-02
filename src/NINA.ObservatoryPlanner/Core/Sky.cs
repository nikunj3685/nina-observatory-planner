using System;

namespace NINA.ObservatoryPlanner.Core {

    public readonly record struct Site(double LatitudeDeg, double LongitudeDeg);

    /// <summary>
    /// Low-precision positions, good to well under a degree, which is enough for scheduling
    /// (altitude limits and darkness). Sun: Astronomical Almanac low-precision formula (~0.01°).
    /// Sidereal time: IAU 1982 GMST expression. Both are standard references.
    /// </summary>
    public static class Sky {
        private const double D2R = Math.PI / 180.0;

        public static double JulianDate(DateTime utc) {
            if (utc.Kind == DateTimeKind.Local) { utc = utc.ToUniversalTime(); }
            return utc.Ticks / (double)TimeSpan.TicksPerDay + 1721425.5;
        }

        private static double Normalize(double deg) => ((deg % 360) + 360) % 360;

        public static double GmstDegrees(DateTime utc) => Normalize(280.46061837 + 360.98564736629 * (JulianDate(utc) - 2451545.0));

        /// <summary>Geometric altitude in degrees of an object at J2000 RA/Dec (precession ignored, a few arcmin today).</summary>
        public static double Altitude(double raHours, double decDeg, Site site, DateTime utc) {
            var ha = (GmstDegrees(utc) + site.LongitudeDeg - raHours * 15.0) * D2R;
            var lat = site.LatitudeDeg * D2R;
            var dec = decDeg * D2R;
            return Math.Asin(Math.Sin(lat) * Math.Sin(dec) + Math.Cos(lat) * Math.Cos(dec) * Math.Cos(ha)) / D2R;
        }

        public static (double RaDeg, double DecDeg) Sun(DateTime utc) {
            var d = JulianDate(utc) - 2451545.0;
            var g = Normalize(357.529 + 0.98560028 * d) * D2R;
            var q = Normalize(280.459 + 0.98564736 * d);
            var l = (q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * D2R;
            var e = (23.439 - 0.00000036 * d) * D2R;
            var ra = Normalize(Math.Atan2(Math.Cos(e) * Math.Sin(l), Math.Cos(l)) / D2R);
            var dec = Math.Asin(Math.Sin(e) * Math.Sin(l)) / D2R;
            return (ra, dec);
        }

        public static double SunAltitude(Site site, DateTime utc) {
            var (ra, dec) = Sun(utc);
            return Altitude(ra / 15.0, dec, site, utc);
        }

        /// <summary>Sun's apparent ecliptic longitude in degrees (same low-precision formula as <see cref="Sun"/>).</summary>
        public static double SunLongitude(DateTime utc) {
            var d = JulianDate(utc) - 2451545.0;
            var g = Normalize(357.529 + 0.98560028 * d) * D2R;
            return Normalize(280.459 + 0.98564736 * d + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g));
        }

        /// <summary>
        /// Geocentric Moon: Astronomical Almanac low-precision formula (about 0.3° in longitude, 0.2° in latitude).
        /// Returns ecliptic longitude/latitude and equatorial RA/Dec in degrees.
        /// </summary>
        public static (double LonDeg, double LatDeg, double RaDeg, double DecDeg) Moon(DateTime utc) {
            var t = (JulianDate(utc) - 2451545.0) / 36525.0;
            double S(double a, double b) => Math.Sin(Normalize(a + b * t) * D2R);
            var lon = Normalize(218.32 + 481267.881 * t
                + 6.29 * S(135.0, 477198.87) - 1.27 * S(259.3, -413335.36) + 0.66 * S(235.7, 890534.22)
                + 0.21 * S(269.9, 954397.74) - 0.19 * S(357.5, 35999.05) - 0.11 * S(186.5, 966404.03));
            var lat = 5.13 * S(93.3, 483202.02) + 0.28 * S(228.2, 960400.89) - 0.28 * S(318.3, 6003.15) - 0.17 * S(217.6, -407332.21);
            var e = (23.439 - 0.0130042 * t) * D2R;
            double l = lon * D2R, b = lat * D2R;
            var ra = Normalize(Math.Atan2(Math.Sin(l) * Math.Cos(e) - Math.Tan(b) * Math.Sin(e), Math.Cos(l)) / D2R);
            var dec = Math.Asin(Math.Sin(b) * Math.Cos(e) + Math.Cos(b) * Math.Sin(e) * Math.Sin(l)) / D2R;
            return (lon, lat, ra, dec);
        }

        public static double MoonAltitude(Site site, DateTime utc) {
            var m = Moon(utc);
            return Altitude(m.RaDeg / 15.0, m.DecDeg, site, utc);
        }

        /// <summary>Illuminated fraction (0..1) and whether the Moon is waxing, from its elongation from the Sun.</summary>
        public static (double Fraction, bool Waxing) MoonIllumination(DateTime utc) {
            var m = Moon(utc);
            var dl = Normalize(m.LonDeg - SunLongitude(utc));
            var cosPsi = Math.Cos(m.LatDeg * D2R) * Math.Cos(dl * D2R);
            return ((1 - cosPsi) / 2, dl < 180);
        }
    }
}
