using Newtonsoft.Json;
using System;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>Run state that must survive closing NINA: whether the sequence is paused, and where.</summary>
    [JsonObject(MemberSerialization.OptIn)]
    public class PlannerState {
        [JsonProperty] public bool Paused { get; set; }
        [JsonProperty] public Guid? TargetId { get; set; }
        [JsonProperty] public string TargetName { get; set; }
        [JsonProperty] public double? MountRaHours { get; set; }
        [JsonProperty] public double? MountDecDeg { get; set; }
        [JsonProperty] public DateTime? PausedAt { get; set; }
        /// <summary>Paused during 1 Begin after this many steps, or null.</summary>
        [JsonProperty] public int? BeginDone { get; set; }
        /// <summary>2 Start of target had run to its end. Missing in files from 1.0: then it runs again on resume.</summary>
        [JsonProperty] public bool? ImagingStarted { get; set; }

        public PausePoint Point => Paused ? new PausePoint(TargetId, TargetName, MountRaHours, MountDecDeg, PausedAt ?? DateTime.Now, BeginDone, ImagingStarted ?? false) : null;

        public static PlannerState From(PausePoint p) => p == null ? new PlannerState() : new PlannerState {
            Paused = true, TargetId = p.TargetId, TargetName = p.TargetName, MountRaHours = p.MountRaHours, MountDecDeg = p.MountDecDeg, PausedAt = p.At,
            BeginDone = p.BeginDone, ImagingStarted = p.ImagingStarted
        };
    }

    /// <summary>
    /// While paused the mount keeps tracking. Near the meridian that can drive it into the pier, so the planner warns
    /// shortly before the flip time and stops tracking once it is reached (the next resume slews again).
    /// </summary>
    public static class MeridianGuard {
        public static readonly TimeSpan WarnBefore = TimeSpan.FromMinutes(15);

        public enum Action { None, Warn, StopTracking }

        /// <param name="tracking">Whether the mount is tracking.</param>
        /// <param name="hoursToFlip">NINA's time to the meridian flip in hours (negative once it has passed), or null if unknown.</param>
        public static Action Check(bool tracking, double? hoursToFlip) {
            if (!tracking || hoursToFlip == null || double.IsNaN(hoursToFlip.Value)) { return Action.None; }
            if (hoursToFlip.Value <= 0) { return Action.StopTracking; }
            return hoursToFlip.Value <= WarnBefore.TotalHours ? Action.Warn : Action.None;
        }
    }
}
