using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>GS Server stops during the night: reconnect, AutoHome, unpark and continue; pause when that fails.</summary>
    [TestFixture]
    public class MountRecoveryTests {

        // ---------- the recovery steps ----------

        private static (MountRecovery recovery, List<string> log) Recovery(Queue<bool> connects, Queue<string> autoHomes, bool unparks = true) {
            var log = new List<string>();
            var r = new MountRecovery(_ => Task.FromResult(connects.Count > 0 ? connects.Dequeue() : true),
                                      _ => Task.FromResult(autoHomes.Count > 0 ? autoHomes.Dequeue() : null),
                                      _ => Task.FromResult(unparks), (d, t) => Task.CompletedTask, log.Add);
            return (r, log);
        }

        [Test]
        public async Task Reconnect_AutoHome_and_unpark_make_the_position_known() {
            var (r, log) = Recovery(new Queue<bool>(new[] { true }), new Queue<string>(new string[] { null }));
            (await r.Run(3, CancellationToken.None)).Should().BeTrue();
            log.Should().ContainInOrder("Mount recovery 1/3: waiting 10 s, then reconnecting the mount", "Mount recovery 1/3: connected; AutoHome with the home sensors",
                "Mount recovery: done; the mount position is known again");
        }

        [Test]
        public async Task A_failed_connect_or_AutoHome_is_tried_again_and_gives_up_after_the_tries() {
            var (r, log) = Recovery(new Queue<bool>(new[] { false, true, true }), new Queue<string>(new[] { "sensor not found", null }));
            (await r.Run(3, CancellationToken.None)).Should().BeTrue();
            log.Should().Contain("Mount recovery 1/3: the mount could not be connected").And.Contain("Mount recovery 2/3: AutoHome failed: sensor not found");

            var (never, log2) = Recovery(new Queue<bool>(), new Queue<string>(new[] { "x", "x" }));
            (await never.Run(2, CancellationToken.None)).Should().BeFalse();
            log2.Last().Should().Be("Mount recovery failed after 2 tries: the mount position is unknown");
        }

        // ---------- the engine ----------

        private sealed class LostMountHardware : IPlannerHardware {
            private readonly FakeClock clock;
            private readonly List<string> calls = new();
            public int BeginSteps = 3;
            public volatile int BeginDone;
            public Action<int> OnBeginStep;
            public Action<int> OnFrame;
            public Action<StageKind> OnStage;
            public bool RecoveryWorks = true;
            public bool Unknown;
            public LostMountHardware(FakeClock clock) { this.clock = clock; }
            private void Add(string c) { lock (calls) { calls.Add(c); } }
            public string[] Calls { get { lock (calls) { return calls.ToArray(); } } }

            public Task ConnectKeptDevices(CancellationToken token) { Add("connect"); return Task.CompletedTask; }
            public Task RunStage(StageKind stage, CancellationToken token) => ContinueStage(stage, 0, token);
            public async Task ContinueStage(StageKind stage, int done, CancellationToken token) {
                OnStage?.Invoke(stage);
                if (stage != StageKind.Begin) { Add($"stage {(int)stage}"); await Task.Delay(30, stage == StageKind.End ? CancellationToken.None : token); return; }
                Add($"begin from step {done + 1}");
                BeginDone = done;
                for (var i = done; i < BeginSteps; i++) {
                    OnBeginStep?.Invoke(i + 1);
                    await Task.Delay(30, token);
                    BeginDone = i + 1;
                }
            }
            public int FinishedSteps(StageKind stage) => BeginDone;
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => Task.CompletedTask;
            public bool MountPositionUnknown => Unknown;
            public Task<bool> RecoverMount(CancellationToken token) { Add("recover"); Unknown = !RecoveryWorks; return Task.FromResult(RecoveryWorks); }
            public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                Add($"target {target.Name}");
                var frames = 0;
                PlannerExposure last = null;
                while (ExposurePlanner.Next(target, last) is { } e) {
                    token.ThrowIfCancellationRequested();
                    if (!canStartFrame(e)) { break; }
                    OnFrame?.Invoke(target.DoneFrames);
                    await Task.Delay(15, token);
                    clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                    e.Done++;
                    frames++;
                    last = e;
                }
                return frames;
            }
        }

        private LostMountHardware hw;
        private ListLog log;

        private PlannerEngine Make(params PlannerTarget[] targets) {
            var options = new PlannerOptions { RunMode = RunMode.WithoutSafety };
            var clock = new FakeClock(TestData.Night(21));
            hw = new LostMountHardware(clock);
            log = new ListLog();
            var list = targets.ToList();
            return new PlannerEngine(options, TestData.Selector(options), () => list, hw, new SwitchedSafety(), clock, log) { StepPoll = (d, t) => Task.Delay(2, t) };
        }

        private static async Task<RunOutcome> Run(PlannerEngine engine, StartKind start = StartKind.Normal, PausePoint point = null) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            return await engine.RunAsync(cts.Token, start, point);
        }

        [Test]
        public async Task Lost_while_imaging_recovers_and_continues_with_2_Start_of_target() {
            var t = TestData.Circumpolar("T", frames: 4, exposure: 60);
            var engine = Make(t);
            var lost = false;
            hw.OnFrame = done => { if (done == 1 && !lost) { lost = true; engine.MountLost(); } };
            (await Run(engine)).Should().Be(RunOutcome.Finished);
            hw.Calls.Should().ContainInOrder("begin from step 1", "target T", "recover", "target T", "stage 4");
            hw.Calls.Count(c => c == "begin from step 1").Should().Be(1, "1 Begin is not repeated");
            t.IsComplete.Should().BeTrue();
        }

        [Test]
        public async Task Lost_in_1_Begin_recovers_and_continues_1_Begin_from_the_next_step() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var engine = Make(t);
            hw.OnBeginStep = step => { if (step == 2 && hw.Calls.Count(c => c == "recover") == 0) { engine.MountLost(); } };
            (await Run(engine)).Should().Be(RunOutcome.Finished);
            hw.Calls.Should().ContainInOrder("begin from step 1", "recover", "begin from step 2", "target T", "stage 4");
        }

        [Test]
        public async Task A_failed_recovery_pauses_and_Start_sequence_tries_again_first() {
            var t = TestData.Circumpolar("T", frames: 4, exposure: 60);
            var engine = Make(t);
            hw.RecoveryWorks = false;
            hw.OnFrame = done => { if (done == 1) { engine.MountLost(); } };
            (await Run(engine)).Should().Be(RunOutcome.Paused);
            engine.PausedReason.Should().Be(PlannerEngine.RecoveryFailed);
            engine.Paused.TargetId.Should().Be(t.Id);
            hw.Calls.Should().NotContain("stage 4", "the run pauses; 4 End does not run");

            hw.OnFrame = null;
            hw.RecoveryWorks = true;
            (await Run(engine, StartKind.Resume, engine.Paused)).Should().Be(RunOutcome.Finished);
            hw.Calls.Skip(hw.Calls.ToList().IndexOf("recover") + 1).Should().ContainInOrder("recover", "target T", "stage 4");
            t.IsComplete.Should().BeTrue();
        }

        [Test]
        public async Task Lost_during_4_End_is_not_recovered() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var engine = Make(t);
            hw.OnStage = stage => { if (stage == StageKind.End) { engine.MountLost(); } };
            (await Run(engine)).Should().Be(RunOutcome.Finished);
            hw.Calls.Should().NotContain("recover");
        }

        [Test]
        public void The_recovery_options_default_to_on_3_tries_and_leaving_the_roof_open() {
            var o = new PlannerOptions();
            o.MountRecovery.Should().BeTrue();
            o.MountRecoveryTries.Should().Be(3);
            o.CloseRoofWhenRecoveryFails.Should().BeFalse();
            o.MountRecoveryTries = 50;
            o.MountRecoveryTries.Should().Be(10);
        }
    }
}
