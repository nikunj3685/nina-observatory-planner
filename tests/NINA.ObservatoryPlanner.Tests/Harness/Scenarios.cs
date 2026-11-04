using NINA.ObservatoryPlanner.Core;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests.Harness {

    /// <summary>One simulated night: options, targets, the weather, hardware faults and when the user pauses or stops.</summary>
    internal sealed class Scenario {
        public int Seed;
        public PlannerOptions Options;
        public List<PlannerTarget> Targets;
        public SimFaults Faults = SimFaults.None;
        public DateTime Start;
        public Func<HarnessClock, RandomSafety> Safety;
        public int? PauseAtFrame;
        public PauseKind PauseKind = PauseKind.Now;
        public int? StopAtFrame;
        /// <summary>After a pause, how many times the user presses Start sequence.</summary>
        public int Resumes = 1;
        public string Description = "";
        public override string ToString() => $"seed {Seed}: {Description}";
    }

    internal sealed class ScenarioResult {
        public SimEvent[] Journal;
        public List<(PlannerPhase phase, string message)> Phases;
        public List<string> Infos;
        public List<RunOutcome> Outcomes = new();
        public Exception Error;
        public bool TimedOut;
        /// <summary>Nothing was running or powered when the run ended: the invariants about the final state apply.</summary>
        public bool Quiescent;
        public SimObservatory Hardware;
        public RandomSafety Safety;
        public Scenario Scenario;
    }

    internal sealed class RecordingLog : IPlannerLog {
        public readonly List<(PlannerPhase phase, string message)> Phases = new();
        public readonly List<string> Infos = new();
        public void Phase(PlannerPhase phase, string message, PlannerTarget target = null) { lock (Phases) { Phases.Add((phase, message)); } }
        public void Info(string message) { lock (Infos) { Infos.Add(message); } }
        public (PlannerPhase phase, string message)[] PhaseSnapshot() { lock (Phases) { return Phases.ToArray(); } }
    }

    internal static class ScenarioGenerator {

        /// <summary>A random but reproducible night: the same seed always gives the same scenario.</summary>
        public static Scenario Make(int seed, bool faults = false, bool userActions = false) {
            var rng = new Random(seed);
            var withSafety = rng.NextDouble() < 0.8;
            var o = new PlannerOptions {
                RunMode = withSafety ? RunMode.WithSafety : RunMode.WithoutSafety,
                UnsafeAction = rng.NextDouble() < 0.5 ? UnsafeAction.CloseUpAndWait : UnsafeAction.RunEnd,
                SafeDelaySeconds = new[] { 0, 0, 60, 300 }[rng.Next(4)],
                CloseUpMaxHours = new[] { 0.5, 1, 2, 3 }[rng.Next(4)],
                GapMinutes = new[] { 10, 30, 60 }[rng.Next(3)],
                GapCloseDome = rng.NextDouble() < 0.5,
                GapMount = (GapMountAction)rng.Next(3),
                AutofocusAfterBegin = false
            };

            var targets = new List<PlannerTarget>();
            var n = rng.Next(1, 4);
            for (var i = 0; i < n; i++) {
                var t = TestData.Circumpolar($"T{i + 1}", frames: rng.Next(1, 25), exposure: new[] { 60, 120, 300, 600 }[rng.Next(4)]);
                if (rng.NextDouble() < 0.5) { t.Exposures.Add(new PlannerExposure { Filter = "OIII", ExposureTime = new[] { 120, 300 }[rng.Next(2)], Count = rng.Next(1, 10) }); }
                if (rng.NextDouble() < 0.3) { t.Start = TestData.At(21 + rng.Next(0, 4), rng.Next(0, 4) * 15); }
                if (rng.NextDouble() < 0.3) { t.End = TestData.At(1 + rng.Next(0, 5), rng.Next(0, 4) * 15); }
                if (rng.NextDouble() < 0.2) { t.Enabled = false; }
                targets.Add(t);
            }

            var start = TestData.Night(20);
            // alternating safe / unsafe spells from the evening on, and unsafe again at 06:30 (day)
            var spells = new List<(int minutesFromStart, bool safe)>();
            var initiallySafe = rng.NextDouble() < 0.6;
            var at = 0;
            var safe = initiallySafe;
            var spellCount = rng.Next(0, 7);
            for (var i = 0; i < spellCount; i++) {
                at += rng.Next(5, 150);
                safe = !safe;
                spells.Add((at, safe));
            }
            var s = new Scenario {
                Seed = seed, Options = o, Targets = targets, Start = start,
                Faults = faults ? new SimFaults { DomeCloseFails = rng.NextDouble() * 0.5, DomeOpenFails = rng.NextDouble() * 0.3, ParkFails = rng.NextDouble() * 0.2 } : SimFaults.None,
                Safety = clock => {
                    var r = new RandomSafety(clock, initiallySafe);
                    foreach (var sp in spells) { r.At(start.AddMinutes(sp.minutesFromStart), sp.safe); }
                    r.At(TestData.Night(6, 30), false);
                    return r;
                }
            };
            if (userActions && rng.NextDouble() < 0.7) {
                if (rng.NextDouble() < 0.6) { s.PauseAtFrame = rng.Next(1, 12); s.PauseKind = rng.NextDouble() < 0.5 ? PauseKind.Now : PauseKind.AfterFrame; s.Resumes = rng.Next(0, 3); }
                else { s.StopAtFrame = rng.Next(1, 12); }
            }
            s.Description = $"{(withSafety ? "safety" : "no safety")}, {o.UnsafeAction}, {n} target(s), {spellCount} weather change(s)"
                + (faults ? ", faults" : "") + (s.PauseAtFrame != null ? $", pause at frame {s.PauseAtFrame} ({s.PauseKind}, {s.Resumes} resume(s))" : "")
                + (s.StopAtFrame != null ? $", stop at frame {s.StopAtFrame}" : "");
            return s;
        }
    }

    internal static class ScenarioRunner {

        /// <summary>Runs the real engine against the simulated observatory until the night is done (or the simulated day is over).</summary>
        public static async Task<ScenarioResult> Run(Scenario s, int realSeconds = 60) {
            var clock = new HarnessClock(s.Start);
            var safety = s.Safety(clock);
            var hw = new SimObservatory(clock, safety, s.Faults, new Random(s.Seed ^ 0x5eed));
            var log = new RecordingLog();
            var selector = TestData.Selector(s.Options);
            var engine = new PlannerEngine(s.Options, selector, () => s.Targets, hw, safety, clock, log) {
                WatchDelay = (d, t) => clock.WaitTick(t),
                StepPoll = (d, t) => Task.Delay(1, t)
            };
            hw.OnFrameStart = n => {
                if (n == s.PauseAtFrame) { engine.RequestPause(s.PauseKind); }
                if (n == s.StopAtFrame) { engine.RequestStop(); }
            };

            var result = new ScenarioResult { Hardware = hw, Safety = safety, Scenario = s };
            using var cts = new CancellationTokenSource();
            var dayOver = TestData.Night(12).AddDays(1); // well past the night
            var deadline = DateTime.UtcNow.AddSeconds(realSeconds);

            async Task<RunOutcome?> Drive() {
                try {
                    var outcome = await engine.RunAsync(cts.Token);
                    result.Outcomes.Add(outcome);
                    var resumes = s.Resumes;
                    while (outcome == RunOutcome.Paused && resumes-- > 0) {
                        outcome = await engine.RunAsync(cts.Token, StartKind.Resume, engine.Paused);
                        result.Outcomes.Add(outcome);
                    }
                    return outcome;
                } catch (OperationCanceledException) {
                    return null;
                } catch (Exception ex) {
                    result.Error = ex;
                    return null;
                }
            }

            var run = Drive();
            while (!run.IsCompleted) {
                if (DateTime.UtcNow > deadline) { result.TimedOut = true; break; }
                var phases = log.PhaseSnapshot();
                if (phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight) || clock.Now > dayOver) { break; }
                await Task.Delay(5);
            }
            cts.Cancel();
            try { await run; } catch (Exception) { }

            result.Journal = hw.Journal;
            result.Phases = log.PhaseSnapshot().ToList();
            lock (log.Infos) { result.Infos = log.Infos.ToList(); }
            var last = result.Phases.LastOrDefault().phase;
            var finished = run.IsCompleted && result.Outcomes.Count > 0 && result.Outcomes.Last() is RunOutcome.Finished or RunOutcome.Stopped;
            result.Quiescent = !result.TimedOut && result.Error == null
                && (finished || last is PlannerPhase.WaitingForNextNight or PlannerPhase.Finished or PlannerPhase.Stopped
                    || (last == PlannerPhase.WaitingForSafe && !hw.Powered));
            return result;
        }
    }

    internal static class Invariants {

        /// <summary>Everything that must hold after any simulated night, as readable violations (empty when all hold).</summary>
        public static List<string> Check(ScenarioResult r) {
            var v = new List<string>();
            var s = r.Scenario;
            var j = r.Journal;
            var hw = r.Hardware;
            var withSafety = s.Options.RunMode == RunMode.WithSafety;

            if (r.Error != null) { v.Add($"the engine threw {r.Error.GetType().Name}: {r.Error.Message}"); }
            if (r.TimedOut) { v.Add("the run did not finish in real time (deadlock or endless loop)"); }

            foreach (var e in j.Where(e => e.Kind == "VIOLATION")) { v.Add($"{e.At:HH:mm:ss} {e.Detail}"); }

            // time never goes backwards
            for (var i = 1; i < j.Length; i++) {
                if (j[i].At < j[i - 1].At) { v.Add($"time went backwards at {j[i].Kind} ({j[i - 1].At:HH:mm:ss} then {j[i].At:HH:mm:ss})"); break; }
            }

            // No frame is started, and the dome is not opened, once it has been unsafe for longer than the watchdog can be late.
            // The real watchdog looks every 2 s; the harness hands the clock over to it, which now and then (about 1 night in 1000)
            // misses a look and so the engine reacts one frame late: the slack is the longest frame plus its download.
            var slack = TimeSpan.FromSeconds(s.Targets.SelectMany(t => t.Exposures).Select(x => x.ExposureTime).DefaultIfEmpty(0).Max() + 130);
            if (withSafety) {
                foreach (var e in j.Where(e => e.Kind is "frame start" or "open dome" or "unpark" or "power on")) {
                    if (r.Safety.UnsafeFor(e.At, slack)) { v.Add($"{e.At:HH:mm:ss} {e.Kind} ({e.Detail}) while unsafe for {slack.TotalSeconds:0} s"); }
                }
            }

            // progress is consistent
            foreach (var t in s.Targets) {
                foreach (var e in t.Exposures) {
                    if (e.Done > e.Count) { v.Add($"{t.Name} {e.Filter}: {e.Done} frames done of {e.Count}"); }
                    if (e.Done < 0) { v.Add($"{t.Name} {e.Filter}: negative progress"); }
                }
                if (!t.Enabled && t.Exposures.Any(e => e.Done > 0)) { v.Add($"{t.Name} is turned off but was imaged"); }
            }
            var done = s.Targets.Sum(t => t.Exposures.Sum(e => e.Done));
            if (done != hw.FramesCompleted) { v.Add($"the list says {done} frames done, the observatory took {hw.FramesCompleted}"); }

            // every 1 Begin gets its 4 End before the next 1 Begin
            var open = false;
            foreach (var e in j) {
                if (e.Kind == "stage 1 begin") {
                    if (open) { v.Add($"{e.At:HH:mm:ss} 1 Begin started again without a 4 End in between"); }
                    open = true;
                } else if (e.Kind == "stage 4 end") {
                    open = false;
                }
            }
            if (!withSafety) {
                var begins = j.Count(e => e.Kind == "stage 1 begin");
                var ends = j.Count(e => e.Kind == "stage 4 end");
                if (begins > 1) { v.Add($"without a safety monitor 1 Begin ran {begins} times"); }
                if (ends > 1) { v.Add($"without a safety monitor 4 End ran {ends} times"); }
            }

            // after the night everything is shut down
            if (r.Quiescent) {
                var opened = j.Any(e => e.Kind == "stage 1 begin");
                if (opened) {
                    if (hw.Powered) { v.Add("the run is over but the power is still on"); }
                    if (hw.Guiding) { v.Add("the run is over but guiding is still on"); }
                    if (!hw.Parked && !s.Faults.Any) { v.Add("the run is over but the mount is not parked"); }
                    if (hw.DomeOpen && !s.Faults.Any) { v.Add("the run is over but the dome is open"); }
                }
            }
            return v;
        }

        public static string Report(ScenarioResult r, List<string> violations) {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(r.Scenario.ToString());
            foreach (var x in violations) { sb.AppendLine("  VIOLATION: " + x); }
            sb.AppendLine("  outcomes: " + string.Join(", ", r.Outcomes));
            sb.AppendLine("  last phases:");
            foreach (var p in r.Phases.TakeLast(8)) { sb.AppendLine($"    {p.phase}: {p.message}"); }
            sb.AppendLine("  last journal:");
            foreach (var e in r.Journal.TakeLast(25)) { sb.AppendLine($"    {e.At:HH:mm:ss} {(e.Safe ? "safe  " : "UNSAFE")} {e.Kind} {e.Detail}"); }
            return sb.ToString();
        }
    }
}
