using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// Item 11: one Pause for the whole run. Pause now stops whatever runs (except a flip, the dome shutter, park and 4 End),
    /// Pause after this step lets the running step finish, an interrupted 1 Begin continues from its next step, and Pause
    /// is only possible once 1 Begin has started.
    /// </summary>
    [TestFixture]
    public class GlobalPauseTests {

        /// <summary>A rig whose steps take a few milliseconds of real time, so a pause can land inside one.</summary>
        private sealed class StepHardware : IPlannerHardware {
            private readonly FakeClock clock;
            private readonly List<string> calls = new();
            public int BeginSteps = 4;
            public volatile int BeginDone;
            public volatile object Step;
            public volatile bool Protected;
            public volatile bool Imaging;
            public Action<int> OnBeginStep;
            public Action OnCenter;
            public Action<int> OnFrame;

            public StepHardware(FakeClock clock) { this.clock = clock; }
            private void Add(string c) { lock (calls) { calls.Add(c); } }
            public string[] Calls { get { lock (calls) { return calls.ToArray(); } } }

            public Task ConnectKeptDevices(CancellationToken token) { Add("connect"); return Task.CompletedTask; }
            public Task RunStage(StageKind stage, CancellationToken token) => ContinueStage(stage, 0, token);

            public async Task ContinueStage(StageKind stage, int done, CancellationToken token) {
                if (stage != StageKind.Begin) {
                    Add($"stage {(int)stage}");
                    await Task.Delay(5, stage == StageKind.End ? CancellationToken.None : token);
                    return;
                }
                Add($"begin from step {done + 1}");
                BeginDone = done;
                for (var i = done; i < BeginSteps; i++) {
                    Step = $"begin step {i + 1}";
                    try {
                        OnBeginStep?.Invoke(i + 1);
                        await Task.Delay(30, token);
                    } finally { Step = null; }
                    BeginDone = i + 1;
                }
            }

            public int FinishedSteps(StageKind stage) => stage == StageKind.Begin ? BeginDone : 0;
            public object CurrentStep() => Step;
            public bool InProtectedStep() => Protected;
            public bool ImagingStarted(PlannerTarget target) => Imaging;
            public (double RaHours, double DecDeg)? MountPosition() => (2, 80);
            public Task StopGuiding(CancellationToken token) { Add("stop guiding"); return Task.CompletedTask; }
            public Task<IReadOnlyList<string>> CheckConnections(CancellationToken token) { Add("check connections"); return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()); }
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) { Add("gap " + string.Join(",", steps)); return Task.CompletedTask; }

            public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                Imaging = false;
                Add($"target {target.Name}");
                Step = "center";
                try {
                    OnCenter?.Invoke();
                    await Task.Delay(30, token);
                } finally { Step = null; }
                Imaging = true;
                return await Frames(target, canStartFrame, token);
            }

            public async Task<int> ResumeTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                Imaging = true;
                Add($"resume {target.Name}");
                return await Frames(target, canStartFrame, token);
            }

            private async Task<int> Frames(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                var frames = 0;
                PlannerExposure last = null;
                while (ExposurePlanner.Next(target, last) is { } e) {
                    token.ThrowIfCancellationRequested();
                    if (!canStartFrame(e)) { break; }
                    OnFrame?.Invoke(target.DoneFrames);
                    await Task.Delay(10, token);
                    clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                    e.Done++;
                    frames++;
                    last = e;
                }
                return frames;
            }
        }

        private StepHardware hw;
        private ListLog log;
        private SwitchedSafety safety;

        private PlannerEngine Make(PlannerOptions options, params PlannerTarget[] targets) {
            var clock = new FakeClock(TestData.Night(21));
            hw = new StepHardware(clock);
            log = new ListLog();
            safety = new SwitchedSafety();
            var list = targets.ToList();
            return new PlannerEngine(options, TestData.Selector(options), () => list, hw, safety, clock, log) {
                WatchDelay = (d, t) => Task.Delay(1, t),
                StepPoll = (d, t) => Task.Delay(2, t)
            };
        }

        private static async Task<RunOutcome> Run(PlannerEngine engine, StartKind start = StartKind.Normal, PausePoint point = null) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await engine.RunAsync(cts.Token, start, point);
        }

        private static PlannerOptions NoSafety() => new() { RunMode = RunMode.WithoutSafety };

        [Test]
        public async Task Pause_now_interrupts_1_Begin_and_Start_sequence_continues_from_the_next_step() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(NoSafety(), t);
            hw.OnBeginStep = step => { if (step == 2) { engine.RequestPause(PauseKind.Now); } };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.Paused.BeginDone.Should().Be(1, "step 2 was interrupted; step 1 had finished");

            hw.OnBeginStep = null;
            (await Run(engine, StartKind.Resume, engine.Paused)).Should().Be(RunOutcome.Finished);
            hw.Calls.Should().ContainInOrder("begin from step 1", "begin from step 2", "target T", "stage 4");
            hw.Calls.Should().NotContain("check connections", "1 Begin connects the devices itself");
            t.IsComplete.Should().BeTrue();
        }

        [Test]
        public async Task Pause_after_this_step_lets_the_running_step_finish() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(NoSafety(), t);
            hw.OnBeginStep = step => { if (step == 2) { engine.RequestPause(PauseKind.AfterFrame); } };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.Paused.BeginDone.Should().Be(2, "step 2 finished, step 3 was not run");
        }

        [Test]
        public async Task A_protected_step_finishes_before_pause_now_takes_effect() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(NoSafety(), t);
            hw.OnBeginStep = step => {
                if (step == 2) {
                    hw.Protected = true; // e.g. the dome shutter is opening
                    engine.RequestPause(PauseKind.Now);
                }
                if (step == 3) { hw.Protected = false; }
            };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.Paused.BeginDone.Should().BeGreaterThanOrEqualTo(2, "the protected step 2 was not interrupted");
        }

        [Test]
        public async Task Pause_is_not_possible_while_waiting_for_safe() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(new PlannerOptions(), t);
            safety.Safe = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = engine.RunAsync(cts.Token);
            while (!log.Phases.ToArray().Any(p => p.phase == PlannerPhase.WaitingForSafe)) { await Task.Delay(2); }
            engine.PauseAllowed.Should().BeFalse();
            engine.RequestPause(PauseKind.Now);
            engine.PausePending.Should().BeFalse("nothing is powered: Stop and Run are used instead");
            engine.RequestStop();
            (await run).Should().Be(RunOutcome.Stopped);
            hw.Calls.Should().NotContain("begin from step 1");
        }

        [Test]
        public async Task Pause_during_2_Start_of_target_runs_it_again_on_resume() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(NoSafety(), t);
            hw.OnCenter = () => engine.RequestPause(PauseKind.Now);
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.Paused.ImagingStarted.Should().BeFalse();
            engine.Paused.TargetId.Should().Be(t.Id);

            hw.OnCenter = null;
            (await Run(engine, StartKind.Resume, engine.Paused)).Should().Be(RunOutcome.Finished);
            hw.Calls.Count(c => c == "target T").Should().Be(2, "centering was interrupted, so 2 Start of target runs again");
            hw.Calls.Should().NotContain("resume T");
            log.Phases.Should().Contain(p => p.message.StartsWith("Resume: 2 Start of target had not finished"));
        }

        [Test]
        public async Task Pause_during_frames_resumes_with_guiding_only() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var engine = Make(NoSafety(), t);
            hw.OnFrame = done => { if (done == 1) { engine.RequestPause(PauseKind.AfterFrame); } };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.Paused.ImagingStarted.Should().BeTrue();
            hw.OnFrame = null;
            await Run(engine, StartKind.Resume, engine.Paused);
            hw.Calls.Should().Contain("resume T");
        }

        [Test]
        public async Task Start_sequence_says_why_it_continues_with_another_target() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var t2 = TestData.Circumpolar("T2", frames: 1, exposure: 60);
            var engine = Make(NoSafety(), t1, t2);
            hw.OnFrame = done => { if (done == 1) { engine.RequestPause(PauseKind.AfterFrame); } };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            t1.Enabled = false; // turned off while paused
            hw.OnFrame = null;
            await Run(engine, StartKind.Resume, engine.Paused);
            log.Phases.Should().Contain(p => p.message.StartsWith("Resume: T1 was turned off; continuing with T2."));
            hw.Calls.Should().ContainInOrder("target T2", "stage 4");
        }

        [Test]
        public void The_pause_point_is_saved_with_1_Begin_progress_and_files_from_1_0_still_load() {
            var p = new PausePoint(null, null, null, null, TestData.Night(21), BeginDone: 3, ImagingStarted: false);
            var back = PlannerStore.Deserialize<PlannerState>(PlannerStore.Serialize(PlannerState.From(p))).Point;
            back.BeginDone.Should().Be(3);
            back.ImagingStarted.Should().BeFalse();

            var old = PlannerStore.Deserialize<PlannerState>("{\"Paused\":true,\"TargetName\":\"M31\",\"MountRaHours\":0.7,\"MountDecDeg\":41.3}").Point;
            old.BeginDone.Should().BeNull();
            old.ImagingStarted.Should().BeFalse("unknown from 1.0: 2 Start of target runs again, which is always safe");
        }
    }
}
