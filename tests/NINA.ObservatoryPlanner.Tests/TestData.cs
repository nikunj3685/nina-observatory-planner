using NINA.ObservatoryPlanner.Core;
using System;
using System.Collections.ObjectModel;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>A fixed site and night so the tests don't depend on this PC's location, clock or time zone.</summary>
    internal static class TestData {
        // 45.50° N, 73.57° W (the site the mockup used), Eastern Daylight Time (UTC−4) on 1 October 2026
        public static readonly Site Site = new(45.50, -73.57);
        public static DateTime Utc(DateTime local) => local.AddHours(4);
        public static DateTime Night(int hour, int minute = 0) {
            var day = hour < 12 ? new DateTime(2026, 10, 2) : new DateTime(2026, 10, 1);
            return day.AddHours(hour).AddMinutes(minute);
        }

        public static TargetSelector Selector(PlannerOptions options = null) =>
            new(Site, options ?? new PlannerOptions()) { ToUtc = Utc };

        /// <summary>A target near the pole: above 35° all night at this site, so only its time window matters.</summary>
        public static PlannerTarget Circumpolar(string name, int frames = 10, double exposure = 300) => new() {
            Name = name, RaHours = 2, DecDegrees = 80,
            Exposures = new ObservableCollection<PlannerExposure> { new PlannerExposure { Filter = "Ha", ExposureTime = exposure, Count = frames } }
        };

        public static TimeConstraint At(int hour, int minute = 0) => new() { Enabled = true, By = ConstraintBy.Time, Time = new TimeSpan(hour, minute, 0) };
    }
}
