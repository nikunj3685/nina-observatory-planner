using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    /// <summary>What happens when the guide star is lost and not found again within the wait.</summary>
    public enum GuideLostAction {
        /// <summary>4 End runs and there is no more imaging tonight.</summary>
        StopForNight,
        /// <summary>The target is skipped for the rest of tonight; with no other target, 4 End runs.</summary>
        NextTarget
    }

    /// <summary>The guide star was lost during a target's imaging and the guider did not find it again in time.</summary>
    public sealed class GuideStarLostException : Exception {
        public GuideStarLostException(string target) : base($"{target}: the guide star was lost and not found again") { }
    }

    /// <summary>
    /// Watches one guider condition during light frames: the guide star lost, or the guiding error above the limit.
    /// A condition shorter than <see cref="Grace"/> is ignored (a passing bird, one bad guide frame); a longer one drops the
    /// frame being taken, then the planner waits for the condition to clear.
    /// </summary>
    public sealed class GuiderWatch {
        /// <summary>Conditions shorter than this are ignored.</summary>
        public static readonly TimeSpan Grace = TimeSpan.FromSeconds(10);

        private readonly Func<bool> isBad;
        private readonly Func<DateTime> now;

        public GuiderWatch(Func<bool> isBad, Func<DateTime> now) {
            this.isBad = isBad;
            this.now = now;
        }

        /// <summary>When the current bad spell started, or null while all is well.</summary>
        public DateTime? BadSince { get; private set; }

        /// <summary>Looks at the guider once. True when it has been bad without a break for at least <see cref="Grace"/>.</summary>
        public bool Poll() {
            if (!isBad()) {
                BadSince = null;
                return false;
            }
            BadSince ??= now();
            return now() - BadSince.Value >= Grace;
        }

        /// <summary>
        /// Waits until the condition clears, at most <paramref name="wait"/> counted from when it started.
        /// Returns true when it cleared. <paramref name="status"/> gets the time left every second.
        /// </summary>
        public async Task<bool> WaitUntilGood(TimeSpan wait, Func<TimeSpan, CancellationToken, Task> delay, Action<TimeSpan> status, CancellationToken token) {
            if (!isBad()) { BadSince = null; return true; }
            BadSince ??= now();
            var until = BadSince.Value + wait;
            while (isBad()) {
                var left = until - now();
                if (left <= TimeSpan.Zero) { return false; }
                status?.Invoke(left);
                await delay(left < TimeSpan.FromSeconds(1) ? left : TimeSpan.FromSeconds(1), token);
            }
            BadSince = null;
            return true;
        }
    }

    /// <summary>
    /// The guiding error of the last guide steps, in guide camera pixels: the root mean square of each step's total
    /// distance from the lock position (√(RA² + Dec²)), over the last <see cref="Steps"/> steps. A constant offset counts too,
    /// unlike a standard deviation. Unknown (null) with fewer than 3 recent steps, e.g. while not guiding.
    /// </summary>
    public sealed class GuideErrorWindow {
        public const int Steps = 10;
        /// <summary>Older steps don't count (guiding stopped or the star is lost).</summary>
        public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

        private readonly object gate = new();
        private readonly Queue<(DateTime At, double Ra, double Dec)> steps = new();

        public void Add(DateTime at, double raPixels, double decPixels) {
            lock (gate) {
                steps.Enqueue((at, raPixels, decPixels));
                while (steps.Count > Steps) { steps.Dequeue(); }
            }
        }

        /// <summary>After a dither or a guiding restart the old steps say nothing about the guiding now.</summary>
        public void Clear() { lock (gate) { steps.Clear(); } }

        public double? Total(DateTime now) {
            lock (gate) {
                var recent = steps.Where(s => now - s.At <= MaxAge).ToList();
                if (recent.Count < 3) { return null; }
                return Math.Sqrt(recent.Average(s => s.Ra * s.Ra + s.Dec * s.Dec));
            }
        }
    }
}
