using System;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>
    /// Recovers the mount after its software (GS Server) stopped during the night: reconnect, AutoHome with the home sensors
    /// (the only way to know where the mount really is), unpark. Tried a few times; the caller continues the night or pauses.
    /// </summary>
    public sealed class MountRecovery {
        private readonly Func<CancellationToken, Task<bool>> connect;
        private readonly Func<CancellationToken, Task<string>> autoHome;
        private readonly Func<CancellationToken, Task<bool>> unpark;
        private readonly Func<TimeSpan, CancellationToken, Task> delay;
        private readonly Action<string> log;
        private readonly Func<CancellationToken, Task<bool>> restartSoftware;

        /// <param name="autoHome">Runs AutoHome; returns null when it worked, otherwise the reason it failed.</param>
        /// <param name="restartSoftware">Closes the mount software so that the next reconnect starts it again; true when it was closed.</param>
        public MountRecovery(Func<CancellationToken, Task<bool>> connect, Func<CancellationToken, Task<string>> autoHome,
                             Func<CancellationToken, Task<bool>> unpark, Func<TimeSpan, CancellationToken, Task> delay, Action<string> log,
                             Func<CancellationToken, Task<bool>> restartSoftware = null) {
            this.restartSoftware = restartSoftware;
            this.connect = connect;
            this.autoHome = autoHome;
            this.unpark = unpark;
            this.delay = delay;
            this.log = log;
        }

        public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        /// <summary>
        /// AutoHome's reason says GS Server holds no link to the mount (e.g. "ErrQueueFailed, Queue | IsConnected:False"): the program
        /// was left open with a dead connection (the mount lost power, or the program was restarted by hand), and connecting again
        /// through ASCOM does not open a new link. Only starting the program again does.
        /// </summary>
        public static bool MountLinkIsDead(string reason) =>
            reason != null && (reason.IndexOf("IsConnected:False", StringComparison.OrdinalIgnoreCase) >= 0
                            || reason.IndexOf("IsConnected: False", StringComparison.OrdinalIgnoreCase) >= 0);

        /// <summary>True when the mount is connected, homed on its sensors and unparked.</summary>
        public async Task<bool> Run(int tries, CancellationToken token) {
            for (var i = 1; i <= Math.Max(1, tries); i++) {
                log($"Mount recovery {i}/{tries}: waiting {Wait.TotalSeconds:0} s, then reconnecting the mount");
                await delay(Wait, token);
                if (!await connect(token)) { log($"Mount recovery {i}/{tries}: the mount could not be connected"); continue; }
                log($"Mount recovery {i}/{tries}: connected; AutoHome with the home sensors");
                var error = await autoHome(token);
                if (error != null) {
                    log($"Mount recovery {i}/{tries}: AutoHome failed: {error}");
                    if (restartSoftware != null && MountLinkIsDead(error)) {
                        log($"Mount recovery {i}/{tries}: the mount software has no link to the mount; closing it so that it starts again");
                        if (!await restartSoftware(token)) { log($"Mount recovery {i}/{tries}: the mount software could not be closed"); }
                    }
                    continue;
                }
                if (!await unpark(token)) { log($"Mount recovery {i}/{tries}: the mount could not be unparked"); continue; }
                log("Mount recovery: done; the mount position is known again");
                return true;
            }
            log($"Mount recovery failed after {tries} tries: the mount position is unknown");
            return false;
        }
    }
}
