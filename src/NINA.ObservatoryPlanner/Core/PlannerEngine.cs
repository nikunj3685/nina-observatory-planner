using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    public enum PlannerPhase { Idle, Connecting, WaitingForSafe, Begin, Imaging, WaitingBetweenTargets, End, WaitingForNextNight, Finished, Stopped, Paused }

    public readonly record struct SafetyState(bool Connected, bool IsSafe);

    public interface ISafetySource { SafetyState Read(); }

    public interface IPlannerClock {
        DateTime Now { get; }
        Task Delay(TimeSpan duration, CancellationToken token);
    }

    public sealed class SystemClock : IPlannerClock {
        public DateTime Now => DateTime.Now;
        public Task Delay(TimeSpan duration, CancellationToken token) => Task.Delay(duration < TimeSpan.Zero ? TimeSpan.Zero : duration, token);
    }

    /// <summary>Everything the engine asks the observatory to do. NINA implements it with sequencer instructions.</summary>
    public interface IPlannerHardware {
        Task ConnectKeptDevices(CancellationToken token);
        Task RunStage(StageKind stage, CancellationToken token);
        /// <summary>Runs 2 Start of target, then images until done, the window closes or the token is cancelled. Returns frames taken.</summary>
        Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token);
        Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token);

        /// <summary>
        /// Resume on the same target with the mount where it was: instead of 2 Start of target, only make sure the mount
        /// tracks and restart guiding without calibration, then continue imaging.
        /// </summary>
        Task<int> ResumeTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) => RunTarget(target, canStartFrame, token);

        /// <summary>Devices needed for imaging that are not connected after one reconnect attempt (empty when all are there).</summary>
        Task<IReadOnlyList<string>> CheckConnections(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        /// <summary>Where the mount points now (J2000 hours, degrees), or null when unknown.</summary>
        (double RaHours, double DecDeg)? MountPosition() => null;

        Task StopGuiding(CancellationToken token) => Task.CompletedTask;
    }

    public interface IPlannerLog {
        void Phase(PlannerPhase phase, string message, PlannerTarget target = null);
        void Info(string message);
    }

    public enum NightResult { Finished, Unsafe, Paused, Stopped }

    /// <summary>How a run ended.</summary>
    public enum RunOutcome { Finished, Paused, Stopped }

    /// <summary>What the user asked for while a run is going.</summary>
    public enum PauseKind { None, Now, AfterFrame }

    /// <summary>Where a paused run stood: the target being imaged and where the mount pointed.</summary>
    public sealed record PausePoint(Guid? TargetId, string TargetName, double? MountRaHours, double? MountDecDeg, DateTime At);

    /// <summary>How a run starts.</summary>
    public enum StartKind {
        /// <summary>Run forever / Run: the normal loop.</summary>
        Normal,
        /// <summary>Start sequence after a pause: check connections and safety, then continue where it stopped.</summary>
        Resume,
        /// <summary>The weather turned unsafe while paused: 4 End, wait for safe, 1 Begin, then paused again.</summary>
        WeatherWhilePaused,
        /// <summary>Stop pressed while paused: run 4 End and stop.</summary>
        StopFromPause
    }

    /// <summary>
    /// The night loop. With safety: wait for safe → 1 Begin → targets → 4 End, forever; unsafe at any point jumps to 4 End,
    /// then waits for safe and resumes. Without safety: 1 Begin → targets → 4 End once.
    /// Pause stops at a safe point and keeps everything powered; Stop runs 4 End and ends the run.
    /// </summary>
    public class PlannerEngine {
        private readonly PlannerOptions options;
        private readonly TargetSelector selector;
        private readonly Func<IList<PlannerTarget>> targets;
        private readonly IPlannerHardware hardware;
        private readonly ISafetySource safety;
        private readonly IPlannerClock clock;
        private readonly IPlannerLog log;

        private readonly object gate = new();
        private CancellationTokenSource interrupt = new();
        private volatile PauseKind pauseRequested;
        private volatile bool stopRequested;
        private volatile bool inEnd;
        private PlannerTarget current;

        public TimeSpan SafetyPoll { get; set; } = TimeSpan.FromSeconds(2);
        /// <summary>While waiting for a target, re-check the plan at least this often (the list can be edited meanwhile).</summary>
        public TimeSpan WaitPoll { get; set; } = TimeSpan.FromSeconds(30);
        /// <summary>Download and save time added to each exposure when checking whether a frame still fits in the window.</summary>
        public TimeSpan FrameOverhead { get; set; } = TimeSpan.FromSeconds(10);
        /// <summary>How the safety watchdog waits between checks. Real time by default; tests poll faster.</summary>
        public Func<TimeSpan, CancellationToken, Task> WatchDelay { get; set; } = (d, t) => Task.Delay(d, t);
        /// <summary>A resume counts as "the mount has not moved" within this distance of where it was paused (degrees).</summary>
        public double MountMovedDegrees { get; set; } = 0.5;

        public PlannerEngine(PlannerOptions options, TargetSelector selector, Func<IList<PlannerTarget>> targets,
                             IPlannerHardware hardware, ISafetySource safety, IPlannerClock clock, IPlannerLog log) {
            this.options = options;
            this.selector = selector;
            this.targets = targets;
            this.hardware = hardware;
            this.safety = safety;
            this.clock = clock;
            this.log = log;
        }

        /// <summary>Where the run stood when it paused (set when <see cref="RunAsync"/> returns Paused).</summary>
        public PausePoint Paused { get; private set; }

        /// <summary>Why the run is staying paused instead of continuing (e.g. a device is missing), or null.</summary>
        public string PausedReason { get; private set; }

        public bool PausePending => pauseRequested != PauseKind.None;

        /// <summary>
        /// Pause now: the frame being taken is dropped. Pause after frame: the current frame (or step) finishes first.
        /// Steps other than a frame (1 Begin, a slew, a wait) always finish first; 4 End is never paused.
        /// </summary>
        public void RequestPause(PauseKind kind) {
            if (kind == PauseKind.None) { return; }
            lock (gate) {
                if (pauseRequested == PauseKind.Now) { return; }
                pauseRequested = kind;
                log.Info(kind == PauseKind.Now ? "Pause requested: now" : "Pause requested: after the current frame");
                // the caller passes Now only while a frame is being taken; 4 End is never interrupted
                if (kind == PauseKind.Now && !inEnd) { interrupt.Cancel(); }
            }
        }

        /// <summary>Cancels a pause that has not happened yet.</summary>
        public void CancelPause() {
            lock (gate) {
                if (pauseRequested == PauseKind.None) { return; }
                pauseRequested = PauseKind.None;
                log.Info("Pause cancelled");
            }
        }

        /// <summary>Stop: the current frame is dropped, 4 End runs (unless it already has) and the run ends.</summary>
        public void RequestStop() {
            lock (gate) {
                stopRequested = true;
                log.Info("Stop requested: 4 End runs, then the run stops");
                if (!inEnd) { interrupt.Cancel(); }
            }
        }

        /// <summary>The imaging loop asks before each frame: false when a pause is due.</summary>
        public bool FrameAllowed() => pauseRequested == PauseKind.None && !stopRequested;

        private bool IsSafe() {
            var s = safety.Read();
            return s.Connected && s.IsSafe;
        }

        private async Task Stage(StageKind kind, CancellationToken token) {
            if (kind == StageKind.End) { inEnd = true; }
            try {
                await hardware.RunStage(kind, token);
            } finally {
                if (kind == StageKind.End) { inEnd = false; }
            }
        }

        public async Task<RunOutcome> RunAsync(CancellationToken token, StartKind start = StartKind.Normal, PausePoint resume = null) {
            lock (gate) { interrupt = new CancellationTokenSource(); pauseRequested = PauseKind.None; stopRequested = false; }
            Paused = null;
            PausedReason = null;

            log.Phase(PlannerPhase.Connecting, "Connecting the devices that stay connected");
            await hardware.ConnectKeptDevices(token);

            if (start == StartKind.StopFromPause) {
                log.Phase(PlannerPhase.End, "4 End (stopped)");
                await Stage(StageKind.End, token);
                log.Phase(PlannerPhase.Stopped, "Stopped: 4 End has run");
                return RunOutcome.Stopped;
            }

            if (start == StartKind.WeatherWhilePaused) {
                log.Phase(PlannerPhase.End, "4 End (unsafe while paused)");
                await Stage(StageKind.End, token);
                while (true) {
                    log.Phase(PlannerPhase.WaitingForSafe, "Paused: waiting for the safety monitor to report safe");
                    while (!IsSafe()) { await clock.Delay(SafetyPoll, token); }
                    if (await StaysSafe(token)) { break; }
                }
                log.Phase(PlannerPhase.Begin, "1 Begin (after the weather, still paused)");
                await Stage(StageKind.Begin, token);
                return PauseHere(resume, "Paused: safe again and powered up; press Start sequence to continue");
            }

            var skipBegin = false;
            if (start == StartKind.Resume) {
                var missing = await hardware.CheckConnections(token);
                if (missing.Count > 0) {
                    PausedReason = "Not connected: " + string.Join(", ", missing) + ". Connect them and press Start sequence again.";
                    return PauseHere(resume, "Still paused. " + PausedReason);
                }
                if (options.RunMode == RunMode.WithSafety && !IsSafe()) {
                    log.Info("Resume: it is unsafe now, so 4 End runs first");
                    log.Phase(PlannerPhase.End, "4 End (unsafe)");
                    await Stage(StageKind.End, token);
                    resume = resume == null ? null : resume with { MountRaHours = null, MountDecDeg = null };
                } else {
                    skipBegin = true;
                    log.Info("Resume: devices connected; continuing without 1 Begin");
                }
            }

            if (options.RunMode == RunMode.WithoutSafety) {
                if (!skipBegin && NothingTonight()) {
                    log.Phase(PlannerPhase.Finished, "Finished: nothing to image tonight, so 1 Begin and 4 End did not run");
                    return RunOutcome.Finished;
                }
                var r = await RunNight(token, watchSafety: false, skipBegin, resume);
                return Outcome(r, "Finished: all targets are done and 4 End has run");
            }

            while (true) {
                token.ThrowIfCancellationRequested();
                if (!skipBegin) {
                    if (!IsSafe()) {
                        log.Phase(PlannerPhase.WaitingForSafe, "Waiting for the safety monitor to report safe");
                        while (!IsSafe()) {
                            if (stopRequested) { return Stopped(); }
                            await clock.Delay(SafetyPoll, token);
                        }
                    }
                    if (!await StaysSafe(token)) { continue; }
                    if (stopRequested) { return Stopped(); }
                    if (NothingTonight()) {
                        // Everything is done or out of its window: keep the equipment off instead of powering up for nothing.
                        log.Phase(PlannerPhase.WaitingForNextNight, "Nothing to image tonight; the equipment stays off. Waiting for the next night");
                        while (IsSafe()) {
                            if (stopRequested) { return Stopped(); }
                            await clock.Delay(SafetyPoll, token);
                        }
                        continue;
                    }
                }
                var result = await RunNight(token, watchSafety: true, skipBegin, resume);
                skipBegin = false;
                resume = null;
                if (result is NightResult.Paused or NightResult.Stopped) { return Outcome(result, null); }
                if (result == NightResult.Finished) {
                    // Nothing left tonight. Stay shut down until the monitor reports unsafe (dawn), then wait for the next night.
                    log.Phase(PlannerPhase.WaitingForNextNight, "Nothing left tonight. Waiting for the next night");
                    while (IsSafe()) {
                        if (stopRequested) { return Stopped(); }
                        await clock.Delay(SafetyPoll, token);
                    }
                }
            }
        }

        private RunOutcome Outcome(NightResult r, string finishedMessage) {
            if (r == NightResult.Paused) { return RunOutcome.Paused; }
            if (r == NightResult.Stopped) { return RunOutcome.Stopped; }
            log.Phase(PlannerPhase.Finished, finishedMessage);
            return RunOutcome.Finished;
        }

        /// <summary>Stop while nothing is powered (waiting for safe or for the next night): 4 End is not needed again.</summary>
        private RunOutcome Stopped() {
            log.Phase(PlannerPhase.Stopped, "Stopped");
            return RunOutcome.Stopped;
        }

        private RunOutcome PauseHere(PausePoint point, string message) {
            var mount = hardware.MountPosition();
            Paused = new PausePoint(point?.TargetId ?? current?.Id, point?.TargetName ?? current?.Name,
                mount?.RaHours ?? point?.MountRaHours, mount?.DecDeg ?? point?.MountDecDeg, clock.Now);
            log.Phase(PlannerPhase.Paused, message, current);
            return RunOutcome.Paused;
        }

        private bool NothingTonight() => selector.Decide(targets(), clock.Now, new HashSet<Guid>()).Kind == DecisionKind.NothingTonight;

        /// <summary>The "wait after safe" option: true when it stayed safe for the whole delay.</summary>
        private async Task<bool> StaysSafe(CancellationToken token) {
            if (options.SafeDelaySeconds <= 0) { return true; }
            log.Phase(PlannerPhase.WaitingForSafe, $"Safe: waiting {options.SafeDelaySeconds} s to be sure it stays safe");
            var until = clock.Now + TimeSpan.FromSeconds(options.SafeDelaySeconds);
            while (clock.Now < until) {
                if (!IsSafe()) {
                    log.Info("Unsafe again during the wait after safe: waiting for safe again");
                    return false;
                }
                var left = until - clock.Now;
                await clock.Delay(left < SafetyPoll ? left : SafetyPoll, token);
            }
            if (!IsSafe()) { log.Info("Unsafe again during the wait after safe: waiting for safe again"); return false; }
            return true;
        }

        private bool MountStillAt(PausePoint p) {
            if (p?.MountRaHours == null || p.MountDecDeg == null) { return false; }
            var now = hardware.MountPosition();
            if (now == null) { return false; }
            const double D2R = Math.PI / 180;
            double ra1 = p.MountRaHours.Value * 15 * D2R, dec1 = p.MountDecDeg.Value * D2R, ra2 = now.Value.RaHours * 15 * D2R, dec2 = now.Value.DecDeg * D2R;
            var cos = Math.Sin(dec1) * Math.Sin(dec2) + Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);
            return Math.Acos(Math.Max(-1, Math.Min(1, cos))) / D2R <= MountMovedDegrees;
        }

        private async Task<NightResult> RunNight(CancellationToken token, bool watchSafety, bool skipBegin = false, PausePoint resume = null) {
            CancellationTokenSource interruptNow;
            lock (gate) { interruptNow = interrupt; }
            using var night = CancellationTokenSource.CreateLinkedTokenSource(token, interruptNow.Token);
            var unsafeHit = false;
            var watchdog = watchSafety ? Task.Run(async () => {
                while (!night.IsCancellationRequested) {
                    if (!IsSafe() && !inEnd) {
                        unsafeHit = true;
                        log.Info("Unsafe: interrupting");
                        night.Cancel();
                        return;
                    }
                    try { await WatchDelay(SafetyPoll, night.Token); } catch (OperationCanceledException) { return; }
                }
            }) : Task.CompletedTask;

            var skipTonight = new HashSet<Guid>();
            IReadOnlyList<GapStep> gapResume = null;
            current = null;
            try {
                if (!skipBegin) {
                    log.Phase(PlannerPhase.Begin, "1 Begin");
                    await Stage(StageKind.Begin, night.Token);
                }

                while (true) {
                    night.Token.ThrowIfCancellationRequested();
                    if (PausePending) { return await PauseNight(); }
                    var d = selector.Decide(targets(), clock.Now, skipTonight);

                    if (d.Kind == DecisionKind.RunNow) {
                        var t = d.Target;
                        // Don't slew and start guiding for a target whose next frame no longer fits in its window.
                        var nextFrame = ExposurePlanner.Next(t, null);
                        if (nextFrame != null && !selector.CanStartFrame(t, clock.Now, TimeSpan.FromSeconds(nextFrame.ExposureTime) + FrameOverhead)) {
                            skipTonight.Add(t.Id);
                            log.Info($"{t.Name}: not enough time left in its window for another frame; done for tonight");
                            continue;
                        }
                        if (gapResume != null) { await hardware.RunGapSteps(gapResume, night.Token); gapResume = null; }
                        current = t;
                        bool CanFrame(PlannerExposure e) => FrameAllowed() && selector.CanStartFrame(t, clock.Now, TimeSpan.FromSeconds(e.ExposureTime) + FrameOverhead);
                        int frames;
                        {
                            if (resume != null && resume.TargetId == t.Id && MountStillAt(resume)) {
                                log.Phase(PlannerPhase.Imaging, $"Resume: {t.Name}, mount where it was; restarting guiding", t);
                                frames = await hardware.ResumeTarget(t, CanFrame, night.Token);
                            } else {
                                if (resume != null) { log.Info(resume.TargetId == t.Id ? $"Resume: the mount moved; 2 Start of target runs for {t.Name}" : $"Resume: {t.Name} is next now; 2 Start of target runs"); }
                                log.Phase(PlannerPhase.Imaging, $"2 Start of target: {t.Name}", t);
                                frames = await hardware.RunTarget(t, CanFrame, night.Token);
                            }
                        }
                        resume = null;
                        if (PausePending) { return await PauseNight(); }
                        if (stopRequested) { night.Token.ThrowIfCancellationRequested(); interruptNow.Cancel(); night.Token.ThrowIfCancellationRequested(); }
                        if (frames == 0 && !t.IsComplete && selector.IsOpenAt(t, clock.Now)) {
                            // Nothing could be imaged although the window is open (e.g. a camera error): don't retry it all night.
                            skipTonight.Add(t.Id);
                            log.Info($"No frames taken for {t.Name}; skipping it for the rest of the night");
                        }
                        current = null;
                        continue;
                    }

                    if (d.Kind == DecisionKind.WaitUntil) {
                        current = null;
                        var wait = d.At - clock.Now;
                        if (gapResume == null && wait > TimeSpan.FromMinutes(options.GapMinutes)) {
                            var plan = GapPlan.For(options.GapMount, options.GapCloseDome);
                            log.Phase(PlannerPhase.WaitingBetweenTargets, $"Waiting for {d.Target.Name} at {d.At:HH:mm} ({options.GapMount})", d.Target);
                            await hardware.RunGapSteps(plan.Wait, night.Token);
                            gapResume = plan.Resume;
                        } else {
                            log.Phase(PlannerPhase.WaitingBetweenTargets, $"Waiting for {d.Target.Name} at {d.At:HH:mm}", d.Target);
                        }
                        var wakeAt = gapResume != null ? d.At - TimeSpan.FromMinutes(options.GapLeadMinutes) : d.At;
                        if (gapResume != null && clock.Now >= wakeAt) {
                            await hardware.RunGapSteps(gapResume, night.Token);
                            gapResume = null;
                            continue;
                        }
                        var sleep = wakeAt - clock.Now;
                        if (sleep > WaitPoll) { sleep = WaitPoll; }
                        if (sleep < TimeSpan.FromSeconds(1)) { sleep = TimeSpan.FromSeconds(1); }
                        // wake up early for a pause or stop
                        var slept = TimeSpan.Zero;
                        while (slept < sleep && !PausePending && !stopRequested) {
                            var step = sleep - slept < TimeSpan.FromSeconds(1) ? sleep - slept : TimeSpan.FromSeconds(1);
                            await clock.Delay(step, night.Token);
                            slept += step;
                        }
                        if (gapResume != null && PausePending) { await hardware.RunGapSteps(gapResume, night.Token); gapResume = null; }
                        if (stopRequested) { interruptNow.Cancel(); night.Token.ThrowIfCancellationRequested(); }
                        continue;
                    }

                    break; // nothing left tonight
                }

                night.Cancel(); // stop the watchdog: shutting down because we are done, not because of the weather
                log.Phase(PlannerPhase.End, "4 End (finished)");
                await Stage(StageKind.End, token);
                return stopRequested ? NightStopped() : NightResult.Finished;
            } catch (Exception) when (unsafeHit && !token.IsCancellationRequested) {
                // Interrupted by the weather. NINA instructions may surface this as a cancellation or as their own error.
                log.Phase(PlannerPhase.End, "4 End (unsafe)");
                await Stage(StageKind.End, token);
                if (stopRequested) { return NightStopped(); }
                return NightResult.Unsafe;
            } catch (Exception) when (stopRequested && !token.IsCancellationRequested) {
                log.Phase(PlannerPhase.End, "4 End (stopped)");
                await Stage(StageKind.End, token);
                return NightStopped();
            } catch (Exception) when (pauseRequested == PauseKind.Now && !token.IsCancellationRequested) {
                // Pause now: the frame being taken was dropped.
                if (watchSafety && !IsSafe()) {
                    log.Phase(PlannerPhase.End, "4 End (unsafe)");
                    await Stage(StageKind.End, token);
                    return NightResult.Unsafe;
                }
                return await PauseNight();
            } finally {
                night.Cancel();
                try { await watchdog; } catch (OperationCanceledException) { }
            }

            async Task<NightResult> PauseNight() {
                if (watchSafety && !IsSafe()) {
                    // the weather turned while the pause was due: shut down first, the pause happens after 1 Begin when safe
                    unsafeHit = true;
                    throw new OperationCanceledException("unsafe");
                }
                await hardware.StopGuiding(token);
                PauseHere(null, current == null ? "Paused" : $"Paused on {current.Name}: guiding stopped; tracking, dome and power stay on");
                return NightResult.Paused;
            }

            NightResult NightStopped() {
                log.Phase(PlannerPhase.Stopped, "Stopped: 4 End has run");
                return NightResult.Stopped;
            }
        }
    }
}
