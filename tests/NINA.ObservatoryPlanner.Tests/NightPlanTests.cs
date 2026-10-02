using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    public class MoonTests {
        private const double D2R = Math.PI / 180;

        private static double Separation(double ra1, double dec1, double ra2, double dec2) =>
            Math.Acos(Math.Sin(dec1 * D2R) * Math.Sin(dec2 * D2R) + Math.Cos(dec1 * D2R) * Math.Cos(dec2 * D2R) * Math.Cos((ra1 - ra2) * D2R)) / D2R;

        [Test]
        public void Moon_covers_the_Sun_at_the_total_eclipse_of_8_April_2024() {
            // Greatest eclipse 18:17 UTC (NASA eclipse catalogue). Geocentric positions agree to well under a degree.
            var utc = new DateTime(2024, 4, 8, 18, 17, 0, DateTimeKind.Utc);
            var m = Sky.Moon(utc);
            var s = Sky.Sun(utc);
            Separation(m.RaDeg, m.DecDeg, s.RaDeg, s.DecDeg).Should().BeLessThan(1.0);
            Sky.MoonIllumination(utc).Fraction.Should().BeLessThan(0.01);
        }

        [Test]
        public void Full_moon_of_23_April_2024_is_fully_lit() {
            // Full Moon 23 April 2024 23:49 UTC (USNO phases of the Moon)
            Sky.MoonIllumination(new DateTime(2024, 4, 23, 23, 49, 0, DateTimeKind.Utc)).Fraction.Should().BeGreaterThan(0.99);
        }

        [Test]
        public void First_quarter_of_15_April_2024_is_half_lit_and_waxing() {
            // First Quarter 15 April 2024 19:13 UTC (USNO phases of the Moon)
            var (fraction, waxing) = Sky.MoonIllumination(new DateTime(2024, 4, 15, 19, 13, 0, DateTimeKind.Utc));
            fraction.Should().BeApproximately(0.5, 0.03);
            waxing.Should().BeTrue();
        }
    }

    [TestFixture]
    public class NightPlanTests {
        private static NightPlan Tonight() => NightPlan.For(TestData.Site, TestData.Night(15), TestData.Utc);

        [Test]
        public void Twilight_events_are_in_order_and_at_their_defining_altitudes() {
            var n = Tonight();
            n.Sunset.Should().NotBeNull();
            n.Dusk.Should().NotBeNull();
            n.Dawn.Should().NotBeNull();
            n.Sunrise.Should().NotBeNull();
            n.Sunset.Value.Should().BeBefore(n.Dusk.Value);
            n.Dusk.Value.Should().BeBefore(n.Dawn.Value);
            n.Dawn.Value.Should().BeBefore(n.Sunrise.Value);
            n.SunAlt(n.Sunset.Value).Should().BeApproximately(NightPlan.SunsetAltitude, 0.02);
            n.SunAlt(n.Dawn.Value).Should().BeApproximately(NightPlan.AstronomicalTwilight, 0.02);
            // At 45.5° N in early October astronomical twilight lasts about an hour and a half.
            (n.Dusk.Value - n.Sunset.Value).TotalMinutes.Should().BeInRange(75, 110);
        }

        [Test]
        public void Chart_runs_from_before_sunset_to_after_sunrise() {
            var n = Tonight();
            n.From.Should().Be(n.Sunset.Value.AddMinutes(-45));
            n.To.Should().Be(n.Sunrise.Value.AddMinutes(45));
        }

        [Test]
        public void Planning_chart_covers_24_hours_and_always_contains_now() {
            foreach (var hour in new[] { 13, 18, 23, 3, 9, 11 }) {
                var now = TestData.Night(hour, 30);
                var n = NightPlan.For(TestData.Site, now, TestData.Utc);
                (n.ChartTo - n.ChartFrom).Should().Be(TimeSpan.FromHours(24));
                n.ChartFrom.Hour.Should().Be(12, "the chart runs from local noon to noon");
                now.Should().BeOnOrAfter(n.ChartFrom).And.BeOnOrBefore(n.ChartTo, $"now ({now:HH:mm}) is on the chart");
            }
        }

        [Test]
        public void Highest_point_over_the_whole_day_finds_a_daytime_transit() {
            var n = Tonight();
            // RA 12h transits around local noon-ish in early October, outside the dark window
            var night = n.Highest(12, 30);
            var day = n.Highest(12, 30, wholeDay: true);
            day.Altitude.Should().BeGreaterThan(night.Altitude);
        }

        [Test]
        public void Time_at_altitude_round_trips() {
            var n = Tonight();
            // M42: rises in the late evening at this site
            const double ra = 5.588, dec = -5.39;
            var rise = n.TimeAtAltitude(ra, dec, 20, rising: true);
            rise.Should().NotBeNull();
            n.Altitude(ra, dec, rise.Value).Should().BeApproximately(20, 0.01);
            n.Altitude(ra, dec, rise.Value.AddMinutes(10)).Should().BeGreaterThan(20);
        }

        [Test]
        public void Window_uses_dusk_and_dawn_without_constraints_and_the_setting_crossing_for_end_by_altitude() {
            var n = Tonight();
            var t = new PlannerTarget { RaHours = 21.0, DecDegrees = 30 };
            n.Window(t).Should().Be((n.Dusk.Value, n.Dawn.Value));

            t.End = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 30 };
            var end = n.Window(t).End;
            n.Altitude(t.RaHours, t.DecDegrees, end).Should().BeApproximately(30, 0.01);
            n.Altitude(t.RaHours, t.DecDegrees, end.AddMinutes(10)).Should().BeLessThan(30, "the end is where the target sets through 30°");

            t.Start = TestData.At(23, 40);
            n.Window(t).Start.Should().Be(TestData.Night(23, 40));
        }
    }
}
