using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Simulated time that passes at 2 s per real millisecond, so a test can change the weather in time.</summary>
    internal sealed class SlowClock : IPlannerClock {
        public readonly FakeClock Inner;
        public SlowClock(DateTime start) { Inner = new FakeClock(start); }
        public DateTime Now => Inner.Now;
        public async Task Delay(TimeSpan duration, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            await Task.Delay(Math.Max(1, (int)(duration.TotalSeconds / 2)), token);
            Inner.Advance(duration);
        }
    }

    /// <summary>A safety monitor switched by the test.</summary>
    internal class SwitchedSafety : ISafetySource {
        public volatile bool Safe = true;
        public SafetyState Read() => new(true, Safe);
    }

    /// <summary>
    /// Hardware that turns the weather unsafe inside one phase. Like NINA, it either honours the cancellation (throws) or,
    /// like an instruction with "Continue on error", swallows it and returns normally.
    /// </summary>
    internal class PhaseHardware : IPlannerHardware {
        private readonly FakeClock clock;
        private readonly SwitchedSafety safety;
        public readonly List<string> Calls = new();
        /// <summary>"1", "2", "imaging" or "4": where the weather turns unsafe (once).</summary>
        public string UnsafeIn { get; set; }
        public bool SwallowCancel { get; set; }
        /// <summary>Make it safe again while 4 End runs.</summary>
        public bool SafeAgainDuringEnd { get; set; }
        private bool fired;

        public PhaseHardware(FakeClock clock, SwitchedSafety safety) { this.clock = clock; this.safety = safety; }

        public Task ConnectKeptDevices(CancellationToken token) { lock (Calls) { Calls.Add("connect"); } return Task.CompletedTask; }

        private async Task TurnUnsafeHere(string phase, CancellationToken token) {
            if (fired || UnsafeIn != phase) { return; }
            fired = true;
            safety.Safe = false;
            // the instruction keeps working until the planner cancels it
            try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch (OperationCanceledException) when (SwallowCancel) { }
        }

        public async Task RunStage(StageKind stage, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            lock (Calls) { Calls.Add($"stage {(int)stage}"); }
            if (stage == StageKind.End) {
                if (SafeAgainDuringEnd) { safety.Safe = true; }
                await Task.Delay(20, CancellationToken.None); // End must not be interrupted
                lock (Calls) { Calls.Add("stage 4 done"); }
                if (UnsafeIn == "4" && !fired) { fired = true; safety.Safe = false; }
                return;
            }
            await TurnUnsafeHere(((int)stage).ToString(), token);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            lock (Calls) { Calls.Add($"target {target.Name}"); }
            await TurnUnsafeHere("2", token); // 2 Start of target: slew, center, guide
            var frames = 0;
            PlannerExposure last = null;
            while (ExposurePlanner.Next(target, last) is { } e) {
                token.ThrowIfCancellationRequested();
                if (!canStartFrame(e)) { break; }
                await TurnUnsafeHere("imaging", token);
                token.ThrowIfCancellationRequested();
                await Task.Delay(1, token);
                clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                e.Done++;
                frames++;
                last = e;
            }
            return frames;
        }

        public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) { lock (Calls) { Calls.Add("gap"); } return Task.CompletedTask; }
    }

    [TestFixture]
    public class NegativeEngineTests {

        private static (PlannerEngine engine, PhaseHardware hw, ListLog log, SwitchedSafety safety) Make(PlannerOptions options, params PlannerTarget[] targets) {
            var clock = new SlowClock(TestData.Night(21));
            var safety = new SwitchedSafety();
            var hw = new PhaseHardware(clock.Inner, safety);
            var log = new ListLog();
            var list = targets.ToList();
            var engine = new PlannerEngine(options, TestData.Selector(options), () => list, hw, safety, clock, log) {
                WatchDelay = (d, t) => Task.Delay(1, t),
                SafetyPoll = TimeSpan.FromSeconds(2)
            };
            return (engine, hw, log, safety);
        }

        private static async Task RunUntil(PlannerEngine engine, Func<bool> done, int seconds = 20) {
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && !run.IsCompleted && DateTime.UtcNow < deadline) { await Task.Delay(5); }
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
            run.IsFaulted.Should().BeFalse(run.Exception?.ToString());
        }

        private static string[] Calls(PhaseHardware hw) { lock (hw.Calls) { return hw.Calls.ToArray(); } }

        [TestCase("1", false)]
        [TestCase("1", true)]
        [TestCase("2", false)]
        [TestCase("2", true)]
        [TestCase("imaging", false)]
        [TestCase("imaging", true)]
        public async Task Unsafe_in_a_phase_runs_4_End_then_restarts_at_1_Begin_and_finishes_the_target(string phase, bool instructionSwallowsCancel) {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var (engine, hw, log, safety) = Make(new PlannerOptions(), t);
            hw.UnsafeIn = phase;
            hw.SwallowCancel = instructionSwallowsCancel;

            // safe again a moment after 4 End has finished
            _ = Task.Run(async () => {
                while (!Calls(hw).Contains("stage 4 done")) { await Task.Delay(2); }
                await Task.Delay(30);
                safety.Safe = true;
            });
            await RunUntil(engine, () => t.IsComplete && Calls(hw).Count(c => c == "stage 4 done") >= 2);

            var calls = Calls(hw);
            var firstEnd = Array.IndexOf(calls, "stage 4");
            firstEnd.Should().BeGreaterThan(0, "unsafe must lead to 4 End");
            log.Phases.Should().Contain(p => p.message == "4 End (unsafe)");
            calls.Skip(firstEnd).Should().ContainInOrder("stage 4", "stage 4 done", "stage 1", "target T", "stage 4", "stage 4 done");
            if (phase == "1") {
                calls.Take(firstEnd).Should().NotContain("target T", "no target starts after an interrupted 1 Begin");
            }
            t.DoneFrames.Should().Be(3, "the target finishes after the restart, nothing is lost or repeated");
            log.Phases.Count(p => p.message == "4 End (unsafe)").Should().Be(1);
        }

        [Test]
        public async Task Unsafe_during_4_End_does_not_cut_it_short() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var (engine, hw, log, _) = Make(new PlannerOptions(), t);
            hw.UnsafeIn = "4";
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForSafe));
            Calls(hw).Should().ContainInOrder("stage 1", "target T", "stage 4", "stage 4 done");
            log.Phases.Should().Contain(p => p.message == "4 End (finished)");
        }

        [Test]
        public async Task Safe_again_during_4_End_finishes_End_first_then_restarts() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var (engine, hw, log, _) = Make(new PlannerOptions(), t);
            hw.UnsafeIn = "imaging";
            hw.SafeAgainDuringEnd = true;
            await RunUntil(engine, () => t.IsComplete && Calls(hw).Count(c => c == "stage 4 done") >= 2);
            var calls = Calls(hw);
            calls.Should().ContainInOrder("stage 4", "stage 4 done", "stage 1", "target T");
            var endDone = Array.IndexOf(calls, "stage 4 done");
            Array.IndexOf(calls, "stage 1", endDone).Should().BeGreaterThan(endDone, "1 Begin runs again only after 4 End has finished");
        }

        [Test]
        public async Task Wait_after_safe_starts_1_Begin_only_after_it_stayed_safe() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var options = new PlannerOptions { SafeDelaySeconds = 120 };
            var (engine, hw, log, safety) = Make(options, t);
            safety.Safe = false;
            var clockAtBegin = DateTime.MinValue;
            // safe at 21:00, unsafe again after 60 s (inside the 120 s wait), safe again for good
            _ = Task.Run(async () => {
                while (!log.Phases.Any(p => p.phase == PlannerPhase.WaitingForSafe)) { await Task.Delay(1); }
                safety.Safe = true;
                while (!log.Phases.Any(p => p.message.StartsWith("Safe: waiting 120 s"))) { await Task.Delay(1); }
                safety.Safe = false;
                await Task.Delay(30);
                safety.Safe = true;
            });
            await RunUntil(engine, () => Calls(hw).Contains("stage 4 done"));
            log.Phases.Count(p => p.message.StartsWith("Safe: waiting 120 s")).Should().BeGreaterThanOrEqualTo(2, "the unsafe blip restarts the wait");
            Calls(hw).Should().ContainInOrder("connect", "stage 1", "target T", "stage 4");
        }

        [Test]
        public async Task Wait_after_safe_delays_1_Begin_by_the_set_time() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var safety = new SwitchedSafety();
            var log = new ListLog();
            var begunAt = DateTime.MinValue;
            var hw = new RecordingHardware(clock, () => begunAt = clock.Now);
            var options = new PlannerOptions { SafeDelaySeconds = 300 };
            var engine = new PlannerEngine(options, TestData.Selector(options), () => new List<PlannerTarget> { t }, hw, safety, clock, log) { WatchDelay = (d, tk) => Task.Delay(1, tk) };
            await RunUntil(engine, () => begunAt != DateTime.MinValue);
            begunAt.Should().BeOnOrAfter(TestData.Night(21, 5), "1 Begin starts 300 s after it became safe");
        }

        [Test]
        public async Task A_target_whose_next_frame_no_longer_fits_is_not_started_again() {
            // window ends 21:03; 2 Start of target takes a minute in this rig, frames are 300 s: none can fit
            var t = TestData.Circumpolar("Short", frames: 5, exposure: 300);
            t.End = TestData.At(21, 3);
            var (engine, hw, log, _) = Make(new PlannerOptions(), t);
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight));
            Calls(hw).Should().NotContain("target Short", "there is no time for even one 300 s frame before 21:03");
            Calls(hw).Should().Contain("stage 4");
        }

        [Test]
        public async Task Nothing_to_image_keeps_the_equipment_off() {
            var done = TestData.Circumpolar("Done", frames: 1);
            done.Exposures[0].Done = 1;
            var (engine, hw, log, _) = Make(new PlannerOptions(), done);
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight));
            Calls(hw).Should().Equal("connect");
            log.Phases.Should().Contain(p => p.message.StartsWith("Nothing to image tonight"));
        }

        [Test]
        public async Task Without_safety_and_nothing_to_image_does_not_power_up() {
            var done = TestData.Circumpolar("Done", frames: 1);
            done.Exposures[0].Done = 1;
            var (engine, hw, log, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, done);
            await engine.RunAsync(CancellationToken.None);
            Calls(hw).Should().Equal("connect");
            log.Phases.Last().phase.Should().Be(PlannerPhase.Finished);
        }

        /// <summary>Calls back when 1 Begin starts.</summary>
        private sealed class RecordingHardware : IPlannerHardware {
            private readonly FakeClock clock;
            private readonly Action begin;
            public RecordingHardware(FakeClock clock, Action begin) { this.clock = clock; this.begin = begin; }
            public Task ConnectKeptDevices(CancellationToken token) => Task.CompletedTask;
            public Task RunStage(StageKind stage, CancellationToken token) { if (stage == StageKind.Begin) { begin(); } return Task.CompletedTask; }
            public Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) { target.Exposures[0].Done = target.Exposures[0].Count; return Task.FromResult(1); }
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => Task.CompletedTask;
        }
    }
}
