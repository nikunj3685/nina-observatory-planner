using NINA.ObservatoryPlanner.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests.Harness {

    /// <summary>
    /// Simulated time with a handshake to the engine's safety watchdog: each time the clock moves (or <see cref="Look"/> is called),
    /// the caller waits until the watchdog has looked at the safety monitor for that moment. Without it a long simulated wait would be
    /// over in microseconds of real time and the engine could act before any check, which real time never allows. No real sleeping.
    /// </summary>
    internal sealed class HarnessClock : IPlannerClock {
        private readonly object gate = new();
        private DateTime now;
        private TaskCompletionSource tick = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long generation, seen;
        private int waiting;
        public static int Ticks, Watched, TimedOut;
        public HarnessClock(DateTime start) { now = start; }
        public DateTime Now { get { lock (gate) { return now; } } }
        public void Advance(TimeSpan d) { lock (gate) { now += d; } }

        /// <summary>The engine's WatchDelay: completes at the next tick of the clock, and says which tick it has seen.</summary>
        public async Task WaitTick(CancellationToken token) {
            Task next;
            lock (gate) { waiting++; seen = generation; next = tick.Task; }
            try { await next.WaitAsync(token); } finally { lock (gate) { waiting--; } }
        }

        /// <summary>Lets the watchdog look at the safety monitor now, without moving the clock.</summary>
        public Task Look(CancellationToken token) => Tick(TimeSpan.Zero, token);

        public Task Delay(TimeSpan duration, CancellationToken token) => Tick(duration, token);

        private async Task Tick(TimeSpan duration, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            TaskCompletionSource old; long target; bool watched;
            lock (gate) {
                now += duration; target = ++generation; old = tick; tick = new(TaskCreationOptions.RunContinuationsAsynchronously); watched = waiting > 0;
            }
            old.TrySetResult();
            Interlocked.Increment(ref Ticks);
            if (watched) {
                Interlocked.Increment(ref Watched);
                var wait = System.Diagnostics.Stopwatch.StartNew();
                while (wait.ElapsedMilliseconds < 100) { // only reached in full when no watchdog answers
                    lock (gate) { if (seen >= target) { break; } }
                    await Task.Yield();
                }
                if (wait.ElapsedMilliseconds >= 100) { Interlocked.Increment(ref TimedOut); }
            } else {
                await Task.Yield();
            }
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>One thing the simulated observatory did, with whether the safety monitor said safe at that moment.</summary>
    internal sealed record SimEvent(DateTime At, string Kind, string Detail, bool Safe);

    /// <summary>What can go wrong in the simulated hardware (each is the chance that one attempt fails).</summary>
    internal sealed class SimFaults {
        public double DomeCloseFails;
        public double DomeOpenFails;
        public double ParkFails;
        public static readonly SimFaults None = new();
        public bool Any => DomeCloseFails > 0 || DomeOpenFails > 0 || ParkFails > 0;
    }

    /// <summary>A safety monitor that follows a script of (from time, safe) entries and can be asked about any time.</summary>
    internal sealed class RandomSafety : ISafetySource {
        private readonly HarnessClock clock;
        private readonly List<(DateTime from, bool safe)> script = new();
        public RandomSafety(HarnessClock clock, bool initiallySafe) { this.clock = clock; script.Add((DateTime.MinValue, initiallySafe)); }
        public RandomSafety At(DateTime from, bool safe) { script.Add((from, safe)); return this; }
        public bool SafeAt(DateTime t) => script.Last(s => s.from <= t).safe;
        public SafetyState Read() => new(true, SafeAt(clock.Now));
        public IReadOnlyList<(DateTime from, bool safe)> Script => script;

        /// <summary>True when it has been unsafe for the whole of the last <paramref name="span"/> (so a late reaction is a fault, not a race).</summary>
        public bool UnsafeFor(DateTime t, TimeSpan span) {
            for (var back = TimeSpan.Zero; back <= span; back += TimeSpan.FromSeconds(15)) { if (SafeAt(t - back)) { return false; } }
            return true;
        }
    }

    /// <summary>
    /// A stateful stand-in for the whole observatory (power, dome, mount, guiding, camera). The real <see cref="PlannerEngine"/> drives it
    /// like NINA; every command is journalled, and commands that would hurt the real rig (closing the dome with the mount not parked,
    /// slewing with the dome shut, imaging unpowered) are journalled as VIOLATION.
    /// </summary>
    internal sealed class SimObservatory : IPlannerHardware {
        private readonly HarnessClock clock;
        private readonly RandomSafety safety;
        private readonly Random rng;
        private readonly SimFaults faults;
        private readonly object gate = new();
        private readonly List<SimEvent> journal = new();

        public bool Powered, Connected, Cooled, DomeOpen, Parked = true, Tracking, Guiding;
        public int FramesStarted, FramesCompleted, DomeCloseFailures, DomeCloseCommands;

        /// <summary>Called when a frame is about to start, with the number of frames started so far (to inject a pause or stop).</summary>
        public Action<int> OnFrameStart;

        private int beginDone;
        private static readonly string[] BeginSteps = { "power on", "connect", "cool camera", "open dome", "unpark" };
        private static readonly string[] EndSteps = { "stop guiding", "park", "close dome", "warm camera", "power off" };

        public SimObservatory(HarnessClock clock, RandomSafety safety, SimFaults faults, Random rng) {
            this.clock = clock; this.safety = safety; this.faults = faults; this.rng = rng;
        }

        public SimEvent[] Journal { get { lock (gate) { return journal.ToArray(); } } }

        private void Add(string kind, string detail = "") {
            lock (gate) { journal.Add(new SimEvent(clock.Now, kind, detail, safety.SafeAt(clock.Now))); }
        }
        private void Violation(string what) => Add("VIOLATION", what);
        private bool Fails(double chance) { lock (gate) { return chance > 0 && rng.NextDouble() < chance; } }
        private void Take(double seconds) => clock.Advance(TimeSpan.FromSeconds(seconds));

        // ---------- single actions ----------

        private void PowerOn() { Add("power on"); Powered = true; Take(20); }
        private void Connect() { Add("connect"); if (!Powered) { Violation("connect with the power off"); } Connected = true; Take(30); }
        private void Cool() { Add("cool camera"); if (!Connected) { Violation("cool the camera while disconnected"); } Cooled = true; Take(60); }
        /// <summary>
        /// A failing Open Dome in 1 Begin is not simulated: in NINA the step's error behaviour stops the whole sequence and the
        /// service (not the engine) runs 4 End and stops. After a close-up and in the gap steps the engine sees the failure.
        /// </summary>
        private bool OpenDome(bool inBegin = false) {
            Add("open dome");
            if (!inBegin && Fails(faults.DomeOpenFails)) { Add("open dome failed"); Take(60); return false; }
            DomeOpen = true; Take(60); return true;
        }
        private void Unpark() { Add("unpark"); Parked = false; Tracking = true; Take(15); }
        private void StopGuiding() { Add("stop guiding"); Guiding = false; Take(2); }
        private void StopTracking() { Add("stop tracking"); Tracking = false; Take(1); }
        private bool Park() {
            Add("park");
            if (Guiding) { Violation("park while guiding"); }
            if (Fails(faults.ParkFails)) { Add("park failed"); Take(30); return false; }
            Parked = true; Tracking = false; Take(30); return true;
        }
        private bool CloseDome() {
            Add("close dome");
            DomeCloseCommands++;
            if (DomeOpen && !Parked) { Violation("close the dome with the mount not parked"); }
            if (Fails(faults.DomeCloseFails)) { DomeCloseFailures++; Add("close dome failed"); Take(36); return false; }
            DomeOpen = false; Take(30); return true;
        }
        private void WarmCamera() { Add("warm camera"); Cooled = false; Take(180); }
        private void PowerOff() { Add("power off"); Powered = false; Connected = false; Cooled = false; Take(5); }

        // ---------- IPlannerHardware ----------

        public Task ConnectKeptDevices(CancellationToken token) { Add("connect kept devices"); return Task.CompletedTask; }

        public async Task RunStage(StageKind stage, CancellationToken token) {
            if (stage == StageKind.Begin) { beginDone = 0; Add("stage 1 begin"); await RunBegin(0, token); return; }
            if (stage == StageKind.End) { Add("stage 4 end"); await RunEnd(); return; }
            Add($"stage {(int)stage}");
        }

        public Task ContinueStage(StageKind stage, int done, CancellationToken token) {
            if (stage != StageKind.Begin) { return RunStage(stage, token); }
            Add($"stage 1 begin continues from step {done + 1}");
            return RunBegin(done, token);
        }

        public int FinishedSteps(StageKind stage) => stage == StageKind.Begin ? beginDone : 0;

        private async Task RunBegin(int from, CancellationToken token) {
            for (var i = from; i < BeginSteps.Length; i++) {
                token.ThrowIfCancellationRequested();
                // real steps take real time: let the engine's watchdog look at the safety monitor between steps
                await clock.Look(token);
                token.ThrowIfCancellationRequested();
                switch (i) {
                    case 0: PowerOn(); break;
                    case 1: Connect(); break;
                    case 2: Cool(); break;
                    case 3: OpenDome(inBegin: true); break;
                    case 4: Unpark(); break;
                }
                beginDone = i + 1;
            }
        }

        /// <summary>4 End always runs to the end (NINA's error behaviour is "continue"): a failed step does not stop the next ones.</summary>
        private async Task RunEnd() {
            for (var i = 0; i < EndSteps.Length; i++) {
                await Task.Yield();
                switch (i) {
                    case 0: StopGuiding(); break;
                    case 1: Park(); break;
                    case 2: CloseDome(); break;
                    case 3: WarmCamera(); break;
                    case 4: PowerOff(); break;
                }
            }
        }

        public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            Add("2 start of target", target.Name);
            if (!DomeOpen) { Violation($"2 Start of target {target.Name} with the dome closed"); }
            if (Parked) { Violation($"2 Start of target {target.Name} with the mount parked"); }
            if (!Powered || !Connected) { Violation($"2 Start of target {target.Name} unpowered or disconnected"); }
            Take(120); // slew, center
            Tracking = true; Guiding = true;
            token.ThrowIfCancellationRequested();
            return await Image(target, canStartFrame, token);
        }

        public Task<int> ResumeTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            Add("resume target", target.Name);
            if (!DomeOpen) { Violation($"resume {target.Name} with the dome closed"); }
            if (Parked) { Violation($"resume {target.Name} with the mount parked"); }
            Tracking = true; Guiding = true;
            return Image(target, canStartFrame, token);
        }

        private async Task<int> Image(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            var frames = 0;
            PlannerExposure last = null;
            while (ExposurePlanner.Next(target, last) is { } e) {
                token.ThrowIfCancellationRequested();
                if (!canStartFrame(e)) { break; }
                // give the engine's watchdog a look at the safety monitor before the frame, as the 2 s poll would in real time
                await clock.Look(token);
                token.ThrowIfCancellationRequested();
                if (!Powered || !Cooled) { Violation($"frame of {target.Name} with the camera unpowered or not cooled"); }
                if (!DomeOpen) { Violation($"frame of {target.Name} with the dome closed"); }
                if (Parked || !Tracking) { Violation($"frame of {target.Name} with the mount parked or not tracking"); }
                FramesStarted++;
                Add("frame start", $"{target.Name} {e.Filter} {e.ExposureTime:0}s");
                OnFrameStart?.Invoke(FramesStarted);
                var left = e.ExposureTime;
                while (left > 0) {
                    var chunk = Math.Min(left, 150);
                    try {
                        await clock.Look(token);
                        token.ThrowIfCancellationRequested();
                    } catch (OperationCanceledException) {
                        Add("frame aborted", target.Name);
                        throw;
                    }
                    Take(chunk);
                    left -= chunk;
                }
                Take(10); // download and save
                e.Done++;
                frames++;
                FramesCompleted++;
                Add("frame end", $"{target.Name} {e.Filter}");
                last = e;
            }
            return frames;
        }

        public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) {
            Gap(steps);
            return Task.CompletedTask;
        }

        /// <summary>The steps after a wait between targets; false when the dome did not open.</summary>
        public Task<bool> ResumeAfterGap(IReadOnlyList<GapStep> steps, CancellationToken token) => Task.FromResult(Gap(steps));

        private bool Gap(IReadOnlyList<GapStep> steps) {
            Add("gap", string.Join(",", steps));
            var ok = true;
            foreach (var s in steps) {
                switch (s) {
                    case GapStep.StopGuiding: StopGuiding(); break;
                    case GapStep.StopTracking: StopTracking(); break;
                    case GapStep.Park: Park(); break;
                    case GapStep.FindHome: Add("find home"); Take(60); break;
                    case GapStep.CloseDome: CloseDome(); break;
                    case GapStep.OpenDome: ok &= OpenDome(); break;
                    case GapStep.Unpark: Unpark(); break;
                }
            }
            return ok;
        }

        public Task<bool> CloseUp(CancellationToken token) {
            Add("close up");
            StopGuiding(); StopTracking();
            var ok = Park();
            ok &= CloseDome();
            return Task.FromResult(ok);
        }

        public Task<bool> Reopen(CancellationToken token) {
            Add("reopen");
            var ok = OpenDome();
            Unpark();
            return Task.FromResult(ok);
        }
    }
}
