using System;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Tonight at the site, in local time: twilight, the Moon, and where a target is. Used by the Planning tools chart
    /// and the Target Settings ("ends at 30° altitude" → the clock time it happens tonight, and back).
    /// Rise/set use the standard altitudes: Sun −0.833°, Moon +0.125° (parallax and refraction), astronomical twilight −18°.
    /// </summary>
    public sealed class NightPlan {
        public const double SunsetAltitude = -0.833;
        public const double AstronomicalTwilight = -18;
        public const double MoonRiseAltitude = 0.125;

        private readonly Site site;
        private readonly Func<DateTime, DateTime> toUtc;

        private NightPlan(Site site, Func<DateTime, DateTime> toUtc, DateTime localNow) {
            this.site = site;
            this.toUtc = toUtc;
            Now = localNow;
            NightStart = NightTime.NightStart(localNow);
            var end = NightStart.AddDays(1);
            Sunset = Cross(SunAlt, SunsetAltitude, NightStart, end, rising: false, TimeSpan.FromMinutes(2));
            Dusk = Cross(SunAlt, AstronomicalTwilight, NightStart, end, rising: false, TimeSpan.FromMinutes(2));
            Dawn = Cross(SunAlt, AstronomicalTwilight, NightStart, end, rising: true, TimeSpan.FromMinutes(2));
            Sunrise = Cross(SunAlt, SunsetAltitude, NightStart, end, rising: true, TimeSpan.FromMinutes(2));
            Moonrise = Cross(MoonAlt, MoonRiseAltitude, NightStart, end, rising: true, TimeSpan.FromMinutes(2));
            Moonset = Cross(MoonAlt, MoonRiseAltitude, NightStart, end, rising: false, TimeSpan.FromMinutes(2));
            From = (Sunset ?? NightStart.AddHours(6)).AddMinutes(-45);
            To = (Sunrise ?? NightStart.AddHours(19)).AddMinutes(45);
            var middle = From + TimeSpan.FromTicks((To - From).Ticks / 2);
            (MoonIllumination, MoonWaxing) = Sky.MoonIllumination(Utc(middle));
        }

        /// <summary>Tonight (local noon to noon) for this site. <paramref name="toUtc"/> converts local times.</summary>
        public static NightPlan For(Site site, DateTime localNow, Func<DateTime, DateTime> toUtc) => new(site, toUtc, localNow);

        public DateTime Now { get; }
        public DateTime NightStart { get; }
        /// <summary>The dark part of the night: 45 minutes before sunset to 45 minutes after sunrise ("tonight" times).</summary>
        public DateTime From { get; }
        public DateTime To { get; }
        /// <summary>The Planning tools chart: the whole 24 hours from local noon, so "now" is always on it.</summary>
        public DateTime ChartFrom => NightStart;
        public DateTime ChartTo => NightStart.AddDays(1);
        public DateTime? Sunset { get; }
        public DateTime? Dusk { get; }
        public DateTime? Dawn { get; }
        public DateTime? Sunrise { get; }
        public DateTime? Moonrise { get; }
        public DateTime? Moonset { get; }
        public double MoonIllumination { get; }
        public bool MoonWaxing { get; }

        public string MoonPhaseName {
            get {
                var k = MoonIllumination;
                if (k < 0.03) { return "New Moon"; }
                if (k > 0.97) { return "Full Moon"; }
                if (k > 0.45 && k < 0.55) { return MoonWaxing ? "First Quarter" : "Last Quarter"; }
                return (MoonWaxing ? "Waxing " : "Waning ") + (k < 0.5 ? "Crescent" : "Gibbous");
            }
        }

        private DateTime Utc(DateTime local) => DateTime.SpecifyKind(toUtc(local), DateTimeKind.Utc);
        public double SunAlt(DateTime local) => Sky.SunAltitude(site, Utc(local));
        public double MoonAlt(DateTime local) => Sky.MoonAltitude(site, Utc(local));
        public double Altitude(double raHours, double decDeg, DateTime local) => Sky.Altitude(raHours, decDeg, site, Utc(local));

        /// <summary>When the target passes <paramref name="altitude"/> tonight (rising or setting), or null if it does not.</summary>
        public DateTime? TimeAtAltitude(double raHours, double decDeg, double altitude, bool rising) =>
            Cross(t => Altitude(raHours, decDeg, t), altitude, From, To, rising, TimeSpan.FromMinutes(1));

        /// <summary>The highest point of the target between <see cref="From"/> and <see cref="To"/>, or over the whole chart.</summary>
        public (DateTime At, double Altitude) Highest(double raHours, double decDeg, bool wholeDay = false) {
            DateTime from = wholeDay ? ChartFrom : From, to = wholeDay ? ChartTo : To;
            var best = (At: from, Altitude: double.MinValue);
            for (var t = from; t <= to; t = t.AddMinutes(2)) {
                var a = Altitude(raHours, decDeg, t);
                if (a > best.Altitude) { best = (t, a); }
            }
            return best;
        }

        /// <summary>
        /// When the target will be imaged tonight: from its Start at (or astronomical dusk) to its End at (or dawn).
        /// Altitude limits use the rising crossing for the start and the setting crossing for the end.
        /// </summary>
        public (DateTime Start, DateTime End) Window(PlannerTarget t) {
            var start = !t.Start.Enabled ? Dusk ?? From
                : t.Start.By == ConstraintBy.Time ? NightTime.At(t.Start.Time, Now)
                : TimeAtAltitude(t.RaHours, t.DecDegrees, t.Start.Altitude, rising: true) ?? From;
            var end = !t.End.Enabled ? Dawn ?? To
                : t.End.By == ConstraintBy.Time ? NightTime.At(t.End.Time, Now)
                : TimeAtAltitude(t.RaHours, t.DecDegrees, t.End.Altitude, rising: false) ?? To;
            return (start, end);
        }

        /// <summary>First crossing of <paramref name="level"/> in [from, to], refined to the second by bisection.</summary>
        private static DateTime? Cross(Func<DateTime, double> f, double level, DateTime from, DateTime to, bool rising, TimeSpan step) {
            var prev = f(from);
            for (var t = from + step; t <= to; t += step) {
                var cur = f(t);
                if (rising ? prev < level && cur >= level : prev >= level && cur < level) {
                    DateTime lo = t - step, hi = t;
                    while (hi - lo > TimeSpan.FromSeconds(1)) {
                        var mid = lo + TimeSpan.FromTicks((hi - lo).Ticks / 2);
                        var above = f(mid) >= level;
                        if (above == rising) { hi = mid; } else { lo = mid; }
                    }
                    return hi;
                }
                prev = cur;
            }
            return null;
        }
    }
}
