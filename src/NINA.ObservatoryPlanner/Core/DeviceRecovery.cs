using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Device recovery: while a run is going or paused, the devices that stay connected (safety monitor, switch, dome,
    /// weather) are reconnected when they dropped, at most every <see cref="PlannerOptions.DeviceRecoveryMinutes"/>.
    /// Each drop and return is logged once; a device missing for longer than <see cref="PlannerOptions.DeviceMissingWarnMinutes"/>
    /// is shown as a warning in the panel. Never used while the planner is stopped: then the user is in control.
    /// </summary>
    public sealed class DeviceRecovery {
        private readonly PlannerOptions options;
        private readonly IPlannerLog log;
        private readonly Func<DateTime> now;
        private readonly Dictionary<string, DateTime> missingSince = new();
        private readonly HashSet<string> warned = new();
        private DateTime nextCheck = DateTime.MinValue;

        public DeviceRecovery(PlannerOptions options, IPlannerLog log, Func<DateTime> now) {
            this.options = options;
            this.log = log;
            this.now = now;
        }

        /// <summary>" (reconnecting every 1 min)": for status lines about a missing device.</summary>
        public string RetryNote => options.DeviceRecovery ? $" (reconnecting every {options.DeviceRecoveryMinutes} min)" : " (device recovery is off)";

        /// <summary>
        /// Reconnects the dropped devices when the next try is due, or right away with <paramref name="immediately"/>
        /// (e.g. before closing the roof, which needs the dome).
        /// </summary>
        public async Task Check(Func<CancellationToken, Task<KeptDevicesCheck>> reconnect, CancellationToken token, bool immediately = false) {
            if (!options.DeviceRecovery) { return; }
            if (!immediately && now() < nextCheck) { return; }
            nextCheck = now() + TimeSpan.FromMinutes(options.DeviceRecoveryMinutes);

            KeptDevicesCheck check;
            try {
                check = await reconnect(token);
            } catch (OperationCanceledException) when (token.IsCancellationRequested) {
                throw;
            } catch (Exception ex) {
                log.Info($"Device recovery: reconnecting failed: {ex.Message}");
                return;
            }

            foreach (var d in check.Reconnected) {
                log.Info(missingSince.TryGetValue(d, out var since)
                    ? $"Device recovery: {d} reconnected after {PlannerEngine.Countdown(now() - since)}"
                    : $"Device recovery: {d} had dropped; reconnected");
                Forget(d);
            }
            foreach (var d in check.Missing) {
                if (!missingSince.TryGetValue(d, out var since)) {
                    missingSince[d] = now();
                    log.Info($"Device recovery: {d} is not connected and could not be reconnected; trying again every {options.DeviceRecoveryMinutes} min");
                } else if (!warned.Contains(d) && now() - since >= TimeSpan.FromMinutes(options.DeviceMissingWarnMinutes)) {
                    warned.Add(d);
                    log.Info($"Device recovery: {d} is still not connected after {options.DeviceMissingWarnMinutes} min");
                }
            }

            // connected again by other means (e.g. by hand in NINA)
            foreach (var d in missingSince.Keys.Where(k => !check.Missing.Contains(k) && !check.Reconnected.Contains(k)).ToList()) {
                log.Info($"Device recovery: {d} is connected again");
                Forget(d);
            }
            log.DeviceProblem(Warning);
        }

        /// <summary>The panel warning for devices missing too long, or null.</summary>
        public string Warning => warned.Count == 0 ? null
            : $"Not connected for more than {options.DeviceMissingWarnMinutes} min: {string.Join(", ", warned)}. "
              + $"The planner keeps trying every {options.DeviceRecoveryMinutes} min; check the device, its power and its network.";

        private void Forget(string device) {
            missingSince.Remove(device);
            warned.Remove(device);
        }
    }
}
