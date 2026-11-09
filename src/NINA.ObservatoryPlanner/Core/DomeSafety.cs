using System.Collections.Generic;
using System.Linq;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>The dome shutter as the dome reports it. None: the dome gives no shutter status (or has no shutter).</summary>
    public enum DomeShutter { None, Open, Closed, Opening, Closing, Error }

    /// <summary>
    /// Run with dome: "safe" is "the dome shutter is open". Anything else (closing, closed, error, no status, dome not connected) is
    /// not safe, so imaging stops as soon as the shutter starts to close and the mount can be parked before it is shut.
    /// </summary>
    public static class DomeSafety {

        /// <summary>The safety the engine sees. "Connected" is false for a dome that is not connected or reports no shutter status.</summary>
        public static SafetyState Read(bool domeConnected, DomeShutter shutter) =>
            new(domeConnected && shutter != DomeShutter.None, domeConnected && shutter == DomeShutter.Open);

        /// <summary>The short state shown in the panel chip.</summary>
        public static string StateText(bool domeConnected, DomeShutter shutter) =>
            !domeConnected ? "Not connected" : shutter switch {
                DomeShutter.Open => "Open",
                DomeShutter.Closed => "Closed",
                DomeShutter.Opening => "Opening",
                DomeShutter.Closing => "Closing",
                DomeShutter.Error => "Shutter error",
                _ => "Status unknown"
            };

        /// <summary>The line shown beside the chip.</summary>
        public static string Message(bool domeConnected, DomeShutter shutter) =>
            !domeConnected ? "Dome not connected: not safe, device recovery keeps trying to reconnect." : shutter switch {
                DomeShutter.Open => "Imaging.",
                DomeShutter.Closed => "Waiting for it to open again.",
                DomeShutter.Opening => "Waiting for the dome to be fully open.",
                DomeShutter.Closing => "Dome closing: not safe, the mount is parked and 4 End runs.",
                DomeShutter.Error => "The dome reports a shutter error: not safe.",
                _ => "The dome does not report a shutter status."
            };

        /// <summary>
        /// Problems that stop Run with dome from working, shown when it is chosen and before a run: no dome in NINA's profile, or a
        /// connected dome that reports no shutter status. Empty when all is well.
        /// </summary>
        public static IReadOnlyList<string> StartProblems(bool domeConfigured, bool domeConnected, DomeShutter shutter) {
            var problems = new List<string>();
            if (!domeConfigured) { problems.Add("Run with dome is selected, but no dome is set up in this NINA profile."); }
            else if (domeConnected && shutter == DomeShutter.None) {
                problems.Add("The dome does not report a shutter status, so the planner can't tell if it is open. Choose Run with safety or Manual instead.");
            }
            return problems;
        }

        /// <summary>
        /// The weather close-up: stop guiding, stop tracking, park, and close the dome. The dome is only closed when the planner runs it
        /// (not in Run with dome), it is connected, and the roof may close (not when the mount position is unknown and "close anyway" is off).
        /// </summary>
        public static List<GapStep> CloseUpSteps(bool plannerHandlesDome, bool domeConnected, bool roofMustStayOpen) {
            var steps = new List<GapStep> { GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park };
            if (plannerHandlesDome && domeConnected && !roofMustStayOpen) { steps.Add(GapStep.CloseDome); }
            return steps;
        }

        /// <summary>Opening up after the weather: open the dome (only when the planner runs it and it is connected), then unpark.</summary>
        public static List<GapStep> ReopenSteps(bool plannerHandlesDome, bool domeConnected) {
            var steps = new List<GapStep>();
            if (plannerHandlesDome && domeConnected) { steps.Add(GapStep.OpenDome); }
            steps.Add(GapStep.Unpark);
            return steps;
        }

        /// <summary>The gap steps without the ones that open or close the dome (Run with dome: the observatory runs it).</summary>
        public static IReadOnlyList<GapStep> WithoutDomeSteps(IEnumerable<GapStep> steps) =>
            steps.Where(s => s is not (GapStep.CloseDome or GapStep.OpenDome)).ToList();
    }
}
