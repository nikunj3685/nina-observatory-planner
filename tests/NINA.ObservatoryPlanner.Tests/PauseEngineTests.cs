using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Hardware for the pause tests: frames take a little real time so the test can press Pause or Stop during one.</summary>
    internal class PauseHardware : IPlannerHardware {
        private readonly FakeClock clock;
        public readonly List<string> Calls = new();
        public (double RaHours, double DecDeg)? Mount { get; set; } = (2, 80);
        public List<string> Missing { get; } = new();
        /// <summary>Called when a frame starts, with the number of frames of this target taken before it.</summary>
        public Action<int> OnFrame { get; set; }
        public Action<StageKind> OnStage { get; set; }

        public PauseHardware(FakeClock clock) { this.clock = clock; }

        private void Add(string c) { lock (Calls) { Calls.Add(c); } }
        public string[] Snapshot() { lock (Calls) { return Calls.ToArray(); } }

        public Task ConnectKeptDevices(CancellationToken token) { Add("connect"); return Task.CompletedTask; }

        public async Task RunStage(StageKind stage, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            Add($"stage {(int)stage}");
            OnStage?.Invoke(stage);
            await Task.Delay(5, stage == StageKind.End ? CancellationToken.None : token);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        public Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            Add($"target {target.Name}");
            return Frames(target, canStartFrame, token);
        }

        public Task<int> ResumeTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            Add($"resume {target.Name}");
            return Frames(target, canStartFrame, token);
        }

        private async Task<int> Frames(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
            var frames = 0;
            PlannerExposure last = null;
            while (ExposurePlanner.Next(target, last) is { } e) {
                token.ThrowIfCancellationRequested();
                if (!canStartFrame(e)) { break; }
                OnFrame?.Invoke(target.DoneFrames);
                await Task.Delay(40, token); // the exposure
                clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                e.Done++;
                frames++;
                last = e;
            }
            return frames;
        }

        public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) { Add("gap"); return Task.CompletedTask; }
        public Task<IReadOnlyList<string>> CheckConnections(CancellationToken token) => Task.FromResult<IReadOnlyList<string>>(Missing.ToList());
        public (double RaHours, double DecDeg)? MountPosition() => Mount;
        public Task StopGuiding(CancellationToken token) { Add("stop guiding"); return Task.CompletedTask; }
    }

    [TestFixture]
    public class PauseEngineTests {
        private SwitchedSafety safety;
        private PauseHardware hw;
        private ListLog log;
        private List<PlannerTarget> targets;

        private PlannerEngine Make(PlannerOptions options = null, params PlannerTarget[] list) {
            options ??= new PlannerOptions();
            var clock = new SlowClock(TestData.Night(21));
            safety = new SwitchedSafety();
            hw = new PauseHardware(clock.Inner);
            log = new ListLog();
            targets = list.ToList();
            return new PlannerEngine(options, TestData.Selector(options), () => targets, hw, safety, clock, log) { WatchDelay = (d, t) => Task.Delay(1, t) };
        }

        private static async Task<RunOutcome> Run(PlannerEngine engine, StartKind start = StartKind.Normal, PausePoint point = null, int seconds = 20) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            return await engine.RunAsync(cts.Token, start, point);
        }

        [Test]
        public async Task Pause_after_this_frame_lets_the_frame_finish_then_stops_guiding_and_keeps_everything_on() {
            var t = TestData.Circumpolar("T", frames: 5, exposure: 60);
            var engine = Make(null, t);
            hw.OnFrame = done => { if (done == 1) { engine.RequestPause(PauseKind.AfterFrame); } };
            var outcome = await Run(engine);
            outcome.Should().Be(RunOutcome.Paused);
            t.DoneFrames.Should().Be(2, "the frame running when Pause was pressed finished and counted");
            hw.Snapshot().Should().Equal("connect", "stage 1", "target T", "stop guiding");
            engine.Paused.TargetId.Should().Be(t.Id);
            engine.Paused.MountRaHours.Should().Be(2);
            log.Phases.Last().phase.Should().Be(PlannerPhase.Paused);
        }

        [Test]
        public async Task Pause_now_drops_the_frame_being_taken() {
            var t = TestData.Circumpolar("T", frames: 5, exposure: 60);
            var engine = Make(null, t);
            hw.OnFrame = done => { if (done == 1) { _ = Task.Run(async () => { await Task.Delay(10); engine.RequestPause(PauseKind.Now); }); } };
            var outcome = await Run(engine);
            outcome.Should().Be(RunOutcome.Paused);
            t.DoneFrames.Should().Be(1, "the second frame was cut short and is not counted");
            hw.Snapshot().Should().NotContain("stage 4", "a pause never shuts the observatory down");
            hw.Snapshot().Last().Should().Be("stop guiding");
        }

        [Test]
        public async Task Pause_during_1_Begin_waits_for_Begin_then_pauses_before_any_target() {
            var t = TestData.Circumpolar("T", frames: 3);
            var engine = Make(null, t);
            hw.OnStage = stage => { if (stage == StageKind.Begin) { engine.RequestPause(PauseKind.AfterFrame); } }; // pressed while 1 Begin runs (the service turns "now" into "after this step" outside a frame)
            var outcome = await Run(engine);
            outcome.Should().Be(RunOutcome.Paused);
            hw.Snapshot().Should().Equal("connect", "stage 1", "stop guiding");
        }

        [Test]
        public async Task Resume_on_the_same_target_with_the_mount_unmoved_restarts_guiding_only() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            t.Exposures[0].Done = 1;
            var engine = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, t);
            var point = new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21));
            var outcome = await Run(engine, StartKind.Resume, point);
            outcome.Should().Be(RunOutcome.Finished);
            hw.Snapshot().Should().Equal("connect", "resume T", "stage 4");
            t.DoneFrames.Should().Be(3, "frames already taken stay counted");
        }

        [Test]
        public async Task Resume_after_the_mount_moved_runs_2_Start_of_target_again() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, t);
            hw.Mount = (10, 20); // e.g. slewed to a bright star for a manual autofocus
            await Run(engine, StartKind.Resume, new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21)));
            hw.Snapshot().Should().Equal("connect", "target T", "stage 4");
        }

        [Test]
        public async Task Resume_when_another_target_is_now_first_starts_that_target() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var other = TestData.Circumpolar("Other", frames: 1, exposure: 60);
            var engine = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, other, t); // moved above T while paused
            await Run(engine, StartKind.Resume, new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21)));
            hw.Snapshot().Should().StartWith(new[] { "connect", "target Other" });
        }

        [Test]
        public async Task Resume_with_a_device_that_does_not_reconnect_stays_paused_and_says_which() {
            var t = TestData.Circumpolar("T", frames: 2);
            var engine = Make(null, t);
            hw.Missing.Add("Guider");
            var outcome = await Run(engine, StartKind.Resume, new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21)));
            outcome.Should().Be(RunOutcome.Paused);
            engine.PausedReason.Should().Contain("Guider");
            hw.Snapshot().Should().Equal("connect");
            engine.Paused.TargetId.Should().Be(t.Id, "the pause keeps its place");
        }

        [Test]
        public async Task Resume_while_unsafe_runs_4_End_first_then_waits_for_safe_and_starts_from_1_Begin() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var engine = Make(null, t);
            safety.Safe = false;
            _ = Task.Run(async () => { while (!hw.Snapshot().Contains("stage 4")) { await Task.Delay(1); } await Task.Delay(20); safety.Safe = true; });
            var run = Run(engine, StartKind.Resume, new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21)));
            while (!t.IsComplete && !run.IsCompleted) { await Task.Delay(5); }
            engine.RequestStop();
            await run;
            hw.Snapshot().Should().ContainInOrder("connect", "stage 4", "stage 1", "target T");
            hw.Snapshot().Should().NotContain("resume T", "after a shutdown the mount was parked: a full 2 Start of target runs");
        }

        [Test]
        public async Task Unsafe_while_paused_runs_4_End_then_1_Begin_when_safe_and_stays_paused() {
            var t = TestData.Circumpolar("T", frames: 2);
            var engine = Make(null, t);
            safety.Safe = false;
            _ = Task.Run(async () => { while (!hw.Snapshot().Contains("stage 4")) { await Task.Delay(1); } await Task.Delay(20); safety.Safe = true; });
            var point = new PausePoint(t.Id, t.Name, 2, 80, TestData.Night(21));
            var outcome = await Run(engine, StartKind.WeatherWhilePaused, point);
            outcome.Should().Be(RunOutcome.Paused);
            hw.Snapshot().Should().Equal("connect", "stage 4", "stage 1");
            engine.Paused.TargetId.Should().Be(t.Id);
            t.DoneFrames.Should().Be(0, "nothing is imaged without the user pressing Start sequence");
        }

        [Test]
        public async Task Stop_while_imaging_drops_the_frame_runs_4_End_and_ends_the_run() {
            var t = TestData.Circumpolar("T", frames: 5, exposure: 60);
            var engine = Make(null, t);
            hw.OnFrame = done => { if (done == 1) { _ = Task.Run(async () => { await Task.Delay(10); engine.RequestStop(); }); } };
            var outcome = await Run(engine);
            outcome.Should().Be(RunOutcome.Stopped);
            hw.Snapshot().Should().Equal("connect", "stage 1", "target T", "stage 4");
            t.DoneFrames.Should().Be(1);
            log.Phases.Should().Contain(p => p.message == "4 End (stopped)");
        }

        [Test]
        public async Task Stop_while_paused_runs_4_End() {
            var engine = Make(null, TestData.Circumpolar("T"));
            var outcome = await Run(engine, StartKind.StopFromPause);
            outcome.Should().Be(RunOutcome.Stopped);
            hw.Snapshot().Should().Equal("connect", "stage 4");
        }

        [Test]
        public async Task Stop_while_waiting_for_safe_ends_without_running_4_End_again() {
            var engine = Make(null, TestData.Circumpolar("T"));
            safety.Safe = false;
            var run = Run(engine);
            while (!log.Phases.Any(p => p.phase == PlannerPhase.WaitingForSafe)) { await Task.Delay(1); }
            engine.RequestStop();
            (await run).Should().Be(RunOutcome.Stopped);
            hw.Snapshot().Should().Equal("connect");
        }

        [Test]
        public async Task Pause_pressed_then_unsafe_shuts_down_and_pauses_after_1_Begin_when_safe() {
            var t = TestData.Circumpolar("T", frames: 5, exposure: 60);
            var engine = Make(null, t);
            hw.OnFrame = done => {
                if (done != 1) { return; }
                engine.RequestPause(PauseKind.AfterFrame);
                safety.Safe = false; // and then the clouds come
                _ = Task.Run(async () => { while (!hw.Snapshot().Contains("stage 4")) { await Task.Delay(1); } await Task.Delay(20); safety.Safe = true; });
            };
            var outcome = await Run(engine);
            outcome.Should().Be(RunOutcome.Paused);
            hw.Snapshot().Should().ContainInOrder("stage 1", "target T", "stage 4", "stage 1", "stop guiding");
            hw.Snapshot().Count(c => c == "target T").Should().Be(1, "it does not start imaging again before pausing");
        }

        [Test]
        public async Task Cancel_pause_before_it_happens_keeps_imaging() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var engine = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, t);
            engine.RequestPause(PauseKind.AfterFrame);
            engine.CancelPause();
            (await Run(engine)).Should().Be(RunOutcome.Finished);
            t.IsComplete.Should().BeTrue();
        }
    }

    [TestFixture]
    public class MeridianGuardTests {
        [Test]
        public void Warns_shortly_before_the_flip_and_stops_tracking_once_it_is_reached() {
            MeridianGuard.Check(tracking: true, hoursToFlip: 2).Should().Be(MeridianGuard.Action.None);
            MeridianGuard.Check(true, 0.2).Should().Be(MeridianGuard.Action.Warn);
            MeridianGuard.Check(true, 0).Should().Be(MeridianGuard.Action.StopTracking);
            MeridianGuard.Check(true, -0.1).Should().Be(MeridianGuard.Action.StopTracking);
            MeridianGuard.Check(false, -0.1).Should().Be(MeridianGuard.Action.None, "a mount that is not tracking can't run into the pier");
            MeridianGuard.Check(true, null).Should().Be(MeridianGuard.Action.None);
        }
    }

    [TestFixture]
    public class ProfileStoreTests {
        private string root;

        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "op-profiles-" + Guid.NewGuid().ToString("N")); }
        [TearDown] public void TearDown() { try { Directory.Delete(root, true); } catch (IOException) { } }

        [Test]
        public void Each_profile_has_its_own_options_and_the_shared_settings_are_carried_over_once() {
            var shared = new PlannerStore(root);
            shared.SaveOptions(new PlannerOptions { GapMinutes = 45 });
            var a = PlannerStore.ForProfile(root, "aaaa");
            a.LoadOptions().GapMinutes.Should().Be(45, "the earlier shared settings come over the first time");
            a.SaveOptions(new PlannerOptions { GapMinutes = 10 });
            var b = PlannerStore.ForProfile(root, "bbbb");
            b.LoadOptions().GapMinutes.Should().Be(45);
            PlannerStore.ForProfile(root, "aaaa").LoadOptions().GapMinutes.Should().Be(10, "profile A keeps its own value");
            a.Root.Should().Be(Path.Combine(root, "Profiles", "aaaa"));
        }

        [Test]
        public void Paused_state_survives_a_restart() {
            var store = PlannerStore.ForProfile(root, "p");
            var id = Guid.NewGuid();
            store.SaveState(PlannerState.From(new PausePoint(id, "M42", 5.5, -5.4, new DateTime(2026, 10, 2, 23, 0, 0))));
            var back = PlannerStore.ForProfile(root, "p").LoadState();
            back.Paused.Should().BeTrue();
            back.Point.Should().BeEquivalentTo(new { TargetId = (Guid?)id, TargetName = "M42", MountRaHours = (double?)5.5, MountDecDeg = (double?)-5.4 });
            store.SaveState(new PlannerState());
            PlannerStore.ForProfile(root, "p").LoadState().Paused.Should().BeFalse();
        }
    }
}
