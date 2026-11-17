using NINA.Core.Utility;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

    /// <summary>What the planner uses of GS Server's AutoHome (a seam, so that stopping it can be tested without GS Server).</summary>
    internal interface IAutoHomeApi {
        bool CanHomeSensors { get; }
        bool IsAutoHomeRunning { get; }
        string LastAutoHomeError { get; }
        void AutoHomeStart(int degreeLimit, int direction);
        void AutoHomeStop();
    }

    /// <summary>
    /// GS Server's AutoHome (the home-sensor search) through its own API, "GS.SkyApi", which GS.Server.exe serves. NINA's
    /// "Find Home" can't do this: through ASCOM, GS Server only slews to where it believes home is.
    /// </summary>
    internal static class GssAutoHome {
        /// <summary>The ASCOM id of GS Server's mount driver.</summary>
        public const string TelescopeId = "ASCOM.GS.Sky.Telescope";
        private static readonly Guid SkyApi = new("9D65CC8C-4E34-4FCF-9703-8632A202363E");
        /// <summary>How far each axis may move while looking for its sensor (GS Server allows 20–179°; its default is 100°).</summary>
        public const int DegreeLimit = 150;
        public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

        private sealed class ComApi : IAutoHomeApi {
            private readonly dynamic gs;
            public ComApi(object gs) { this.gs = gs; }
            public bool CanHomeSensors => (bool)gs.CanHomeSensors;
            public bool IsAutoHomeRunning => (bool)gs.IsAutoHomeRunning;
            public string LastAutoHomeError => gs.LastAutoHomeError;
            public void AutoHomeStart(int degreeLimit, int direction) => gs.AutoHomeStart(degreeLimit, direction);
            public void AutoHomeStop() => gs.AutoHomeStop();
        }

        /// <summary>Runs AutoHome and waits for it. Returns null when it worked, otherwise why it failed.</summary>
        public static async Task<string> Run(CancellationToken token) {
            object gs = null;
            try {
                var type = Type.GetTypeFromCLSID(SkyApi);
                if (type == null) { return "GS Server's API is not installed"; }
                gs = Activator.CreateInstance(type);
                return await Run(new ComApi(gs), (d, t) => Task.Delay(d, t), Timeout, () => DateTime.UtcNow, token);
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Error(ex);
                return ex.InnerException?.Message ?? ex.Message;
            } finally {
                if (gs != null) { try { Marshal.ReleaseComObject(gs); } catch (Exception) { } }
            }
        }

        /// <summary>
        /// The AutoHome run. Whatever ends it before it is over (a cancellation during either wait, the time limit, or an error) stops
        /// the homing first: the mount must not keep moving by itself after the planner has given up on it.
        /// </summary>
        internal static async Task<string> Run(IAutoHomeApi gs, Func<TimeSpan, CancellationToken, Task> delay, TimeSpan timeout, Func<DateTime> utcNow, CancellationToken token) {
            if (!gs.CanHomeSensors) { return "the mount reports no home sensors"; }
            var started = false;
            var over = false;
            try {
                started = true; // also when the start call itself fails half way: stopping a homing that did not start is harmless
                gs.AutoHomeStart(DegreeLimit, 0);
                var until = utcNow() + timeout;
                await delay(TimeSpan.FromSeconds(2), token);
                while (gs.IsAutoHomeRunning) {
                    if (utcNow() > until) { return $"it did not finish within {timeout.TotalMinutes:0} minutes"; }
                    token.ThrowIfCancellationRequested();
                    await delay(TimeSpan.FromSeconds(1), token);
                }
                over = true;
                string error = gs.LastAutoHomeError;
                return string.IsNullOrWhiteSpace(error) ? null : error;
            } finally {
                if (started && !over) {
                    try { gs.AutoHomeStop(); } catch (Exception ex) { Logger.Error(ex); }
                }
            }
        }
    }
}
