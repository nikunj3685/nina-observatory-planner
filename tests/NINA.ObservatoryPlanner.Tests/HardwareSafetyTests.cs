using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Dome;
using NINA.Sequencer.SequenceItem.Telescope;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>
    /// Hardware-safety rules a reviewer asked to be enforced and tested: the dome is never closed on a mount whose park failed, a lost
    /// safety monitor counts as unsafe while paused, mount recovery obeys the weather and Stop, and a cancelled AutoHome is stopped.
    /// </summary>
    [TestFixture]
    public class ParkBeforeDomeTests {

        private static readonly ISet<GapStep> Needed = new HashSet<GapStep> { GapStep.Park, GapStep.CloseDome };
        private static IReadOnlyList<GapStep> CloseUpSteps => DomeSafety.CloseUpSteps(true, true, false);

        private sealed class Result {
            public readonly List<GapStep> Ran = new();
            public readonly List<string> Log = new();
            public int RoofLeftOpen;
            public bool ParkFailed;
            public bool Ok;
        }

        private static async Task<Result> Run(IEnumerable<GapStep> steps, Func<GapStep, bool> works, bool parkFailedBefore = false) {
            var r = new Result { ParkFailed = parkFailedBefore };
            r.Ok = await ShutdownSteps.Run("Closing up", steps, Needed, step => { r.Ran.Add(step); return Task.FromResult(works(step)); },
                () => r.ParkFailed, failed => r.ParkFailed = failed, r.Log.Add, () => r.RoofLeftOpen++);
            return r;
        }

        [Test]
        public async Task A_failed_park_never_reaches_the_dome() {
            var r = await Run(CloseUpSteps, step => step != GapStep.Park);
            r.Ran.Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park);
            r.Ran.Should().NotContain(GapStep.CloseDome);
            r.Ok.Should().BeFalse("the close-up did not finish, so 4 End runs");
            r.RoofLeftOpen.Should().Be(1, "the panel is told that the roof was left open");
            r.ParkFailed.Should().BeTrue();
        }

        [Test]
        public async Task A_park_that_works_is_followed_by_closing_the_dome() {
            var r = await Run(CloseUpSteps, _ => true);
            r.Ran.Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park, GapStep.CloseDome);
            r.Ok.Should().BeTrue();
            r.RoofLeftOpen.Should().Be(0);
            r.ParkFailed.Should().BeFalse();
        }

        [Test]
        public async Task A_park_failure_from_before_keeps_the_dome_open_until_a_park_works() {
            var skipped = await Run(new[] { GapStep.CloseDome }, _ => true, parkFailedBefore: true);
            skipped.Ran.Should().BeEmpty();
            skipped.RoofLeftOpen.Should().Be(1);

            var parked = await Run(new[] { GapStep.Park, GapStep.CloseDome }, _ => true, parkFailedBefore: true);
            parked.Ran.Should().Equal(GapStep.Park, GapStep.CloseDome);
            parked.ParkFailed.Should().BeFalse("a park that works clears the failure");
        }

        [Test]
        public async Task The_gap_steps_between_targets_follow_the_same_rule() {
            var steps = GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: true).Wait;
            var r = await Run(steps, step => step != GapStep.Park);
            r.Ran.Should().NotContain(GapStep.CloseDome);
        }

        [Test]
        public async Task A_failed_dome_close_after_a_good_park_is_reported_as_before() {
            var r = await Run(CloseUpSteps, step => step != GapStep.CloseDome);
            r.Ran.Should().EndWith(new[] { GapStep.Park, GapStep.CloseDome });
            r.Ok.Should().BeFalse();
            r.RoofLeftOpen.Should().Be(0);
        }

        // ---------- the real 4 End run in NINA's own container ----------

        private sealed class Mark : SequenceItem {
            private readonly List<string> ran;
            public Mark(string name, List<string> ran) { Name = name; this.ran = ran; }
            public override object Clone() => new Mark(Name, ran) { Status = Status };
            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) { ran.Add(Name); return Task.CompletedTask; }
        }

        private static Mock<IDomeMediator> Dome() {
            var m = new Mock<IDomeMediator>();
            m.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetShutter = true, ShutterStatus = ShutterState.ShutterOpen });
            m.Setup(d => d.CloseShutter(It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));
            return m;
        }

        private static Mock<ITelescopeMediator> Mount(bool parks) {
            var m = new Mock<ITelescopeMediator>();
            m.Setup(t => t.GetInfo()).Returns(new TelescopeInfo { Connected = true, CanPark = true, AtPark = false });
            m.Setup(t => t.ParkTelescope(It.IsAny<IProgress<ApplicationStatus>>(), It.IsAny<CancellationToken>())).Returns(Task.FromResult(parks));
            return m;
        }

        private static (NinaPlannerHardware hw, PlannerService planner, ObservatoryPlannerContainer block, string root) Hardware(
            Mock<IDomeMediator> dome, Mock<ITelescopeMediator> mount, List<string> ran, bool withPark = true) {
            var root = Path.Combine(Path.GetTempPath(), "op-park-" + Guid.NewGuid().ToString("N"));
            var planner = new PlannerService(null, root);
            var end = new List<ISequenceItem> { new Mark("Stop Guiding", ran) };
            if (withPark) { end.Add(new ParkScope(mount.Object, new Mock<IGuiderMediator>().Object) { Name = "Park Scope" }); }
            end.Add(new CloseDomeShutter(dome.Object) { Name = "Close Dome Shutter" });
            end.Add(new Mark("Warm Camera", ran));
            var block = WorkflowLibrary.Assemble(planner, begin: Array.Empty<ISequenceItem>(), targetStart: Array.Empty<ISequenceItem>(),
                triggers: Array.Empty<NINA.Sequencer.Trigger.ISequenceTrigger>(), end: end.ToArray());
            return (new NinaPlannerHardware(block, planner, new Progress<ApplicationStatus>()), planner, block, root);
        }

        private static void Clean(string root) { try { Directory.Delete(root, true); } catch (IOException) { } }

        [Test]
        public async Task The_real_4_End_does_not_close_the_dome_when_Park_Scope_fails_and_still_runs_the_rest() {
            var ran = new List<string>();
            var dome = Dome();
            var (hw, planner, block, root) = Hardware(dome, Mount(parks: false), ran);
            try {
                await hw.RunStage(StageKind.End, CancellationToken.None);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Never, "the mount may not be clear of the roof");
                ran.Should().Equal("Stop Guiding", "Warm Camera");
                block.Stage(StageKind.End).Items.OfType<CloseDomeShutter>().Single().Status.Should().Be(SequenceEntityStatus.SKIPPED);
                block.Stage(StageKind.End).Items.OfType<ParkScope>().Single().Status.Should().Be(SequenceEntityStatus.FAILED);
                planner.ParkFailedNow().Should().BeTrue();
                planner.EndProblems.Should().Contain("NOT closed");
            } finally { Clean(root); }
        }

        [Test]
        public async Task The_real_4_End_closes_the_dome_after_a_good_park() {
            var ran = new List<string>();
            var dome = Dome();
            var (hw, planner, _, root) = Hardware(dome, Mount(parks: true), ran);
            try {
                await hw.RunStage(StageKind.End, CancellationToken.None);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Once);
                ran.Should().Equal("Stop Guiding", "Warm Camera");
                planner.ParkFailedNow().Should().BeFalse();
            } finally { Clean(root); }
        }

        [Test]
        public async Task The_real_4_End_after_a_failed_close_up_park_tries_the_park_again_before_the_dome() {
            var ran = new List<string>();
            var dome = Dome();
            var (hw, planner, _, root) = Hardware(dome, Mount(parks: true), ran);
            try {
                planner.NoteParkFailed(); // the close-up's park failed
                await hw.RunStage(StageKind.End, CancellationToken.None);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Once, "4 End parks the mount again, and it worked");
            } finally { Clean(root); }
        }

        [Test]
        public async Task The_real_4_End_after_a_failed_park_and_with_no_park_step_leaves_the_dome_open() {
            var ran = new List<string>();
            var dome = Dome();
            var (hw, planner, _, root) = Hardware(dome, Mount(parks: true), ran, withPark: false);
            try {
                planner.NoteParkFailed();
                await hw.RunStage(StageKind.End, CancellationToken.None);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Never);
                ran.Should().Equal("Stop Guiding", "Warm Camera");
            } finally { Clean(root); }
        }

        [Test]
        public async Task The_real_4_End_after_a_failed_close_up_park_and_a_failed_second_park_leaves_the_dome_open() {
            var ran = new List<string>();
            var dome = Dome();
            var (hw, planner, _, root) = Hardware(dome, Mount(parks: false), ran);
            try {
                planner.NoteParkFailed();
                await hw.RunStage(StageKind.End, CancellationToken.None);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Never);
            } finally { Clean(root); }
        }

        [Test]
        public void A_mount_that_reports_parked_clears_an_old_park_failure() {
            var planner = new PlannerService(null, Path.Combine(Path.GetTempPath(), "op-park-" + Guid.NewGuid().ToString("N")));
            planner.NoteParkFailed();
            planner.ParkFailedNow().Should().BeTrue();
            planner.NoteParked();
            planner.ParkFailedNow().Should().BeFalse();
        }
    }

    [TestFixture]
    public class PausedSafetyTests {

        [TestCase(true, true, true, false)]
        [TestCase(true, true, false, true)]
        [TestCase(true, false, false, true, TestName = "A lost safety monitor is not safe")]
        [TestCase(true, false, true, true, TestName = "A disconnected monitor is not safe even when its last reading was safe")]
        [TestCase(false, false, false, false, TestName = "Manual does not watch")]
        public void While_paused_a_lost_safety_source_runs_4_End_like_an_unsafe_reading(bool usesSafety, bool connected, bool safe, bool shutDown) =>
            PausedSafety.MustShutDown(usesSafety, new SafetyState(connected, safe)).Should().Be(shutDown);
    }

    [TestFixture]
    public class AutoHomeStopTests {

        private sealed class Api : IAutoHomeApi {
            public int Stops, Starts;
            public bool Running = true, CanHome = true, StartThrows, ThrowOnPoll;
            public string Error;
            public bool CanHomeSensors => CanHome;
            public bool IsAutoHomeRunning { get { if (ThrowOnPoll) { throw new InvalidOperationException("com"); } return Running; } }
            public string LastAutoHomeError => Error;
            public void AutoHomeStart(int degreeLimit, int direction) { Starts++; if (StartThrows) { throw new InvalidOperationException("start"); } }
            public void AutoHomeStop() { Stops++; Running = false; }
        }

        private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);
        private static Task<string> Run(Api api, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken token, Func<DateTime> now = null) =>
            GssAutoHome.Run(api, delay, Limit, now ?? (() => DateTime.UtcNow), token);

        /// <summary>A wait that is over at once, except the n-th one, which lasts until the token is cancelled (the user pressed Stop).</summary>
        private static Func<TimeSpan, CancellationToken, Task> BlockOnWait(int n, Action reached = null) {
            var calls = 0;
            return (d, t) => {
                if (++calls < n) { return Task.CompletedTask; }
                reached?.Invoke();
                return Task.Delay(Timeout.Infinite, t);
            };
        }

        [Test]
        public async Task Cancelling_during_the_first_wait_stops_the_homing() {
            var api = new Api();
            using var cts = new CancellationTokenSource();
            var run = Run(api, BlockOnWait(1, () => cts.Cancel()), cts.Token);
            await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
            api.Starts.Should().Be(1);
            api.Stops.Should().Be(1, "the mount must not keep homing by itself");
        }

        [Test]
        public async Task Cancelling_during_the_polling_wait_stops_the_homing() {
            var api = new Api();
            using var cts = new CancellationTokenSource();
            var run = Run(api, BlockOnWait(2, () => cts.Cancel()), cts.Token);
            await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
            api.Stops.Should().Be(1);
        }

        [Test]
        public async Task Cancelling_between_two_polls_stops_the_homing() {
            var api = new Api();
            using var cts = new CancellationTokenSource();
            var calls = 0;
            var run = Run(api, (d, t) => { if (++calls == 2) { cts.Cancel(); } return Task.CompletedTask; }, cts.Token);
            await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
            api.Stops.Should().Be(1);
        }

        [Test]
        public async Task A_token_cancelled_before_the_start_does_not_leave_the_homing_running() {
            var api = new Api();
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var run = Run(api, (d, t) => Task.Delay(d, t), cts.Token);
            await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
            api.Stops.Should().Be(api.Starts, "whatever was started is stopped");
        }

        [Test]
        public async Task The_time_limit_stops_the_homing() {
            var api = new Api();
            var now = DateTime.UtcNow;
            var calls = 0;
            var result = await Run(api, (d, t) => { calls++; now += TimeSpan.FromMinutes(6); return Task.CompletedTask; }, CancellationToken.None, () => now);
            result.Should().Contain("did not finish within 10 minutes");
            api.Stops.Should().Be(1);
        }

        [Test]
        public async Task A_failing_poll_stops_the_homing_and_reports_the_error() {
            var api = new Api { ThrowOnPoll = true };
            await FluentActions.Awaiting(() => Run(api, (d, t) => Task.CompletedTask, CancellationToken.None)).Should().ThrowAsync<InvalidOperationException>();
            api.Stops.Should().Be(1);
        }

        [Test]
        public async Task A_homing_that_finishes_is_not_stopped() {
            var api = new Api();
            var polls = 0;
            var result = await Run(api, (d, t) => { if (++polls == 3) { api.Running = false; } return Task.CompletedTask; }, CancellationToken.None);
            result.Should().BeNull();
            api.Stops.Should().Be(0);
        }

        [Test]
        public async Task A_homing_that_finished_with_an_error_returns_it_without_a_stop() {
            var api = new Api { Running = false, Error = "ErrQueueFailed" };
            (await Run(api, (d, t) => Task.CompletedTask, CancellationToken.None)).Should().Be("ErrQueueFailed");
            api.Stops.Should().Be(0);
        }

        [Test]
        public async Task A_mount_without_home_sensors_is_not_started() {
            var api = new Api { CanHome = false };
            (await Run(api, (d, t) => Task.CompletedTask, CancellationToken.None)).Should().Contain("no home sensors");
            api.Starts.Should().Be(0);
            api.Stops.Should().Be(0);
        }
    }

    [TestFixture]
    public class RecoveryObeysSafetyTests {

        /// <summary>The mount is lost on the first frame; the recovery then runs until it is cancelled (an AutoHome that takes minutes).</summary>
        private sealed class SlowRecoveryHardware : IPlannerHardware {
            private readonly FakeClock clock;
            private readonly List<string> calls = new();
            public volatile bool Unknown;
            public volatile bool RecoveryHangs = true;
            public volatile bool RecoveryCancelled;
            public Action OnFrame;
            public SlowRecoveryHardware(FakeClock clock) { this.clock = clock; }
            private void Add(string c) { lock (calls) { calls.Add(c); } }
            public string[] Calls { get { lock (calls) { return calls.ToArray(); } } }

            public Task ConnectKeptDevices(CancellationToken token) { Add("connect"); return Task.CompletedTask; }
            public async Task RunStage(StageKind stage, CancellationToken token) {
                Add($"stage {(int)stage}");
                await Task.Delay(5, stage == StageKind.End ? CancellationToken.None : token);
            }
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => Task.CompletedTask;
            public bool MountPositionUnknown => Unknown;
            public async Task<bool> RecoverMount(CancellationToken token) {
                Add("recover");
                if (RecoveryHangs) {
                    try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { RecoveryCancelled = true; Add("recovery cancelled"); throw; }
                }
                Unknown = false;
                Add("unpark");
                return true;
            }
            public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                Add($"target {target.Name}");
                var frames = 0;
                PlannerExposure last = null;
                while (ExposurePlanner.Next(target, last) is { } e) {
                    token.ThrowIfCancellationRequested();
                    if (!canStartFrame(e)) { break; }
                    OnFrame?.Invoke();
                    await Task.Delay(15, token);
                    clock.Advance(TimeSpan.FromSeconds(e.ExposureTime));
                    e.Done++; frames++; last = e;
                }
                return frames;
            }
        }

        private SlowRecoveryHardware hw;
        private SwitchedSafety safety;
        private PlannerEngine engine;

        private PlannerEngine Make(RunMode mode) {
            var options = new PlannerOptions { RunMode = mode, SafeDelaySeconds = 0 };
            var clock = new FakeClock(TestData.Night(21));
            hw = new SlowRecoveryHardware(clock);
            safety = new SwitchedSafety();
            var list = new List<PlannerTarget> { TestData.Circumpolar("T", frames: 4, exposure: 60) };
            return engine = new PlannerEngine(options, TestData.Selector(options), () => list, hw, safety, clock, new ListLog()) {
                StepPoll = (d, t) => Task.Delay(2, t), WatchDelay = (d, t) => Task.Delay(2, t)
            };
        }

        private static async Task WaitFor(Func<bool> condition, string what) {
            var until = DateTime.UtcNow.AddSeconds(15);
            while (!condition()) {
                if (DateTime.UtcNow > until) { Assert.Fail($"Timed out waiting for {what}"); }
                await Task.Delay(5);
            }
        }

        private async Task<RunOutcome> Finish(Task<RunOutcome> run) {
            var done = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(15)));
            done.Should().BeSameAs(run, "the run ends");
            return await run;
        }

        [Test]
        public async Task The_weather_turning_unsafe_during_a_mount_recovery_cancels_it_and_runs_4_End() {
            var engine = Make(RunMode.WithSafety);
            var lost = false;
            hw.OnFrame = () => { if (!lost) { lost = true; engine.MountLost(); } };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token);
            await WaitFor(() => hw.Calls.Contains("recover"), "the recovery to start");
            safety.Safe = false;
            await WaitFor(() => hw.RecoveryCancelled, "the recovery to be cancelled");
            await WaitFor(() => hw.Calls.Contains("stage 4"), "4 End");
            hw.Calls.Should().NotContain("unpark", "no unparking and moving the mount in unsafe weather");
            engine.RequestStop();
            (await Finish(run)).Should().Be(RunOutcome.Stopped);
        }

        [Test]
        public async Task Stop_during_a_mount_recovery_cancels_it_and_runs_4_End() {
            var engine = Make(RunMode.WithSafety);
            var lost = false;
            hw.OnFrame = () => { if (!lost) { lost = true; engine.MountLost(); } };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token);
            await WaitFor(() => hw.Calls.Contains("recover"), "the recovery to start");
            engine.RequestStop();
            (await Finish(run)).Should().Be(RunOutcome.Stopped);
            hw.RecoveryCancelled.Should().BeTrue("Stop reaches the recovery");
            hw.Calls.Should().Contain("stage 4");
            hw.Calls.Should().NotContain("unpark");
        }

        [Test]
        public async Task In_Manual_the_recovery_does_not_look_at_the_weather_but_still_obeys_Stop() {
            var engine = Make(RunMode.WithoutSafety);
            var lost = false;
            hw.OnFrame = () => { if (!lost) { lost = true; engine.MountLost(); } };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token);
            await WaitFor(() => hw.Calls.Contains("recover"), "the recovery to start");
            safety.Safe = false; // nobody watches the weather in Manual
            await Task.Delay(100);
            hw.RecoveryCancelled.Should().BeFalse();
            engine.RequestStop();
            (await Finish(run)).Should().Be(RunOutcome.Stopped);
            hw.RecoveryCancelled.Should().BeTrue();
        }

        [Test]
        public async Task Start_sequence_with_an_unknown_mount_position_checks_the_weather_before_recovering() {
            var engine = Make(RunMode.WithSafety);
            hw.Unknown = true;
            safety.Safe = false;
            hw.RecoveryHangs = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token, StartKind.Resume, new PausePoint(null, null, null, null, DateTime.Now, null, false));
            await WaitFor(() => hw.Calls.Contains("stage 4"), "4 End");
            hw.Calls.Should().NotContain("recover", "no homing and unparking while it is unsafe");
            engine.RequestStop();
            (await Finish(run)).Should().Be(RunOutcome.Stopped);
        }

        [Test]
        public async Task Start_sequence_recovers_the_mount_when_it_is_safe() {
            var engine = Make(RunMode.WithSafety);
            hw.Unknown = true;
            hw.RecoveryHangs = false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token, StartKind.Resume, new PausePoint(null, null, null, null, DateTime.Now, null, false));
            await WaitFor(() => hw.Calls.Contains("unpark"), "the recovery");
            engine.RequestStop();
            await Finish(run);
        }

        [Test]
        public async Task Start_sequence_recovery_that_turns_unsafe_is_cancelled_and_runs_4_End() {
            var engine = Make(RunMode.WithSafety);
            hw.Unknown = true;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var run = engine.RunAsync(cts.Token, StartKind.Resume, new PausePoint(null, null, null, null, DateTime.Now, null, false));
            await WaitFor(() => hw.Calls.Contains("recover"), "the recovery to start");
            safety.Safe = false;
            await WaitFor(() => hw.RecoveryCancelled, "the recovery to be cancelled");
            await WaitFor(() => hw.Calls.Contains("stage 4"), "4 End");
            engine.RequestStop();
            (await Finish(run)).Should().Be(RunOutcome.Stopped);
        }
    }

    [TestFixture]
    public class FrameWindowRecheckTests {

        [Test]
        public void The_window_is_asked_again_right_before_the_exposure_and_a_closed_window_stops_the_frame() {
            var options = new PlannerOptions();
            var selector = TestData.Selector(options);
            var target = TestData.Circumpolar("T", frames: 4, exposure: 300);
            target.End = TestData.At(23);
            var exposure = ExposurePlanner.Next(target, null);
            var now = TestData.Night(22, 40);
            var asked = 0;
            Func<PlannerExposure, bool> canStart = e => { asked++; return selector.CanStartFrame(target, now, TimeSpan.FromSeconds(e.ExposureTime) + TimeSpan.FromSeconds(10)); };

            FrameGate.Check(canStart, exposure, frameAllowed: true).Should().Be(FrameGate.Decision.Go, "before the autofocus the frame fits in the window");
            now = TestData.Night(22, 58); // the autofocus and the guiding wait took 18 minutes
            FrameGate.Check(canStart, exposure, frameAllowed: true).Should().Be(FrameGate.Decision.WindowClosed, "the second check sees that the window closed");
            asked.Should().Be(2, "each check asks again instead of remembering the first answer");
        }

        [Test]
        public void A_pause_is_told_apart_from_a_closed_window() {
            var e = new PlannerExposure();
            FrameGate.Check(_ => false, e, frameAllowed: false).Should().Be(FrameGate.Decision.Pausing);
            FrameGate.Check(_ => false, e, frameAllowed: true).Should().Be(FrameGate.Decision.WindowClosed);
            FrameGate.Check(_ => true, e, frameAllowed: true).Should().Be(FrameGate.Decision.Go);
        }
    }

    [TestFixture]
    public class PausedWatchTests {

        private static NinaServices Services(ISafetyMonitorMediator monitor, IDomeMediator dome = null) =>
            new(null, null, null, null, null, null, null, null, null, null, dome, monitor, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        private static async Task<List<StartKind>> Watch(RunMode mode, SafetyMonitorInfo monitorInfo, DomeInfo domeInfo = null, int milliseconds = 400) {
            var monitor = new Mock<ISafetyMonitorMediator>();
            monitor.Setup(m => m.GetInfo()).Returns(monitorInfo);
            var dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(domeInfo ?? new DomeInfo { Connected = true, ShutterStatus = ShutterState.ShutterOpen });
            var root = Path.Combine(Path.GetTempPath(), "op-pause-" + Guid.NewGuid().ToString("N"));
            var planner = new PlannerService(Services(monitor.Object, dome.Object), root);
            try {
                planner.Options.RunMode = mode;
                planner.Options.KeepConnected.Clear(); // no device to reconnect: the watch goes straight to the weather
                planner.PauseWatchInterval = TimeSpan.FromMilliseconds(20);
                var started = new List<StartKind>();
                planner.RunStarter = k => { lock (started) { started.Add(k); } };
                typeof(PlannerService).GetProperty(nameof(PlannerService.IsPaused)).GetSetMethod(true).Invoke(planner, new object[] { true });
                planner.StartPausedWatchForTest();
                await Task.Delay(milliseconds);
                typeof(PlannerService).GetProperty(nameof(PlannerService.IsPaused)).GetSetMethod(true).Invoke(planner, new object[] { false });
                lock (started) { return started.ToList(); }
            } finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Test]
        public async Task A_paused_run_with_a_safety_monitor_that_is_not_connected_runs_4_End() =>
            (await Watch(RunMode.WithSafety, new SafetyMonitorInfo { Connected = false })).Should().Contain(StartKind.WeatherWhilePaused);

        [Test]
        public async Task A_paused_run_with_an_unsafe_reading_runs_4_End() =>
            (await Watch(RunMode.WithSafety, new SafetyMonitorInfo { Connected = true, IsSafe = false })).Should().Contain(StartKind.WeatherWhilePaused);

        [Test]
        public async Task A_paused_run_with_a_safe_monitor_stays_paused() =>
            (await Watch(RunMode.WithSafety, new SafetyMonitorInfo { Connected = true, IsSafe = true })).Should().BeEmpty();

        [Test]
        public async Task Manual_does_not_watch_the_weather_while_paused() =>
            (await Watch(RunMode.WithoutSafety, new SafetyMonitorInfo { Connected = false })).Should().BeEmpty();

        [Test]
        public async Task Run_with_dome_pauses_on_a_dome_that_is_not_connected_or_not_open() {
            (await Watch(RunMode.WithDome, new SafetyMonitorInfo { Connected = true, IsSafe = true }, new DomeInfo { Connected = false })).Should().Contain(StartKind.WeatherWhilePaused);
            (await Watch(RunMode.WithDome, new SafetyMonitorInfo { Connected = true, IsSafe = true }, new DomeInfo { Connected = true, ShutterStatus = ShutterState.ShutterClosing })).Should().Contain(StartKind.WeatherWhilePaused);
            (await Watch(RunMode.WithDome, new SafetyMonitorInfo { Connected = false }, new DomeInfo { Connected = true, ShutterStatus = ShutterState.ShutterOpen })).Should().BeEmpty("the safety monitor is not used with Run with dome");
        }
    }
}
