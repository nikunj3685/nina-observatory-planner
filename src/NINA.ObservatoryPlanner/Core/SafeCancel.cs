using System;
using System.Threading;

namespace NINA.ObservatoryPlanner.Core {

    public static class SafeCancel {
        /// <summary>
        /// Cancels the token source, and does not let a failing cancel callback escape. NINA instructions register callbacks on the
        /// token (for example the dome's OpenShutter calls AbortSlew, which a roof driver may not implement): a callback that throws
        /// makes <see cref="CancellationTokenSource.Cancel()"/> throw, after the token is already cancelled and every callback has run.
        /// Left alone, that error would fault the watchdog and stop the run with an error after the observatory was shut down properly.
        /// </summary>
        /// <returns>The error a callback threw, or null when the cancel went through cleanly.</returns>
        public static Exception Cancel(CancellationTokenSource source) {
            try {
                source.Cancel();
                return null;
            } catch (AggregateException ex) {
                return ex.Flatten().InnerException ?? ex;
            } catch (ObjectDisposedException) {
                return null;
            }
        }
    }
}
