using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Simulated time: waiting moves the clock forward instead of sleeping.</summary>
    internal class FakeClock : IPlannerClock {
        private readonly object gate = new();
        private DateTime now;
        public FakeClock(DateTime start) { now = start; }
        public DateTime Now { get { lock (gate) { return now; } } }
        public void Advance(TimeSpan d) { lock (gate) { now += d; } }
        public async Task Delay(TimeSpan duration, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Advance(duration);
            await Task.Yield();
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>A safety monitor that follows a script of (from time, safe) entries.</summary>
    internal class ScriptedSafety : ISafetySource {
        private readonly FakeClock clock;
        private readonly List<(DateTime from, bool safe)> script = new();
        public ScriptedSafety(FakeClock clock, bool initiallySafe) { this.clock = clock; script.Add((DateTime.MinValue, initiallySafe)); }
        public ScriptedSafety At(DateTime from, bool safe) { script.Add((from, safe)); return this; }
        public SafetyState Read() => new(true, script.Last(s => s.from <= clock.Now).safe);
    }

    /// <summary>Records what the engine asks for; each frame takes its exposure time of simulated time.</summary>
    internal class FakeHardware : IPlannerHardware {
        private readonly FakeClock clock;
        public readonly List<string> Calls = new();
        public bool FailFrames { get; set; }
        public FakeHardware(FakeClock clock) { this.clock = clock; }

        public Task ConnectKeptDevices(CancellationToken token) { Calls.Add("connect"); return Task.CompletedTask; }

        public async Task RunStage(StageKind stage, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Calls.Add($"stage {(int)stage}");
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Yield();
        }

        public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            Calls.Add($"target {target.Name} at {clock.Now:HH:mm}");
            clock.Advance(TimeSpan.FromMinutes(2)); // 2 Start of target: slew, center, guide
            var frames = 0;
            PlannerExposure last = null;
            while (ExposurePlanner.Next(target, last) is { } e) {
                token.ThrowIfCancellationRequested();
                if (!canStartFrame(e)) { break; }
                // let the watchdog look at the safety monitor during the frame
                for (int i = 0; i < 5; i++) { await Task.Delay(1, token); }
                clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                token.ThrowIfCancellationRequested();
                if (FailFrames) { last = e; if (++frames > 3) { break; } continue; }
                e.Done++;
                frames++;
                last = e;
            }
            return FailFrames ? 0 : frames;
        }

        public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) {
            Calls.Add("gap " + string.Join(",", steps));
            return Task.CompletedTask;
        }
    }

    internal class ListLog : IPlannerLog {
        public readonly List<(PlannerPhase phase, string message)> Phases = new();
        public void Phase(PlannerPhase phase, string message, PlannerTarget target = null) { lock (Phases) { Phases.Add((phase, message)); } }
        public void Info(string message) { }
    }

    [TestFixture]
    public class EngineTests {

        private static (PlannerEngine engine, FakeHardware hw, ListLog log, FakeClock clock) Make(PlannerOptions options, List<PlannerTarget> targets,
                                                                                              DateTime start, Func<FakeClock, ISafetySource> safety) {
            var clock = new FakeClock(start);
            var hw = new FakeHardware(clock);
            var log = new ListLog();
            var engine = new PlannerEngine(options, TestData.Selector(options), () => targets, hw, safety(clock), clock, log) {
                WatchDelay = (d, t) => Task.Delay(1, t) // poll the scripted monitor quickly in real time
            };
            return (engine, hw, log, clock);
        }

        private static async Task RunUntil(PlannerEngine engine, Func<bool> done, int seconds = 20) {
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (!done() && !run.IsCompleted && DateTime.UtcNow < deadline) { await Task.Delay(5); }
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }

        [Test]
        public async Task Without_safety_runs_begin_targets_end_once() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var (engine, hw, log, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, new List<PlannerTarget> { t1 },
                TestData.Night(21), c => new ScriptedSafety(c, false));
            await engine.RunAsync(CancellationToken.None);

            hw.Calls.Should().Equal("connect", "stage 1", "target T1 at 21:01", "stage 4");
            t1.DoneFrames.Should().Be(3);
            log.Phases.Last().phase.Should().Be(PlannerPhase.Finished);
        }

        [Test]
        public async Task With_safety_waits_for_safe_before_begin() {
            var t1 = TestData.Circumpolar("T1", frames: 2, exposure: 60);
            var (engine, hw, log, _) = Make(new PlannerOptions(), new List<PlannerTarget> { t1 },
                TestData.Night(20), c => new ScriptedSafety(c, false).At(TestData.Night(21), true).At(TestData.Night(23), false));
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight));

            log.Phases.First(p => p.phase != PlannerPhase.Connecting).phase.Should().Be(PlannerPhase.WaitingForSafe);
            hw.Calls.Should().StartWith(new[] { "connect", "stage 1", "target T1 at 21:01", "stage 4" });
            log.Phases.Should().Contain(p => p.message == "4 End (finished)");
        }

        [Test]
        public async Task Unsafe_interrupts_runs_end_then_resumes_the_same_target_when_safe() {
            var t1 = TestData.Circumpolar("T1", frames: 20, exposure: 300); // 100 min of frames
            var (engine, hw, log, _) = Make(new PlannerOptions(), new List<PlannerTarget> { t1 }, TestData.Night(21),
                c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false).At(TestData.Night(22, 30), true));
            await RunUntil(engine, () => t1.IsComplete && hw.Calls.Count(c => c == "stage 4") >= 2);

            var expected = new[] { "connect", "stage 1", "target T1 at 21:01", "stage 4", "stage 1" };
            hw.Calls.Take(5).Should().Equal(expected);
            hw.Calls[5].Should().StartWith("target T1 at 22:").And.NotBe("target T1 at 21:01");
            log.Phases.Should().Contain(p => p.message == "4 End (unsafe)");
            t1.DoneFrames.Should().Be(20, "progress continued after the interruption, not restarted");
        }

        [Test]
        public async Task The_users_night_waits_5_minutes_without_parking_then_ends_after_04_00() {
            var t1 = TestData.Circumpolar("Target 1", frames: 1000, exposure: 300);
            t1.End = TestData.At(1, 0);
            var t2 = TestData.Circumpolar("Target 2", frames: 1000, exposure: 300);
            t2.Start = TestData.At(1, 5);
            t2.End = TestData.At(4, 0);
            var (engine, hw, log, _) = Make(new PlannerOptions(), new List<PlannerTarget> { t1, t2 }, TestData.Night(23),
                c => new ScriptedSafety(c, true).At(TestData.Night(6), false));
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight), 30);

            hw.Calls.Should().Contain(c => c.StartsWith("target Target 1"));
            hw.Calls.Should().Contain("target Target 2 at 01:05");
            hw.Calls.Should().NotContain(c => c.StartsWith("gap"), "a 5 minute wait is shorter than the 30 minute threshold");
            hw.Calls.Last().Should().Be("stage 4");
            log.Phases.Should().Contain(p => p.phase == PlannerPhase.WaitingBetweenTargets && p.message.Contains("Target 2"));
            log.Phases.Should().Contain(p => p.message == "4 End (finished)");
        }

        [Test]
        public async Task A_long_wait_parks_and_unparks_5_minutes_before_the_next_target() {
            var t1 = TestData.Circumpolar("Target 1", frames: 1000, exposure: 300);
            t1.End = TestData.At(1, 0);
            var t2 = TestData.Circumpolar("Target 2", frames: 2, exposure: 300);
            t2.Start = TestData.At(2, 0);
            var options = new PlannerOptions { GapMinutes = 30, GapMount = GapMountAction.StopTrackingAndPark, GapCloseDome = true };
            var (engine, hw, log, _) = Make(options, new List<PlannerTarget> { t1, t2 }, TestData.Night(0, 30),
                c => new ScriptedSafety(c, true).At(TestData.Night(6), false));
            await RunUntil(engine, () => t2.IsComplete, 30);

            var gapIndex = hw.Calls.FindIndex(c => c == "gap StopGuiding,StopTracking,Park,CloseDome");
            gapIndex.Should().BeGreaterThan(0);
            hw.Calls[gapIndex + 1].Should().Be("gap OpenDome,Unpark");
            hw.Calls[gapIndex + 2].Should().Be("target Target 2 at 02:00");
        }

        [Test]
        public async Task A_target_that_takes_no_frames_is_skipped_for_the_night() {
            var t1 = TestData.Circumpolar("Broken", frames: 5, exposure: 60);
            var t2 = TestData.Circumpolar("Good", frames: 1, exposure: 60);
            var (engine, hw, _, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, new List<PlannerTarget> { t1, t2 },
                TestData.Night(21), c => new ScriptedSafety(c, false));
            hw.FailFrames = true;
            var run = engine.RunAsync(CancellationToken.None);
            await Task.Delay(200);
            hw.FailFrames = false;
            await run;

            hw.Calls.Count(c => c.StartsWith("target Broken")).Should().Be(1, "it is not retried all night");
            hw.Calls.Should().Contain(c => c.StartsWith("target Good"));
        }

        [Test]
        public async Task Stop_cancels_without_running_end() {
            var t1 = TestData.Circumpolar("T1", frames: 1000, exposure: 300);
            var (engine, hw, log, _) = Make(new PlannerOptions(), new List<PlannerTarget> { t1 }, TestData.Night(21), c => new ScriptedSafety(c, true));
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);
            while (!hw.Calls.Any(c => c.StartsWith("target"))) { await Task.Delay(5); }
            cts.Cancel();
            Func<Task> act = async () => await run;
            await act.Should().ThrowAsync<OperationCanceledException>();
            hw.Calls.Should().NotContain("stage 4");
        }
    }
}
