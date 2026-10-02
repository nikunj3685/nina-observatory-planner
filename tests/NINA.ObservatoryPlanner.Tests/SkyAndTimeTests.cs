using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Linq;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    public class SkyTests {

        [Test]
        public void JulianDate_of_J2000_epoch_is_2451545() {
            Sky.JulianDate(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).Should().BeApproximately(2451545.0, 1e-9);
        }

        [Test]
        public void Transit_altitude_matches_90_minus_latitude_minus_declination() {
            // IC1396: Dec +57.57°. At latitude 45.50° the highest altitude is 90 − |45.50 − 57.57| = 77.93°.
            var start = TestData.Utc(TestData.Night(18));
            var max = Enumerable.Range(0, 14 * 60).Select(m => Sky.Altitude(21.672, 57.567, TestData.Site, start.AddMinutes(m))).Max();
            max.Should().BeApproximately(77.93, 0.05);
        }

        [Test]
        public void Sun_is_at_the_horizon_at_the_published_sunset() {
            // Sunset at 45.50 N, 73.57 W on 2026-10-01 is 18:36 EDT (the SGP screenshot shows the same). Sunset = −0.833° (refraction + radius).
            var alt = Sky.SunAltitude(TestData.Site, TestData.Utc(TestData.Night(18, 36)));
            alt.Should().BeApproximately(-0.833, 0.3);
        }

        [Test]
        public void Sun_is_far_below_the_horizon_at_midnight() {
            Sky.SunAltitude(TestData.Site, TestData.Utc(TestData.Night(0, 0))).Should().BeLessThan(-30);
        }
    }

    [TestFixture]
    public class NightTimeTests {

        [Test]
        public void Evening_time_is_tonight() {
            NightTime.At(new TimeSpan(23, 40, 0), new DateTime(2026, 10, 1, 20, 0, 0)).Should().Be(new DateTime(2026, 10, 1, 23, 40, 0));
        }

        [Test]
        public void Morning_time_is_after_midnight_of_tonight() {
            NightTime.At(new TimeSpan(1, 0, 0), new DateTime(2026, 10, 1, 20, 0, 0)).Should().Be(new DateTime(2026, 10, 2, 1, 0, 0));
        }

        [Test]
        public void After_midnight_the_night_is_still_the_same() {
            NightTime.At(new TimeSpan(4, 0, 0), new DateTime(2026, 10, 2, 0, 30, 0)).Should().Be(new DateTime(2026, 10, 2, 4, 0, 0));
            NightTime.At(new TimeSpan(22, 0, 0), new DateTime(2026, 10, 2, 0, 30, 0)).Should().Be(new DateTime(2026, 10, 1, 22, 0, 0));
        }
    }
}
