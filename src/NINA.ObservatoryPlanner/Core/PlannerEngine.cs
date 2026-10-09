using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Core {

    public enum PlannerPhase { Idle, Connecting, WaitingForSafe, Begin, Imaging, WaitingBetweenTargets, End, WaitingForNextNight, Finished, Stopped, Paused, ClosedUp }

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

        /// <summary>The instruction or trigger running now, other than a frame (a slew, centering, autofocus, a wait), or null.</summary>
        object CurrentStep() => null;

        /// <summary>True while a step runs that is never interrupted by Pause: a meridian flip, the dome shutter, park, a between-targets step.</summary>
        bool InProtectedStep() => false;

        /// <summary>How many of the stage's first steps are done (after it was interrupted), so it can continue from the next one.</summary>
        int FinishedSteps(StageKind stage) => 0;

        /// <summary>Runs the stage without its first <paramref name="done"/> steps, which ran before a pause.</summary>
        Task ContinueStage(StageKind stage, int done, CancellationToken token) => RunStage(stage, token);

        /// <summary>True once the target's imaging has begun, i.e. 2 Start of target ran to its end.</summary>
        bool ImagingStarted(PlannerTarget target) => true;

        /// <summary>Closes up for the weather: stop guiding, stop tracking, park, close the dome. False when park or the dome failed.</summary>
        async Task<bool> CloseUp(CancellationToken token) {
            await RunGapSteps(GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: true).Wait, token);
            return true;
        }

        /// <summary>Safe again after a close-up: open the dome and unpark (autofocus runs before the next frame). False when that failed.</summary>
        /// <summary>The mount was lost and its position is not known (GS Server stopped while it was not parked).</summary>
        bool MountPositionUnknown => false;

        /// <summary>Reconnect, AutoHome and unpark the mount after it was lost. True when its position is known again.</summary>
        Task<bool> RecoverMount(CancellationToken token) => Task.FromResult(true);

        async Task<bool> Reopen(CancellationToken token) {
            await RunGapSteps(GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: true).Resume, token);
            return true;
        }
    }

    public interface IPlannerLog {
        void Phase(PlannerPhase phase, string message, PlannerTarget target = null);
        void Info(string message);
        /// <summary>Updates the status shown in the panel without logging it (e.g. a countdown).</summary>
        void Status(string message) { }
    }

    public enum NightResult { Finished, Unsafe, Paused, Stopped, StoppedForNight, ClosedUp, MountLost }

    /// <summary>How a run ended.</summary>
    public enum RunOutcome { Finished, Paused, Stopped }

    /// <summary>What the user asked for while a run is going. AfterFrame is "Pause after this step": the frame or instruction running now finishes first.</summary>
    public enum PauseKind { None, Now, AfterFrame }

    /// <summary>
    /// Where a paused run stood: the target being imaged and where the mount pointed. <paramref name="BeginDone"/>: paused
    /// during 1 Begin after that many steps (Start sequence continues 1 Begin from the next one). <paramref name="ImagingStarted"/>:
    /// 2 Start of target had run to its end (only then can a resume skip it).
    /// </summary>
    public sealed record PausePoint(Guid? TargetId, string TargetName, double? MountRaHours, double? MountDecDeg, DateTime At,
                                    int? BeginDone = null, bool ImagingStarted = true);

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
        private volatile bool nightActive;
        private volatile bool waitingForTarget;
        private volatile bool mountLostRequested;
        private int? beginDone;
        private PlannerTarget current;

        /// <summary>How the pause watcher waits between looks at the running step. Real time by default; tests poll faster.</summary>
        public Func<TimeSpan, CancellationToken, Task> StepPoll { get; set; } = (d, t) => Task.Delay(d, t);

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

        /// <summary>True while 4 End runs: it always runs to the end, so it can't be paused.</summary>
        public bool InEnd => inEnd;

        /// <summary>
        /// Pause is possible once 1 Begin has started, until 4 End starts. While waiting for safe or for the next night
        /// nothing is powered: Stop and Run are used instead.
        /// </summary>
        public bool PauseAllowed => nightActive && !inEnd;

        /// <summary>"1:05:09" or "4:07": the time left in a countdown.</summary>
        public static string Countdown(TimeSpan left) {
            if (left < TimeSpan.Zero) { left = TimeSpan.Zero; }
            var s = (int)Math.Ceiling(left.TotalSeconds);
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        /// <summary>
        /// Pause now stops whatever runs (a frame is dropped, a slew, centering, autofocus, a wait or 1 Begin is interrupted).
        /// Pause after this step lets the frame or instruction running now finish first. A meridian flip, the dome shutter
        /// and park always finish first, and 4 End is never paused. Between targets the wait simply ends.
        /// </summary>
        public void RequestPause(PauseKind kind) {
            if (kind == PauseKind.None) { return; }
            object waitFor = null;
            lock (gate) {
                if (inEnd) { log.Info("Pause ignored: 4 End is running and always runs to the end"); return; }
                if (!nightActive) { log.Info("Pause ignored: nothing runs yet (waiting for safe or for the next night); use Stop instead"); return; }
                if (pauseRequested == PauseKind.Now) { return; }
                pauseRequested = kind;
                log.Info(kind == PauseKind.Now ? "Pause requested: now" : "Pause requested: after the current step");
                // the wait between targets watches for a pause itself and first undoes a park
                if (waitingForTarget) { return; }
                if (kind == PauseKind.AfterFrame) {
                    waitFor = hardware.CurrentStep();
                    if (waitFor == null) { return; } // a frame: the imaging loop pauses before the next one
                } else if (!hardware.InProtectedStep()) {
                    interrupt.Cancel();
                    return;
                }
            }
            _ = InterruptWhenAllowed(waitFor);
        }

        /// <summary>Waits until the step <paramref name="waitFor"/> (if any) and any protected step are over, then interrupts.</summary>
        private async Task InterruptWhenAllowed(object waitFor) {
            try {
                while (pauseRequested != PauseKind.None && !stopRequested && nightActive && !inEnd
                       && (hardware.InProtectedStep() || (waitFor != null && ReferenceEquals(hardware.CurrentStep(), waitFor)))) {
                    await StepPoll(TimeSpan.FromMilliseconds(250), CancellationToken.None);
                }
                lock (gate) {
                    if (pauseRequested == PauseKind.None || stopRequested || !nightActive || inEnd || waitingForTarget) { return; }
                    pauseRequested = PauseKind.Now; // the step is over: nothing more is lost by stopping now
                    interrupt.Cancel();
                }
            } catch (Exception ex) {
                log.Info($"Pause could not interrupt the running step: {ex.Message}");
            }
        }

        /// <summary>
        /// The mount was lost (its software stopped) during 1 Begin, 2 Start of target or imaging: interrupt whatever runs and
        /// recover the mount. Ignored in 4 End and while nothing runs.
        /// </summary>
        public void MountLost() {
            lock (gate) {
                if (inEnd || !nightActive || mountLostRequested) { return; }
                mountLostRequested = true;
                log.Info("Mount lost: interrupting for the mount recovery");
                interrupt.Cancel();
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

        private async Task Stage(StageKind kind, CancellationToken token, int? continueFrom = null) {
            if (kind == StageKind.End) { inEnd = true; }
            try {
                if (continueFrom is int done) { await hardware.ContinueStage(kind, done, token); } else { await hardware.RunStage(kind, token); }
            } catch (Exception) when (kind == StageKind.Begin && (pauseRequested == PauseKind.Now || mountLostRequested) && !stopRequested) {
                beginDone = hardware.FinishedSteps(StageKind.Begin); // Start sequence continues 1 Begin from the next step
                throw;
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
                return PauseHere(resume == null ? null : resume with { BeginDone = null, MountRaHours = null, MountDecDeg = null },
                    "Paused: safe again and powered up; press Start sequence to continue");
            }

            var skipBegin = false;
            var reopen = false;
            int? resumeBegin = null;
            if (start == StartKind.Resume && hardware.MountPositionUnknown) {
                log.Phase(PlannerPhase.Connecting, "Start sequence: the mount position is unknown, so the mount is recovered first");
                if (!await hardware.RecoverMount(token)) {
                    PausedReason = RecoveryFailed;
                    return PauseHere(resume, "Still paused. " + PausedReason);
                }
                resume = resume == null ? null : resume with { MountRaHours = null, MountDecDeg = null, ImagingStarted = false };
            }
            if (start == StartKind.Resume && resume?.BeginDone is int done) {
                // paused during 1 Begin: it continues from the next step (it connects the devices itself)
                if (options.RunMode == RunMode.WithSafety && !IsSafe()) {
                    log.Info("Resume: it is unsafe now, so 4 End runs first");
                    log.Phase(PlannerPhase.End, "4 End (unsafe)");
                    await Stage(StageKind.End, token);
                    resume = null;
                } else {
                    resumeBegin = done;
                }
            } else if (start == StartKind.Resume) {
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
                if (!skipBegin && resumeBegin == null && NothingTonight()) {
                    log.Phase(PlannerPhase.Finished, "Finished: nothing to image tonight, so 1 Begin and 4 End did not run");
                    return RunOutcome.Finished;
                }
                var r = await RunNightRecovering(token, watchSafety: false, skipBegin, resume, resumeBegin, false);
                return Outcome(r, r == NightResult.StoppedForNight
                    ? "Stopped for the night: the guide star was lost and 4 End has run"
                    : "Finished: all targets are done and 4 End has run");
            }

            while (true) {
                token.ThrowIfCancellationRequested();
                if (!skipBegin && resumeBegin == null) {
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
                var result = await RunNightRecovering(token, watchSafety: true, skipBegin, resume, resumeBegin, reopen);
                skipBegin = false;
                resumeBegin = null;
                resume = null;
                reopen = false;
                if (result == NightResult.ClosedUp) {
                    var after = await WaitClosedUp(token);
                    if (after == CloseUpEnd.Stopped) { return RunOutcome.Stopped; }
                    if (after == CloseUpEnd.Reopen) { skipBegin = true; reopen = true; continue; }
                    if (after == CloseUpEnd.NightOver) { await WaitForNextNight(token); if (stopRequested) { return Stopped(); } }
                    continue;
                }
                if (result is NightResult.Paused or NightResult.Stopped) { return Outcome(result, null); }
                if (result == NightResult.StoppedForNight) {
                    // the guide star was lost: no more imaging tonight, also when it stays safe
                    var nextNight = NightTime.NightEnd(clock.Now);
                    log.Phase(PlannerPhase.WaitingForNextNight, $"Stopped for tonight: the guide star was lost. Starts again after {nextNight:dd MMM HH:mm}");
                    while (clock.Now < nextNight) {
                        if (stopRequested) { return Stopped(); }
                        var left = nextNight - clock.Now;
                        await clock.Delay(left < WaitPoll ? left : WaitPoll, token);
                    }
                    continue;
                }
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
                mount?.RaHours ?? point?.MountRaHours, mount?.DecDeg ?? point?.MountDecDeg, clock.Now,
                point != null ? point.BeginDone : beginDone,
                point != null ? point.ImagingStarted : current != null && hardware.ImagingStarted(current));
            log.Phase(PlannerPhase.Paused, message, current);
            return RunOutcome.Paused;
        }

        public const string RecoveryFailed = "Mount recovery failed: the mount position is unknown. In GS Server: connect and run AutoHome, then press Start sequence.";

        /// <summary>
        /// Runs the night; when the mount is lost, recovers it (reconnect, AutoHome, unpark) and continues: an interrupted
        /// 1 Begin from its next step, otherwise with 2 Start of target. When the recovery fails the run pauses.
        /// </summary>
        private async Task<NightResult> RunNightRecovering(CancellationToken token, bool watchSafety, bool skipBegin, PausePoint resume, int? resumeBegin, bool reopen) {
            while (true) {
                var r = await RunNight(token, watchSafety, skipBegin, resume, resumeBegin, reopen);
                if (r != NightResult.MountLost) { return r; }
                var lostInBegin = beginDone;
                var lostOn = current;
                log.Phase(PlannerPhase.Connecting, "Mount lost: recovering the mount (reconnect, AutoHome, unpark)", lostOn);
                if (await hardware.RecoverMount(token)) {
                    lock (gate) { interrupt = new CancellationTokenSource(); mountLostRequested = false; }
                    if (lostInBegin is int d) { resumeBegin = d; skipBegin = false; } else { resumeBegin = null; skipBegin = true; }
                    resume = null;
                    reopen = false;
                    continue;
                }
                lock (gate) { mountLostRequested = false; }
                PausedReason = RecoveryFailed;
                current = lostOn;
                Paused = null;
                PauseHere(new PausePoint(lostOn?.Id, lostOn?.Name, null, null, clock.Now, lostInBegin, false), "Paused. " + RecoveryFailed);
                return NightResult.Paused;
            }
        }

        private enum CloseUpEnd { Reopen, Ended, NightOver, Stopped }

        /// <summary>The morning, for the weather close-up: the Sun has risen above its own limit (Civil Dawn by default).</summary>
        private bool Morning() => clock.Now.Hour < 12 && selector.SunAltitude(clock.Now) > SunLimits.Altitude(options.CloseUpEnd);

        /// <summary>
        /// Closed up for the weather, power and camera cooling on. Opens up again when it is safe (after the wait after
        /// safe); runs 4 End when it is still unsafe after the time limit, when the night ends or nothing is left tonight,
        /// or on Stop.
        /// </summary>
        private async Task<CloseUpEnd> WaitClosedUp(CancellationToken token) {
            var deadline = clock.Now + TimeSpan.FromHours(options.CloseUpMaxHours);
            string Waiting() => $"Closed up for the weather: waiting for safe. Power and camera cooling stay on; 4 End runs at {deadline:HH:mm} or at the end of the night if it is still unsafe";
            log.Phase(PlannerPhase.ClosedUp, Waiting());
            while (true) {
                if (stopRequested) {
                    log.Phase(PlannerPhase.End, "4 End (stopped)");
                    await Stage(StageKind.End, token);
                    log.Phase(PlannerPhase.Stopped, "Stopped: 4 End has run");
                    return CloseUpEnd.Stopped;
                }
                var over = Morning() ? "the night has ended" : NothingTonight() ? "nothing left to image tonight" : null;
                var why = over ?? (clock.Now >= deadline ? $"still unsafe after {options.CloseUpMaxHours:0.##} h" : null);
                if (why != null) {
                    log.Phase(PlannerPhase.End, $"4 End ({why})");
                    await Stage(StageKind.End, token);
                    return over != null ? CloseUpEnd.NightOver : CloseUpEnd.Ended;
                }
                if (IsSafe()) {
                    if (await StaysSafe(token)) { return CloseUpEnd.Reopen; }
                    log.Phase(PlannerPhase.ClosedUp, Waiting());
                    continue;
                }
                log.Status($"{Waiting()} ({Countdown(deadline - clock.Now)} left)");
                await clock.Delay(SafetyPoll, token);
            }
        }

        /// <summary>After the night is over: stay off until the next night starts (local noon).</summary>
        private async Task WaitForNextNight(CancellationToken token) {
            var nextNight = NightTime.NightEnd(clock.Now);
            log.Phase(PlannerPhase.WaitingForNextNight, $"The night is over. Starts again after {nextNight:dd MMM HH:mm}");
            while (clock.Now < nextNight && !stopRequested) {
                var left = nextNight - clock.Now;
                await clock.Delay(left < WaitPoll ? left : WaitPoll, token);
            }
        }

        /// <summary>Why Start sequence continues with another target than the paused one.</summary>
        private string ResumeChange(PausePoint p) {
            var paused = p.TargetId == null ? null : targets().FirstOrDefault(x => x.Id == p.TargetId);
            var name = p.TargetName ?? "the paused target";
            if (p.TargetId == null) { return "paused between targets"; }
            if (paused == null) { return $"{name} was removed from the list"; }
            if (!paused.Enabled) { return $"{name} was turned off"; }
            if (paused.IsComplete) { return $"{name} is complete"; }
            if (!selector.IsOpenAt(paused, clock.Now)) { return $"{name} is outside its time window"; }
            return $"another target now comes before {name}";
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
                log.Status($"Safe: 1 Begin starts in {Countdown(left)} if it stays safe");
                // at most a second, so the countdown moves every second
                var step = left < SafetyPoll ? left : SafetyPoll;
                if (step > TimeSpan.FromSeconds(1)) { step = TimeSpan.FromSeconds(1); }
                await clock.Delay(step, token);
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

        private async Task<NightResult> RunNight(CancellationToken token, bool watchSafety, bool skipBegin = false, PausePoint resume = null, int? resumeBegin = null, bool reopen = false) {
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
            var guideLostStop = false;
            IReadOnlyList<GapStep> gapResume = null;
            current = null;
            beginDone = null;
            nightActive = true;
            var beginComplete = skipBegin && resumeBegin == null;
            try {
                if (resumeBegin is int done) {
                    log.Phase(PlannerPhase.Begin, $"1 Begin (continuing from step {done + 1})");
                    await Stage(StageKind.Begin, night.Token, done);
                } else if (!skipBegin) {
                    log.Phase(PlannerPhase.Begin, "1 Begin");
                    await Stage(StageKind.Begin, night.Token);
                }
                beginComplete = true;
                if (reopen) {
                    log.Phase(PlannerPhase.ClosedUp, "Safe again: opening the dome and unparking");
                    if (!await hardware.Reopen(night.Token)) {
                        log.Info("Opening up after the weather failed: 4 End runs");
                        nightActive = false;
                        night.Cancel();
                        log.Phase(PlannerPhase.End, "4 End (opening up failed)");
                        await Stage(StageKind.End, token);
                        return stopRequested ? NightStopped() : NightResult.Unsafe;
                    }
                }

                while (true) {
                    waitingForTarget = false;
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
                        try {
                            if (resume != null && resume.TargetId == t.Id && resume.ImagingStarted && MountStillAt(resume)) {
                                log.Phase(PlannerPhase.Imaging, $"Resume: {t.Name}, mount where it was; restarting guiding", t);
                                frames = await hardware.ResumeTarget(t, CanFrame, night.Token);
                            } else {
                                var why = resume == null ? null
                                    : resume.TargetId != t.Id ? $"Resume: {ResumeChange(resume)}; continuing with {t.Name}. "
                                    : !resume.ImagingStarted ? $"Resume: 2 Start of target had not finished; it runs again for {t.Name}. "
                                    : $"Resume: the mount moved; 2 Start of target runs again for {t.Name}. ";
                                if (why != null) { log.Info(why.Trim()); }
                                log.Phase(PlannerPhase.Imaging, $"{why}2 Start of target: {t.Name}", t);
                                frames = await hardware.RunTarget(t, CanFrame, night.Token);
                            }
                        } catch (GuideStarLostException) when (!night.Token.IsCancellationRequested) {
                            resume = null;
                            current = null;
                            if (PausePending) { return await PauseNight(); }
                            if (options.GuideLostAction == GuideLostAction.NextTarget) {
                                skipTonight.Add(t.Id);
                                log.Info($"{t.Name}: the guide star was not found again; going to the next target (skipped for the rest of tonight)");
                                continue;
                            }
                            log.Info($"{t.Name}: the guide star was not found again; 4 End runs and imaging stops for tonight");
                            guideLostStop = true;
                            break;
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
                        waitingForTarget = true;
                        var wait = d.At - clock.Now;
                        if (gapResume == null && wait > TimeSpan.FromMinutes(options.GapMinutes)) {
                            var plan = GapPlan.For(options.GapMount, options.GapCloseDome);
                            log.Phase(PlannerPhase.WaitingBetweenTargets, $"Waiting for {d.Target.Name} at {d.At:HH:mm} ({options.GapMount})", d.Target);
                            await hardware.RunGapSteps(plan.Wait, night.Token);
                            gapResume = plan.Resume;
                        } else {
                            log.Phase(PlannerPhase.WaitingBetweenTargets, $"Waiting for {d.Target.Name} at {d.At:HH:mm}", d.Target);
                        }
                        // the target starts at its set time: the steps after a long wait (unpark, open the dome) run then too
                        var wakeAt = d.At;
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
                            log.Status($"Waiting for {d.Target.Name} at {d.At:HH:mm} (in {Countdown(d.At - clock.Now)})");
                            var step = sleep - slept < TimeSpan.FromSeconds(1) ? sleep - slept : TimeSpan.FromSeconds(1);
                            await clock.Delay(step, night.Token);
                            slept += step;
                        }
                        waitingForTarget = false;
                        if (gapResume != null && PausePending) { await hardware.RunGapSteps(gapResume, night.Token); gapResume = null; }
                        if (stopRequested) { interruptNow.Cancel(); night.Token.ThrowIfCancellationRequested(); }
                        continue;
                    }

                    break; // nothing left tonight
                }

                nightActive = false;
                night.Cancel(); // stop the watchdog: shutting down because we are done, not because of the weather
                log.Phase(PlannerPhase.End, guideLostStop ? "4 End (guide star lost)" : "4 End (finished)");
                await Stage(StageKind.End, token);
                return stopRequested ? NightStopped() : guideLostStop ? NightResult.StoppedForNight : NightResult.Finished;
            } catch (Exception) when (unsafeHit && !token.IsCancellationRequested) {
                // Interrupted by the weather. NINA instructions may surface this as a cancellation or as their own error.
                return await ShutForWeather();
            } catch (Exception) when (mountLostRequested && !stopRequested && !token.IsCancellationRequested) {
                return NightResult.MountLost;
            } catch (Exception) when (stopRequested && !token.IsCancellationRequested) {
                log.Phase(PlannerPhase.End, "4 End (stopped)");
                await Stage(StageKind.End, token);
                return NightStopped();
            } catch (Exception) when (pauseRequested == PauseKind.Now && !token.IsCancellationRequested) {
                // Pause now: the frame being taken was dropped.
                if (watchSafety && !IsSafe()) { return await ShutForWeather(); }
                return await PauseNight();
            } finally {
                nightActive = false;
                waitingForTarget = false;
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

            // Unsafe: close up and wait (the option, once 1 Begin has finished), otherwise 4 End
            async Task<NightResult> ShutForWeather() {
                nightActive = false;
                if (watchSafety && options.UnsafeAction == UnsafeAction.CloseUpAndWait && beginComplete && !stopRequested) {
                    log.Phase(PlannerPhase.ClosedUp, "Unsafe: closing up (stop guiding, park, close the dome); power and camera cooling stay on");
                    if (await hardware.CloseUp(token)) { return NightResult.ClosedUp; }
                    log.Info("Closing up failed (park or dome): 4 End runs instead");
                }
                log.Phase(PlannerPhase.End, "4 End (unsafe)");
                await Stage(StageKind.End, token);
                if (stopRequested) { return NightStopped(); }
                return NightResult.Unsafe;
            }

            NightResult NightStopped() {
                log.Phase(PlannerPhase.Stopped, "Stopped: 4 End has run");
                return NightResult.Stopped;
            }
        }
    }
}
