using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Runs the close-up and between-target steps with one safety rule: the dome is never closed after the park failed, because the
    /// mount may then not be clear of the roof. The park stays "failed" until a park works (or the mount reports it is parked).
    /// </summary>
    public static class ShutdownSteps {

        /// <summary>The text for the panel and the log when the dome was left open because the park failed.</summary>
        public const string RoofLeftOpen = "the dome/roof was NOT closed, because the mount could not be parked (it may not be clear of the roof). Park the mount and close the dome yourself.";

        /// <summary>
        /// Runs <paramref name="steps"/> in order. A CloseDome step is not run (and not counted as done) while the park has failed.
        /// Returns false when a step in <paramref name="mustWork"/> failed or the dome was not closed.
        /// </summary>
        /// <param name="run">Runs one step; true when it worked.</param>
        /// <param name="parkFailed">True while the last park failed and the mount is not known to be parked.</param>
        /// <param name="setParkFailed">Called after each Park step: true when it failed, false when it worked.</param>
        /// <param name="roofLeftOpen">Called when the dome is left open because of the failed park (to log and tell the panel).</param>
        public static async Task<bool> Run(string what, IEnumerable<GapStep> steps, ISet<GapStep> mustWork, Func<GapStep, Task<bool>> run,
                                           Func<bool> parkFailed, Action<bool> setParkFailed, Action<string> log, Action roofLeftOpen) {
            var ok = true;
            foreach (var step in steps) {
                if (step == GapStep.CloseDome && parkFailed()) {
                    log($"{what}: {step} not run, the mount is not parked");
                    roofLeftOpen();
                    ok = false;
                    continue;
                }
                log($"{what}: {step}");
                var worked = await run(step);
                if (step == GapStep.Park) { setParkFailed(!worked); }
                if (!worked && mustWork.Contains(step)) {
                    log($"{what}: {step} failed");
                    ok = false;
                }
            }
            return ok;
        }
    }

    /// <summary>The rule for the weather while the run is paused: the same fail-closed reading as during the run.</summary>
    public static class PausedSafety {
        /// <summary>True when 4 End must run: a safety source that is not connected counts as not safe (a lost connection is not "all clear").</summary>
        public static bool MustShutDown(bool usesSafety, SafetyState safety) => usesSafety && !(safety.Connected && safety.IsSafe);
    }
}
