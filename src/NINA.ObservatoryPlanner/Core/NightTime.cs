using System;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Clock times in target settings mean "tonight": a night runs from local noon to the next local noon,
    /// so 23:40 is this evening and 01:00 is after midnight.
    /// </summary>
    public static class NightTime {

        public static DateTime NightStart(DateTime localNow) =>
            localNow.Hour >= 12 ? localNow.Date.AddHours(12) : localNow.Date.AddDays(-1).AddHours(12);

        public static DateTime NightEnd(DateTime localNow) => NightStart(localNow).AddDays(1);

        /// <summary>The moment of <paramref name="timeOfDay"/> in the night that contains <paramref name="localNow"/>.</summary>
        public static DateTime At(TimeSpan timeOfDay, DateTime localNow) {
            var start = NightStart(localNow);
            var result = start.Date + timeOfDay;
            return timeOfDay < TimeSpan.FromHours(12) ? result.AddDays(1) : result;
        }
    }
}
