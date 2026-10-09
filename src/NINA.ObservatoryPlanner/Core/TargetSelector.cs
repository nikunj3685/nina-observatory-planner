using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.ObservatoryPlanner.Core {

    public enum DecisionKind {
        /// <summary>Image this target now.</summary>
        RunNow,
        /// <summary>No target can run now, but one opens later tonight.</summary>
        WaitUntil,
        /// <summary>Nothing left to image tonight: run 4 End.</summary>
        NothingTonight
    }

    public sealed record Decision(DecisionKind Kind, PlannerTarget Target, DateTime At) {
        public static Decision Nothing(DateTime at) => new(DecisionKind.NothingTonight, null, at);
    }

    /// <summary>
    /// Picks what to image. Checked targets run top to bottom (list order is priority); a target can run while it is
    /// above the horizon, it is dark enough, and its Start at / End at window is open.
    /// </summary>
    public class TargetSelector {
        private readonly Site site;
        private readonly PlannerOptions options;
        private static readonly TimeSpan ScanStep = TimeSpan.FromMinutes(1);

        public TargetSelector(Site site, PlannerOptions options) {
            this.site = site;
            this.options = options;
        }

        /// <summary>Local → UTC. The PC's time zone by default; tests use a fixed offset.</summary>
        public Func<DateTime, DateTime> ToUtc { get; set; } = local => local.ToUniversalTime();

        public double Altitude(PlannerTarget t, DateTime local) => Sky.Altitude(t.RaHours, t.DecDegrees, site, DateTime.SpecifyKind(ToUtc(local), DateTimeKind.Utc));

        public double SunAltitude(DateTime local) => Sky.SunAltitude(site, DateTime.SpecifyKind(ToUtc(local), DateTimeKind.Utc));

        /// <summary>Dark enough to image: the Sun is below the Night setting, or that setting is off.</summary>
        public bool IsDark(DateTime local) => !options.NightLimitEnabled || SunAltitude(local) <= SunLimits.Altitude(options.NightLimit);

        private bool IsSetting(PlannerTarget t, DateTime local) => Altitude(t, local.AddMinutes(5)) < Altitude(t, local);

        public bool IsOpenAt(PlannerTarget t, DateTime local) {
            if (!IsDark(local)) { return false; }
            var alt = Altitude(t, local);
            if (alt <= 0) { return false; }

            if (t.Start.Enabled) {
                if (t.Start.By == ConstraintBy.Time) {
                    if (local < NightTime.At(t.Start.Time, local)) { return false; }
                } else if (alt < t.Start.Altitude) {
                    return false;
                }
            }
            if (t.End.Enabled) {
                if (t.End.By == ConstraintBy.Time) {
                    if (local >= NightTime.At(t.End.Time, local)) { return false; }
                } else if (alt < t.End.Altitude && IsSetting(t, local)) {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Like NINA's Loop Until Time: a frame is only started if the window is still open when it ends.</summary>
        public bool CanStartFrame(PlannerTarget t, DateTime local, TimeSpan duration) => IsOpenAt(t, local) && IsOpenAt(t, local + duration);

        private static bool Eligible(PlannerTarget t, ISet<Guid> skip) => t.Enabled && !t.IsComplete && t.TotalFrames > 0 && (skip == null || !skip.Contains(t.Id));

        public Decision Decide(IEnumerable<PlannerTarget> targets, DateTime now, ISet<Guid> skipTonight = null) {
            var candidates = targets.Where(t => Eligible(t, skipTonight)).ToList();
            foreach (var t in candidates) {
                if (IsOpenAt(t, now)) { return new Decision(DecisionKind.RunNow, t, now); }
            }
            // The earliest moment tonight at which any candidate opens. List order breaks ties.
            var end = NightTime.NightEnd(now);
            for (var at = now + ScanStep; at < end; at += ScanStep) {
                foreach (var t in candidates) {
                    if (IsOpenAt(t, at)) { return new Decision(DecisionKind.WaitUntil, t, at); }
                }
            }
            return Decision.Nothing(now);
        }
    }

    public static class ExposurePlanner {
        /// <summary>
        /// The next exposure row to shoot. Finish each row first: the first row with frames left.
        /// Rotate through filters: the next row with frames left after the one shot last.
        /// </summary>
        public static PlannerExposure Next(PlannerTarget t, PlannerExposure last) {
            var rows = t.Exposures.Where(e => e.Enabled && e.ExposureTime >= 0).ToList();
            if (t.Order == ExposureOrder.FinishEachRowFirst || last == null) {
                return rows.FirstOrDefault(e => e.Remaining > 0);
            }
            var start = rows.IndexOf(last);
            for (int i = 1; i <= rows.Count; i++) {
                var e = rows[(start + i) % rows.Count];
                if (e.Remaining > 0) { return e; }
            }
            return null;
        }
    }

    public enum GapStep { StopGuiding, StopTracking, Park, FindHome, CloseDome, OpenDome, Unpark }

    /// <summary>Between targets: the single place that turns the setting into steps (the panel shows the same list).</summary>
    public static class GapPlan {
        public static (IReadOnlyList<GapStep> Wait, IReadOnlyList<GapStep> Resume) For(GapMountAction mount, bool closeDome) {
            var parked = mount == GapMountAction.StopTrackingAndPark;
            var dome = parked && closeDome;
            var wait = new List<GapStep> { GapStep.StopGuiding };
            var resume = new List<GapStep>();
            if (mount == GapMountAction.StopTrackingAndPark) { wait.Add(GapStep.StopTracking); wait.Add(GapStep.Park); }
            if (mount == GapMountAction.StopTrackingAndFindHome) { wait.Add(GapStep.StopTracking); wait.Add(GapStep.FindHome); }
            if (dome) { wait.Add(GapStep.CloseDome); resume.Add(GapStep.OpenDome); }
            if (parked) { resume.Add(GapStep.Unpark); }
            return (wait, resume);
        }
    }
}
