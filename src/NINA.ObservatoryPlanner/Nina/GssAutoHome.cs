using NINA.Core.Utility;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Nina {

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

        /// <summary>Runs AutoHome and waits for it. Returns null when it worked, otherwise why it failed.</summary>
        public static async Task<string> Run(CancellationToken token) {
            dynamic gs = null;
            try {
                var type = Type.GetTypeFromCLSID(SkyApi);
                if (type == null) { return "GS Server's API is not installed"; }
                gs = Activator.CreateInstance(type);
                if (!(bool)gs.CanHomeSensors) { return "the mount reports no home sensors"; }
                gs.AutoHomeStart(DegreeLimit, 0);
                var until = DateTime.UtcNow + Timeout;
                await Task.Delay(TimeSpan.FromSeconds(2), token);
                while ((bool)gs.IsAutoHomeRunning) {
                    if (DateTime.UtcNow > until) {
                        gs.AutoHomeStop();
                        return $"it did not finish within {Timeout.TotalMinutes:0} minutes";
                    }
                    if (token.IsCancellationRequested) { gs.AutoHomeStop(); token.ThrowIfCancellationRequested(); }
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                string error = gs.LastAutoHomeError;
                return string.IsNullOrWhiteSpace(error) ? null : error;
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                Logger.Error(ex);
                return ex.InnerException?.Message ?? ex.Message;
            } finally {
                if (gs != null) { try { Marshal.ReleaseComObject(gs); } catch (Exception) { } }
            }
        }
    }
}
