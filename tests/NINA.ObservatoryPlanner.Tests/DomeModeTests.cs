using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Equipment.MyDome;
using NINA.Equipment.Equipment.MySafetyMonitor;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI;
using NINA.Profile.Interfaces;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Dome;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>A dome shutter that follows a script of (from time, connected, shutter) entries, read the way Run with dome reads it.</summary>
    internal sealed class ScriptedDome : ISafetySource {
        private readonly FakeClock clock;
        private readonly List<(DateTime from, bool connected, DomeShutter shutter)> script = new();
        public ScriptedDome(FakeClock clock, DomeShutter initially, bool connected = true) { this.clock = clock; script.Add((DateTime.MinValue, connected, initially)); }
        public ScriptedDome At(DateTime from, DomeShutter shutter, bool connected = true) { script.Add((from, connected, shutter)); return this; }
        public SafetyState Read() { var s = script.Last(x => x.from <= clock.Now); return DomeSafety.Read(s.connected, s.shutter); }
    }

    /// <summary>Run with dome: the dome shutter is the safety signal, the planner never opens or closes the dome.</summary>
    [TestFixture]
    public class DomeModeTests {

        // ---------- the dome as the safety signal ----------

        [TestCase(true, DomeShutter.Open, true, true)]
        [TestCase(true, DomeShutter.Closed, true, false)]
        [TestCase(true, DomeShutter.Opening, true, false)]
        [TestCase(true, DomeShutter.Closing, true, false)]
        [TestCase(true, DomeShutter.Error, true, false)]
        [TestCase(true, DomeShutter.None, false, false)]
        [TestCase(false, DomeShutter.Open, false, false)]
        [TestCase(false, DomeShutter.Closed, false, false)]
        [TestCase(false, DomeShutter.None, false, false)]
        public void Only_a_connected_dome_with_an_open_shutter_is_safe(bool connected, DomeShutter shutter, bool readable, bool safe) {
            var state = DomeSafety.Read(connected, shutter);
            state.IsSafe.Should().Be(safe);
            state.Connected.Should().Be(readable, "a dome that is not connected or reports no shutter status can't be read");
        }

        [TestCase(true, DomeShutter.Open, "Open")]
        [TestCase(true, DomeShutter.Closed, "Closed")]
        [TestCase(true, DomeShutter.Opening, "Opening")]
        [TestCase(true, DomeShutter.Closing, "Closing")]
        [TestCase(true, DomeShutter.Error, "Shutter error")]
        [TestCase(true, DomeShutter.None, "Status unknown")]
        [TestCase(false, DomeShutter.Open, "Not connected")]
        public void The_chip_text_says_just_what_the_dome_is_doing(bool connected, DomeShutter shutter, string text) =>
            DomeSafety.StateText(connected, shutter).Should().Be(text);

        [Test]
        public void Every_state_has_a_message_and_a_closed_dome_is_not_blamed_on_anyone() {
            foreach (var connected in new[] { true, false }) {
                foreach (var shutter in Enum.GetValues<DomeShutter>()) { DomeSafety.Message(connected, shutter).Should().NotBeNullOrWhiteSpace(); }
            }
            DomeSafety.Message(true, DomeShutter.Closed).Should().NotContain("observatory").And.Be("Waiting for it to open again.");
            DomeSafety.StateText(true, DomeShutter.Closed).Should().NotContain("observatory");
        }

        [Test]
        public void Start_problems_name_a_missing_dome_and_a_dome_without_shutter_status() {
            DomeSafety.StartProblems(domeConfigured: false, domeConnected: false, DomeShutter.None).Should().ContainSingle().Which.Should().Contain("no dome is set up");
            DomeSafety.StartProblems(true, true, DomeShutter.None).Should().ContainSingle().Which.Should().Contain("does not report a shutter status");
            DomeSafety.StartProblems(true, true, DomeShutter.Open).Should().BeEmpty();
            DomeSafety.StartProblems(true, true, DomeShutter.Closed).Should().BeEmpty();
            DomeSafety.StartProblems(true, false, DomeShutter.None).Should().BeEmpty("a dome that is not connected yet can't say what it reports");
        }

        // ---------- the planner never commands the dome ----------

        [Test]
        public void The_close_up_and_the_opening_up_leave_the_dome_alone_when_the_observatory_runs_it() {
            DomeSafety.CloseUpSteps(plannerHandlesDome: false, domeConnected: true, roofMustStayOpen: false)
                .Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park);
            DomeSafety.ReopenSteps(plannerHandlesDome: false, domeConnected: true).Should().Equal(GapStep.Unpark);
        }

        [Test]
        public void When_the_planner_runs_the_dome_the_close_up_and_the_opening_up_are_as_before() {
            DomeSafety.CloseUpSteps(true, true, false).Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park, GapStep.CloseDome);
            DomeSafety.CloseUpSteps(true, false, false).Should().Equal(new[] { GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park }, "no dome connected: nothing to close");
            DomeSafety.CloseUpSteps(true, true, roofMustStayOpen: true).Should().Equal(new[] { GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park }, "the mount position is unknown");
            DomeSafety.ReopenSteps(true, true).Should().Equal(GapStep.OpenDome, GapStep.Unpark);
            DomeSafety.ReopenSteps(true, false).Should().Equal(GapStep.Unpark);
        }

        [Test]
        public void Dome_steps_are_taken_out_of_any_list_of_gap_steps() {
            var all = new[] { GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park, GapStep.FindHome, GapStep.CloseDome, GapStep.OpenDome, GapStep.Unpark };
            DomeSafety.WithoutDomeSteps(all).Should().Equal(GapStep.StopGuiding, GapStep.StopTracking, GapStep.Park, GapStep.FindHome, GapStep.Unpark);
            DomeSafety.WithoutDomeSteps(Array.Empty<GapStep>()).Should().BeEmpty();
        }

        // ---------- the options ----------

        [TestCase(RunMode.WithSafety, true, true, true)]
        [TestCase(RunMode.WithDome, true, false, true)]
        [TestCase(RunMode.WithoutSafety, false, true, false)]
        public void Each_run_mode_says_what_it_watches_and_who_runs_the_dome(RunMode mode, bool usesSafety, bool plannerHandlesDome, bool canAutoStart) {
            var o = new PlannerOptions { RunMode = mode };
            o.UsesSafety.Should().Be(usesSafety);
            o.PlannerHandlesDome.Should().Be(plannerHandlesDome);
            o.CanAutoStart.Should().Be(canAutoStart);
        }

        [Test]
        public void The_device_the_run_depends_on_is_always_kept_connected() {
            PlannerOptions With(RunMode m) { var o = new PlannerOptions { RunMode = m }; o.KeepConnected.Clear(); return o; }
            With(RunMode.WithSafety).Keeps("Safety Monitor").Should().BeTrue();
            With(RunMode.WithSafety).Keeps("Dome").Should().BeFalse();
            With(RunMode.WithDome).Keeps("Dome").Should().BeTrue();
            With(RunMode.WithDome).Keeps("Safety Monitor").Should().BeFalse("the safety monitor is not needed with the dome shutter");
            With(RunMode.WithoutSafety).Keeps("Dome").Should().BeFalse();
            With(RunMode.WithoutSafety).Keeps("Safety Monitor").Should().BeFalse();
        }

        [Test]
        public void A_device_the_user_keeps_stays_kept_in_every_mode() {
            foreach (var mode in Enum.GetValues<RunMode>()) {
                var o = new PlannerOptions { RunMode = mode };
                o.KeepConnected.Clear();
                o.KeepConnected.Add("Weather");
                o.Keeps("Weather").Should().BeTrue();
            }
        }

        [Test]
        public void The_safety_source_is_named_for_the_messages() {
            new PlannerOptions { RunMode = RunMode.WithDome }.SafetySourceName.Should().Be("the dome shutter");
            new PlannerOptions { RunMode = RunMode.WithSafety }.SafetySourceName.Should().Be("the safety monitor");
        }

        [Test]
        public void Run_with_dome_is_saved_and_old_files_still_load() {
            var o = new PlannerOptions { RunMode = RunMode.WithDome };
            var json = PlannerStore.Serialize(o);
            json.Should().Contain("WithDome");
            PlannerStore.Deserialize<PlannerOptions>(json).RunMode.Should().Be(RunMode.WithDome);
            PlannerStore.Deserialize<PlannerOptions>("{ \"RunMode\": \"WithSafety\" }").RunMode.Should().Be(RunMode.WithSafety);
            PlannerStore.Deserialize<PlannerOptions>("{ \"RunMode\": \"WithoutSafety\" }").RunMode.Should().Be(RunMode.WithoutSafety, "Manual is the old Run without safety");
            PlannerStore.Deserialize<PlannerOptions>("{ }").RunMode.Should().Be(RunMode.WithSafety, "a file with no run mode keeps the default");
        }

        [Test]
        public void Switching_NINA_profile_keeps_the_run_mode() {
            var a = new PlannerOptions();
            a.CopyFrom(new PlannerOptions { RunMode = RunMode.WithDome });
            a.RunMode.Should().Be(RunMode.WithDome);
        }

        // ---------- the workflows ----------

        [Test]
        public void Run_with_dome_starts_from_the_safety_workflow_and_keeps_its_mode_when_it_is_loaded() {
            WorkflowLibrary.DefaultFor(RunMode.WithDome).Should().Be(WorkflowLibrary.SafetyAndDome);
            WorkflowLibrary.DefaultFor(RunMode.WithSafety).Should().Be(WorkflowLibrary.SafetyAndDome);
            WorkflowLibrary.DefaultFor(RunMode.WithoutSafety).Should().Be(WorkflowLibrary.NoSafetyWithDome);
            WorkflowLibrary.ModeAfterLoading(WorkflowLibrary.SafetyAndDome, RunMode.WithDome).Should().Be(RunMode.WithDome, "loading it must not flip Run with dome to Run with safety");
            WorkflowLibrary.ModeAfterLoading(WorkflowLibrary.SafetyAndDome, RunMode.WithSafety).Should().Be(RunMode.WithSafety);
            WorkflowLibrary.ModeAfterLoading(WorkflowLibrary.SafetyAndDome, RunMode.WithoutSafety).Should().Be(RunMode.WithSafety);
            WorkflowLibrary.ModeAfterLoading(WorkflowLibrary.NoSafetyWithDome, RunMode.WithDome).Should().Be(RunMode.WithoutSafety, "a no-safety workflow is a manual run");
            WorkflowLibrary.ModeAfterLoading(WorkflowLibrary.NoSafetyNoDome, RunMode.WithSafety).Should().Be(RunMode.WithoutSafety);
        }

        // ---------- the engine ----------

        private sealed class DomeHardware : IPlannerHardware {
            private readonly FakeHardware inner;
            private readonly PlannerOptions options;
            public DomeHardware(FakeHardware inner, PlannerOptions options) { this.inner = inner; this.options = options; }
            public List<string> Calls => inner.Calls;
            public Task ConnectKeptDevices(CancellationToken token) => inner.ConnectKeptDevices(token);
            public Task RunStage(StageKind stage, CancellationToken token) => inner.RunStage(stage, token);
            public Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) => inner.RunTarget(target, canStartFrame, token);
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => inner.RunGapSteps(steps, token);
            // as NinaPlannerHardware decides them
            public Task<bool> CloseUp(CancellationToken token) =>
                inner.RunGapSteps(DomeSafety.CloseUpSteps(options.PlannerHandlesDome, true, false), token).ContinueWith(_ => true);
            public Task<bool> Reopen(CancellationToken token) =>
                inner.RunGapSteps(DomeSafety.ReopenSteps(options.PlannerHandlesDome, true), token).ContinueWith(_ => true);
        }

        /// <summary>The calls contain these in this order, each given as the start of a call (a time such as "21:" is not fixed).</summary>
        private static void InOrder(IEnumerable<string> calls, params string[] prefixes) {
            var rest = calls.ToList();
            foreach (var prefix in prefixes) {
                var at = rest.FindIndex(c => c.StartsWith(prefix));
                at.Should().BeGreaterOrEqualTo(0, $"\"{prefix}\" should come after the one before it in [{string.Join("; ", calls)}]");
                rest = rest.Skip(at + 1).ToList();
            }
        }

        private static PlannerOptions Dome(Action<PlannerOptions> more = null) {
            var o = new PlannerOptions { RunMode = RunMode.WithDome, AutofocusAfterBegin = false };
            more?.Invoke(o);
            return o;
        }

        private static (PlannerEngine engine, DomeHardware hw, ListLog log, FakeClock clock) Make(PlannerOptions options, Func<FakeClock, ISafetySource> safety, DateTime start, params PlannerTarget[] targets) {
            var clock = new FakeClock(start);
            var hw = new DomeHardware(new FakeHardware(clock), options);
            var log = new ListLog();
            var list = targets.ToList();
            var engine = new PlannerEngine(options, TestData.Selector(options), () => list, hw, safety(clock), clock, log) { WatchDelay = (d, t) => Task.Delay(1, t) };
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

        private static async Task RunFor(PlannerEngine engine, int milliseconds) {
            using var cts = new CancellationTokenSource();
            var run = engine.RunAsync(cts.Token);
            await Task.Delay(milliseconds);
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }
        }

        [Test]
        public async Task It_waits_for_the_shutter_to_be_open_before_1_Begin() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var (engine, hw, log, _) = Make(Dome(), c => new ScriptedDome(c, DomeShutter.Closed).At(TestData.Night(21, 30), DomeShutter.Open), TestData.Night(21), t);
            await RunUntil(engine, () => t.IsComplete);

            log.Phases.Should().Contain(p => p.phase == PlannerPhase.WaitingForSafe && p.message == "Waiting for the dome shutter to be open");
            log.Phases.Should().NotContain(p => p.message.Contains("safety monitor"));
            InOrder(hw.Calls, "stage 1", "target T at 21:3");
            var at = TimeSpan.Parse(hw.Calls.First(c => c.StartsWith("target T at ")).Substring("target T at ".Length));
            at.Should().BeGreaterOrEqualTo(new TimeSpan(21, 30, 0), "1 Begin did not start while the shutter was closed");
        }

        [TestCase(DomeShutter.Closed)]
        [TestCase(DomeShutter.Opening)]
        [TestCase(DomeShutter.Closing)]
        [TestCase(DomeShutter.Error)]
        [TestCase(DomeShutter.None)]
        public async Task A_shutter_that_is_not_open_never_starts_1_Begin(DomeShutter state) {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var (engine, hw, _, _) = Make(Dome(), c => new ScriptedDome(c, state), TestData.Night(21), t);
            await RunFor(engine, 150);
            hw.Calls.Should().NotContain(c => c.StartsWith("stage") || c.StartsWith("target"));
        }

        [Test]
        public async Task A_dome_that_is_not_connected_never_starts_1_Begin_and_the_panel_text_says_so() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var (engine, hw, log, _) = Make(Dome(), c => new ScriptedDome(c, DomeShutter.Open, connected: false), TestData.Night(21), t);
            await RunFor(engine, 150);
            hw.Calls.Should().NotContain(c => c.StartsWith("stage"));
            lock (log.Statuses) { log.Statuses.Should().Contain(s => s.Contains("the dome is not connected or reports no shutter status")); }
        }

        [Test]
        public async Task A_dome_that_closes_during_the_night_runs_4_End_and_the_night_goes_on_when_it_opens_again() {
            var t = TestData.Circumpolar("T", frames: 30, exposure: 300);
            var (engine, hw, log, _) = Make(Dome(), c => new ScriptedDome(c, DomeShutter.Open)
                .At(TestData.Night(21, 30), DomeShutter.Closing).At(TestData.Night(21, 31), DomeShutter.Closed).At(TestData.Night(23), DomeShutter.Open),
                TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Count(c => c.StartsWith("target T")) >= 2);

            InOrder(hw.Calls, "stage 1", "target T at 21:", "stage 4", "stage 1", "target T at 23:");
            log.Phases.Should().Contain(p => p.message == "4 End (unsafe)");
            hw.Calls.Where(c => c.StartsWith("gap")).Should().NotContain(c => c.Contains("Dome"), "the planner never opens or closes the dome");
        }

        [Test]
        public async Task Closing_alone_is_enough_to_stop_imaging_before_the_shutter_is_shut() {
            var t = TestData.Circumpolar("T", frames: 30, exposure: 300);
            var (engine, hw, _, _) = Make(Dome(), c => new ScriptedDome(c, DomeShutter.Open).At(TestData.Night(21, 30), DomeShutter.Closing), TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Contains("stage 4"));
            InOrder(hw.Calls, "stage 1", "target T", "stage 4");
        }

        [Test]
        public async Task A_close_up_parks_without_closing_the_dome_and_opens_up_without_opening_it() {
            var t = TestData.Circumpolar("T", frames: 30, exposure: 300);
            var (engine, hw, log, _) = Make(Dome(o => { o.UnsafeAction = UnsafeAction.CloseUpAndWait; o.CloseUpMaxHours = 4; }),
                c => new ScriptedDome(c, DomeShutter.Open).At(TestData.Night(21, 30), DomeShutter.Closed).At(TestData.Night(22, 30), DomeShutter.Open),
                TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Count(c => c.StartsWith("target T")) >= 2);

            InOrder(hw.Calls, "stage 1", "target T at 21:", "gap StopGuiding,StopTracking,Park", "gap Unpark", "target T at 22:");
            hw.Calls.Should().NotContain(c => c.Contains("CloseDome") || c.Contains("OpenDome"));
            hw.Calls.Count(c => c == "stage 1").Should().Be(1, "power and connections stayed on");
            hw.Calls.Should().NotContain("stage 4");
            log.Phases.Should().Contain(p => p.message == "The dome closed: closing up (stop guiding, park); power and camera cooling stay on");
            log.Phases.Should().Contain(p => p.message.StartsWith("Closed up, waiting for the dome to open again"));
            log.Phases.Should().Contain(p => p.message == "The dome is open again: unparking");
            log.Phases.Should().NotContain(p => p.message.Contains("for the weather") || p.message.Contains("opening the dome"));
        }

        [Test]
        public async Task A_dome_still_closed_after_the_limit_runs_4_End() {
            var t = TestData.Circumpolar("T", frames: 30, exposure: 300);
            var (engine, hw, log, _) = Make(Dome(o => { o.UnsafeAction = UnsafeAction.CloseUpAndWait; o.CloseUpMaxHours = 1; }),
                c => new ScriptedDome(c, DomeShutter.Open).At(TestData.Night(21, 30), DomeShutter.Closed), TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Contains("stage 4"));
            hw.Calls.Should().ContainInOrder("gap StopGuiding,StopTracking,Park", "stage 4");
            log.Phases.Should().Contain(p => p.message == "4 End (still unsafe after 1 h)");
        }

        [Test]
        public async Task The_wait_after_safe_applies_to_the_dome_and_starts_again_when_it_closes_during_the_wait() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var (engine, hw, log, _) = Make(Dome(o => o.SafeDelaySeconds = 600),
                c => new ScriptedDome(c, DomeShutter.Closed).At(TestData.Night(21, 10), DomeShutter.Open).At(TestData.Night(21, 15), DomeShutter.Closed).At(TestData.Night(21, 30), DomeShutter.Open),
                TestData.Night(21), t);
            await RunUntil(engine, () => t.IsComplete);
            log.Statuses.ToArray().Should().Contain(s => s.StartsWith("Safe: 1 Begin starts in"));
            var at = TimeSpan.Parse(hw.Calls.First(c => c.StartsWith("target T at ")).Substring("target T at ".Length));
            at.Should().BeGreaterOrEqualTo(new TimeSpan(21, 40, 0), "the shutter had to stay open for the whole wait, which started again at 21:30");
        }

        [Test]
        public async Task A_gap_between_targets_does_not_close_or_open_the_dome() {
            var first = TestData.Circumpolar("First", frames: 1, exposure: 60);
            var second = TestData.Circumpolar("Second", frames: 1, exposure: 60);
            second.Start = TestData.At(23, 30);
            var (engine, hw, _, _) = Make(Dome(o => { o.GapMinutes = 10; o.GapMount = GapMountAction.StopTrackingAndPark; o.GapCloseDome = true; }),
                c => new ScriptedDome(c, DomeShutter.Open), TestData.Night(21), first, second);
            await RunUntil(engine, () => second.IsComplete);
            hw.Calls.Should().Contain("gap StopGuiding,StopTracking,Park").And.Contain("gap Unpark");
            hw.Calls.Should().NotContain(c => c.Contains("CloseDome") || c.Contains("OpenDome"));
        }

        [Test]
        public async Task The_same_gap_with_a_safety_monitor_still_closes_and_opens_the_dome() {
            var first = TestData.Circumpolar("First", frames: 1, exposure: 60);
            var second = TestData.Circumpolar("Second", frames: 1, exposure: 60);
            second.Start = TestData.At(23, 30);
            var options = new PlannerOptions { RunMode = RunMode.WithSafety, GapMinutes = 10, GapMount = GapMountAction.StopTrackingAndPark, GapCloseDome = true, AutofocusAfterBegin = false };
            var (engine, hw, _, _) = Make(options, c => new ScriptedSafety(c, true), TestData.Night(21), first, second);
            await RunUntil(engine, () => second.IsComplete);
            hw.Calls.Should().Contain("gap StopGuiding,StopTracking,Park,CloseDome").And.Contain("gap OpenDome,Unpark");
        }

        [Test]
        public async Task A_safety_monitor_close_up_still_closes_the_dome() {
            var t = TestData.Circumpolar("T", frames: 30, exposure: 300);
            var options = new PlannerOptions { RunMode = RunMode.WithSafety, UnsafeAction = UnsafeAction.CloseUpAndWait, CloseUpMaxHours = 4, AutofocusAfterBegin = false };
            var (engine, hw, log, _) = Make(options, c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false).At(TestData.Night(22, 30), true), TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Count(c => c.StartsWith("target T")) >= 2);
            hw.Calls.Should().Contain("gap StopGuiding,StopTracking,Park,CloseDome").And.Contain("gap OpenDome,Unpark");
            log.Phases.Should().Contain(p => p.message == "Unsafe: closing up (stop guiding, park, close the dome); power and camera cooling stay on");
            log.Phases.Should().Contain(p => p.message == "Safe again: opening the dome and unparking");
        }

        [Test]
        public async Task Manual_ignores_the_dome_shutter_and_runs_once() {
            var t = TestData.Circumpolar("T", frames: 3, exposure: 60);
            var options = new PlannerOptions { RunMode = RunMode.WithoutSafety, AutofocusAfterBegin = false };
            var (engine, hw, _, _) = Make(options, c => new ScriptedDome(c, DomeShutter.Closed), TestData.Night(21), t);
            await engine.RunAsync(CancellationToken.None);
            hw.Calls.Should().Equal("connect", "stage 1", "target T at 21:01", "stage 4");
            t.DoneFrames.Should().Be(3);
        }

        [Test]
        public async Task Run_with_dome_loops_to_the_next_night_after_the_work_is_done() {
            var t = TestData.Circumpolar("T", frames: 2, exposure: 60);
            var (engine, hw, log, _) = Make(Dome(), c => new ScriptedDome(c, DomeShutter.Open).At(TestData.Night(23), DomeShutter.Closed), TestData.Night(21), t);
            await RunUntil(engine, () => log.Phases.Any(p => p.phase == PlannerPhase.WaitingForNextNight));
            InOrder(hw.Calls, "stage 1", "target T", "stage 4");
            log.Phases.Should().Contain(p => p.phase == PlannerPhase.WaitingForNextNight);
        }
    }

    /// <summary>The Nina side of Run with dome: NINA's shutter states, the safety source, and the dome steps of the stages against NINA's real container.</summary>
    [TestFixture]
    public class DomeStepsTests {

        private sealed class Mark : SequenceItem {
            private readonly List<string> ran;
            public Mark(string name, List<string> ran) { Name = name; this.ran = ran; }
            public override object Clone() => new Mark(Name, ran) { Status = Status };
            public override Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) { ran.Add(Name); return Task.CompletedTask; }
        }

        private static Mock<IDomeMediator> DomeMediator() {
            var m = new Mock<IDomeMediator>();
            m.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, CanSetShutter = true, ShutterStatus = ShutterState.ShutterOpen }); // the steps validate against it
            m.Setup(d => d.OpenShutter(It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));
            m.Setup(d => d.CloseShutter(It.IsAny<CancellationToken>())).Returns(Task.FromResult(true));
            return m;
        }

        private static PlannerStageContainer Stage(Mock<IDomeMediator> dome, List<string> ran) {
            var stage = new PlannerStageContainer { Stage = StageKind.End };
            stage.Add(new Mark("Park", ran));
            stage.Add(new CloseDomeShutter(dome.Object) { Name = "Close Dome Shutter" });
            stage.Add(new Mark("Warm Camera", ran));
            return stage;
        }

        [Test]
        public void NINAs_shutter_states_map_to_the_planners() {
            NinaSafetySource.ShutterOf(new DomeInfo { Connected = true, ShutterStatus = ShutterState.ShutterOpen }).Should().Be(DomeShutter.Open);
            NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = ShutterState.ShutterClosed }).Should().Be(DomeShutter.Closed);
            NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = ShutterState.ShutterOpening }).Should().Be(DomeShutter.Opening);
            NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = ShutterState.ShutterClosing }).Should().Be(DomeShutter.Closing);
            NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = ShutterState.ShutterError }).Should().Be(DomeShutter.Error);
            NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = ShutterState.ShutterNone }).Should().Be(DomeShutter.None);
            NinaSafetySource.ShutterOf(null).Should().Be(DomeShutter.None);
            foreach (var s in Enum.GetValues<ShutterState>()) { NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = s }).Should().Be(NinaSafetySource.ShutterOf(new DomeInfo { ShutterStatus = s })); }
        }

        private static NinaServices Services(IDomeMediator dome, ISafetyMonitorMediator monitor) =>
            new(null, null, null, null, null, null, null, null, null, null, dome, monitor, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        [TestCase(ShutterState.ShutterOpen, true, true, true)]
        [TestCase(ShutterState.ShutterClosed, true, true, false)]
        [TestCase(ShutterState.ShutterClosing, true, true, false)]
        [TestCase(ShutterState.ShutterOpening, true, true, false)]
        [TestCase(ShutterState.ShutterError, true, true, false)]
        [TestCase(ShutterState.ShutterNone, true, false, false)]
        [TestCase(ShutterState.ShutterOpen, false, false, false)]
        public void Run_with_dome_reads_the_dome_and_not_the_safety_monitor(ShutterState shutter, bool connected, bool readable, bool safe) {
            var dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = connected, ShutterStatus = shutter });
            var monitor = new Mock<ISafetyMonitorMediator>();
            monitor.Setup(m => m.GetInfo()).Returns(new SafetyMonitorInfo { Connected = true, IsSafe = !safe }); // says the opposite: must not be used
            var source = new NinaSafetySource(Services(dome.Object, monitor.Object), new PlannerOptions { RunMode = RunMode.WithDome });
            var state = source.Read();
            state.IsSafe.Should().Be(safe);
            state.Connected.Should().Be(readable);
            monitor.Verify(m => m.GetInfo(), Times.Never);
        }

        [TestCase(true, true)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        public void Run_with_safety_reads_the_safety_monitor_and_not_the_dome(bool connected, bool isSafe) {
            var dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, ShutterStatus = isSafe ? ShutterState.ShutterClosed : ShutterState.ShutterOpen });
            var monitor = new Mock<ISafetyMonitorMediator>();
            monitor.Setup(m => m.GetInfo()).Returns(new SafetyMonitorInfo { Connected = connected, IsSafe = isSafe });
            var source = new NinaSafetySource(Services(dome.Object, monitor.Object), new PlannerOptions { RunMode = RunMode.WithSafety });
            source.Read().Should().Be(new SafetyState(connected, isSafe));
            dome.Verify(d => d.GetInfo(), Times.Never);
        }

        [Test]
        public void The_source_follows_the_run_mode_when_it_is_changed() {
            var dome = new Mock<IDomeMediator>();
            dome.Setup(d => d.GetInfo()).Returns(new DomeInfo { Connected = true, ShutterStatus = ShutterState.ShutterOpen });
            var monitor = new Mock<ISafetyMonitorMediator>();
            monitor.Setup(m => m.GetInfo()).Returns(new SafetyMonitorInfo { Connected = true, IsSafe = false });
            var options = new PlannerOptions { RunMode = RunMode.WithSafety };
            var source = new NinaSafetySource(Services(dome.Object, monitor.Object), options);
            source.Read().IsSafe.Should().BeFalse();
            options.RunMode = RunMode.WithDome;
            source.Read().IsSafe.Should().BeTrue();
        }

        [Test]
        public void A_missing_NINA_reads_as_not_safe_not_as_a_crash() {
            var root = Path.Combine(Path.GetTempPath(), "op-dome-" + Guid.NewGuid().ToString("N"));
            try {
                var planner = new PlannerService(null, root);
                planner.Options.RunMode = RunMode.WithDome;
                planner.ReadSafety().Should().Be(new SafetyState(false, false));
                planner.DomeNow().Should().Be((false, DomeShutter.None));
                planner.DomeConfigured.Should().BeFalse();
            } finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Test]
        public async Task Switched_off_dome_steps_do_not_run_in_NINAs_own_container_and_the_others_do() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var stage = Stage(dome, ran);
            DomeSteps.SwitchOff(stage).Should().Equal("Close Dome Shutter");
            stage.Items[1].Status.Should().Be(SequenceEntityStatus.DISABLED);

            stage.ResetAll(); // as the planner does before it runs a stage
            await stage.Run(new Progress<ApplicationStatus>(), CancellationToken.None);

            ran.Should().Equal("Park", "Warm Camera");
            dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Never, "the planner never closes the dome in Run with dome");
            stage.Items[1].Status.Should().Be(SequenceEntityStatus.DISABLED, "it stays switched off after the run");
        }

        [Test]
        public async Task With_the_steps_left_on_the_dome_is_closed_as_before() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var stage = Stage(dome, ran);
            stage.ResetAll();
            await stage.Run(new Progress<ApplicationStatus>(), CancellationToken.None);
            dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public void Switching_off_finds_the_dome_steps_in_nested_containers_and_leaves_the_rest() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var outer = new PlannerStageContainer { Stage = StageKind.Begin };
            outer.Add(new OpenDomeShutter(dome.Object) { Name = "Open Dome Shutter" });
            var inner = new PlannerStageContainer { Stage = StageKind.End };
            inner.Add(new CloseDomeShutter(dome.Object) { Name = "Close Dome Shutter" });
            inner.Add(new Mark("Warm Camera", ran));
            outer.Add(inner);
            outer.Add(new Mark("Unpark", ran));

            DomeSteps.SwitchOff(outer).Should().Equal("Open Dome Shutter", "Close Dome Shutter");
            DomeSteps.SwitchOff(outer).Should().BeEmpty("they are off already");
            NinaPlannerHardware.Descendants(outer).Where(i => i is Mark).Should().OnlyContain(i => i.Status != SequenceEntityStatus.DISABLED);
            WorkflowFiles.DisabledPaths(outer).Should().Equal("i0", "i1/i0");
        }

        [Test]
        public void Switching_on_again_only_touches_the_dome_steps() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var stage = Stage(dome, ran);
            stage.Items[0].Status = SequenceEntityStatus.DISABLED; // a step the user switched off himself stays off
            DomeSteps.SwitchOff(stage);
            DomeSteps.SwitchOn(stage).Should().Equal("Close Dome Shutter");
            stage.Items[1].Status.Should().Be(SequenceEntityStatus.CREATED);
            stage.Items[0].Status.Should().Be(SequenceEntityStatus.DISABLED);
            DomeSteps.SwitchOn(stage).Should().BeEmpty();
        }

        [Test]
        public void The_dome_steps_are_recognised_by_type_and_other_dome_instructions_are_not_touched() {
            var dome = DomeMediator();
            DomeSteps.IsOpenClose(new OpenDomeShutter(dome.Object) { Name = "Open Dome Shutter" }).Should().BeTrue();
            DomeSteps.IsOpenClose(new CloseDomeShutter(dome.Object) { Name = "Close Dome Shutter" }).Should().BeTrue();
            DomeSteps.IsOpenClose(new Mark("Park", new List<string>())).Should().BeFalse();
        }

        private static (NinaPlannerHardware hw, PlannerService planner, ObservatoryPlannerContainer block, string root) Hardware(RunMode mode, Mock<IDomeMediator> dome, List<string> ran) {
            var root = Path.Combine(Path.GetTempPath(), "op-dome-hw-" + Guid.NewGuid().ToString("N"));
            var planner = new PlannerService(null, root);
            planner.Options.RunMode = mode;
            var block = WorkflowLibrary.Assemble(planner,
                begin: new ISequenceItem[] { new Mark("Connect", ran), new OpenDomeShutter(dome.Object) { Name = "Open Dome Shutter" }, new Mark("Unpark", ran) },
                targetStart: Array.Empty<ISequenceItem>(), triggers: Array.Empty<NINA.Sequencer.Trigger.ISequenceTrigger>(),
                end: new ISequenceItem[] { new Mark("Park", ran), new CloseDomeShutter(dome.Object) { Name = "Close Dome Shutter" }, new Mark("Warm Camera", ran) });
            return (new NinaPlannerHardware(block, planner, new Progress<ApplicationStatus>()), planner, block, root);
        }

        [Test]
        public async Task The_real_stage_run_leaves_the_dome_steps_off_in_Run_with_dome() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var (hw, _, block, root) = Hardware(RunMode.WithDome, dome, ran);
            try {
                await hw.RunStage(StageKind.Begin, CancellationToken.None);
                await hw.RunStage(StageKind.End, CancellationToken.None);
                ran.Should().Equal("Connect", "Unpark", "Park", "Warm Camera");
                dome.Verify(d => d.OpenShutter(It.IsAny<CancellationToken>()), Times.Never);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Never);
                block.Stage(StageKind.Begin).Items.OfType<OpenDomeShutter>().Single().Status.Should().Be(SequenceEntityStatus.DISABLED, "the step stays, so the Sequence tab shows it");
                block.Stage(StageKind.End).Items.OfType<CloseDomeShutter>().Single().Status.Should().Be(SequenceEntityStatus.DISABLED);
            } finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Test]
        public async Task The_dome_steps_stay_off_on_every_run_even_when_they_were_switched_on() {
            var ran = new List<string>();
            var dome = DomeMediator();
            var (hw, _, block, root) = Hardware(RunMode.WithDome, dome, ran);
            try {
                var open = block.Stage(StageKind.Begin).Items.OfType<OpenDomeShutter>().Single();
                open.Status.Should().NotBe(SequenceEntityStatus.DISABLED);
                await hw.RunStage(StageKind.Begin, CancellationToken.None);
                dome.Verify(d => d.OpenShutter(It.IsAny<CancellationToken>()), Times.Never);
                open.Status = SequenceEntityStatus.CREATED; // the user switches it on again
                await hw.RunStage(StageKind.Begin, CancellationToken.None);
                dome.Verify(d => d.OpenShutter(It.IsAny<CancellationToken>()), Times.Never);
            } finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [TestCase(RunMode.WithSafety)]
        [TestCase(RunMode.WithoutSafety)]
        public async Task The_real_stage_run_still_opens_and_closes_the_dome_when_the_planner_runs_it(RunMode mode) {
            var ran = new List<string>();
            var dome = DomeMediator();
            var (hw, _, _, root) = Hardware(mode, dome, ran);
            try {
                await hw.RunStage(StageKind.Begin, CancellationToken.None);
                await hw.RunStage(StageKind.End, CancellationToken.None);
                ran.Should().Equal("Connect", "Unpark", "Park", "Warm Camera");
                dome.Verify(d => d.OpenShutter(It.IsAny<CancellationToken>()), Times.Once);
                dome.Verify(d => d.CloseShutter(It.IsAny<CancellationToken>()), Times.Once);
            } finally { try { Directory.Delete(root, true); } catch (IOException) { } }
        }

        [Test]
        public void The_switched_off_dome_steps_survive_a_restart() {
            var folder = Path.Combine(Path.GetTempPath(), "op-dome-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try {
                var ran = new List<string>();
                var before = Stage(DomeMediator(), ran);
                DomeSteps.SwitchOff(before);
                var path = Path.Combine(folder, "Safety and dome.json");
                const string json = "{ \"stand-in\": 1 }";
                File.WriteAllText(path, json);
                WorkflowFiles.WriteDisabled(path, json, WorkflowFiles.DisabledPaths(before));

                var after = Stage(DomeMediator(), ran); // NINA's converter builds everything on again
                after.Items[1].Status.Should().NotBe(SequenceEntityStatus.DISABLED);
                WorkflowFiles.ApplyDisabled(after, WorkflowFiles.ReadDisabled(path, json));
                after.Items[1].Status.Should().Be(SequenceEntityStatus.DISABLED);
            } finally { Directory.Delete(folder, true); }
        }
    }

    /// <summary>The panel in each run mode (no NINA here: the panel must still tell the truth about the mode).</summary>
    [TestFixture, Apartment(ApartmentState.STA), NonParallelizable]
    public class DomeModePanelTests {
        private string root;
        private PlannerService planner;
        private PlannerDockableVM vm;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "op-dome-panel-" + Guid.NewGuid().ToString("N"));
            planner = new PlannerService(null, root);
            planner.Targets.Add(planner.NewTarget("Barnard 175"));
            vm = new PlannerDockableVM(new Mock<IProfileService>().Object, planner);
        }

        [TearDown]
        public void TearDown() { try { Directory.Delete(root, true); } catch (IOException) { } }

        [Test]
        public void The_three_run_modes_exclude_each_other() {
            planner.Options.RunMode = RunMode.WithDome;
            (vm.WithSafety, vm.WithDome, vm.WithoutSafety).Should().Be((false, true, false));
            planner.Options.RunMode = RunMode.WithoutSafety;
            (vm.WithSafety, vm.WithDome, vm.WithoutSafety).Should().Be((false, false, true));
            planner.Options.RunMode = RunMode.WithSafety;
            (vm.WithSafety, vm.WithDome, vm.WithoutSafety).Should().Be((true, false, false));
        }

        [Test]
        public void What_loops_and_watches_follows_the_mode() {
            planner.Options.RunMode = RunMode.WithDome;
            vm.UsesSafety.Should().BeTrue();
            vm.PlannerHandlesDome.Should().BeFalse();
            vm.RunButtonText.Should().Be("▶ Run forever");
            vm.ModeSummary.Should().Be("Run with dome");
            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.UsesSafety.Should().BeFalse();
            vm.RunButtonText.Should().Be("▶ Run");
            vm.ModeSummary.Should().Be("Manual");
            planner.Options.RunMode = RunMode.WithSafety;
            vm.RunButtonText.Should().Be("▶ Run forever");
            vm.ModeSummary.Should().Be("Run with safety");
        }

        [Test]
        public void The_run_mode_and_the_autostart_follow_what_is_running() {
            vm.CanChangeMode.Should().BeTrue();
            vm.ModeLocked.Should().BeFalse();
            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.CanAutoStart.Should().BeFalse("a manual run never starts by itself");
            vm.AutoStartBlocked.Should().BeTrue();
            planner.Options.RunMode = RunMode.WithDome;
            vm.CanAutoStart.Should().BeTrue();
            vm.AutoStartBlocked.Should().BeFalse();
        }

        [Test]
        public void Manual_keeps_the_ticked_autostart_but_never_uses_it() {
            planner.Options.AutoStartOnLaunch = true;
            planner.Options.RunMode = RunMode.WithoutSafety;
            planner.Options.AutoStartOnLaunch.Should().BeTrue("the tick is remembered for when a safety mode is chosen again");
            planner.Options.CanAutoStart.Should().BeFalse();
        }

        [Test]
        public void The_dome_row_shows_only_in_Run_with_dome() {
            vm.DomeRowShown.Should().BeFalse();
            planner.Options.RunMode = RunMode.WithDome;
            vm.DomeRowShown.Should().BeTrue();
            vm.DomeChipText.Should().Be("Not connected", "no NINA here: the dome can't be read");
            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.DomeRowShown.Should().BeFalse();
        }

        [Test]
        public void A_missing_dome_is_a_problem_only_in_Run_with_dome() {
            vm.DomeProblems.Should().BeEmpty();
            planner.Options.RunMode = RunMode.WithDome;
            vm.DomeProblems.Should().ContainSingle().Which.Should().Contain("no dome is set up");
            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.DomeProblems.Should().BeEmpty();
        }

        [Test]
        public void The_texts_follow_the_mode() {
            planner.Options.RunMode = RunMode.WithDome;
            vm.UnsafeTitle.Should().Be("When the dome closes during the night:");
            vm.CloseUpChoiceText.Should().NotContain("close the dome");
            vm.CloseUpTimerText.Should().Contain("still closed");
            vm.SafeDelayText.Should().Be("the dome is open");
            vm.NightBeginBullet.Should().Contain("Run with dome");
            vm.NightOffBullet.Should().Contain("dome shutter").And.NotContain("safety monitor");
            vm.CloseUpHint.Should().NotContain("open the dome, unpark").And.Contain("the dome is open again");
            vm.DeviceRecoveryText.Should().Contain("missing dome");
            vm.CycleHint.Should().Contain("The dome closing");

            planner.Options.RunMode = RunMode.WithSafety;
            vm.UnsafeTitle.Should().Be("When it turns unsafe during the night:");
            vm.CloseUpChoiceText.Should().Contain("close the dome");
            vm.SafeDelayText.Should().Be("it becomes safe");
            vm.NightBeginBullet.Should().Contain("with a safety monitor");
            vm.DeviceRecoveryText.Should().Contain("A missing safety monitor always counts as unsafe");

            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.MountRecoveryText.Should().NotContain("closed up or waiting");
            vm.DeviceRecoveryText.Should().NotContain("safety monitor always").And.NotContain("waiting, closed up");
            planner.Options.RunMode = RunMode.WithSafety;
            vm.MountRecoveryText.Should().Contain("closed up or waiting");
        }

        [Test]
        public void Target_chips_say_when_a_target_starts_and_ends_in_each_mode() {
            planner.Options.RunMode = RunMode.WithDome;
            vm.TargetChips.Should().Contain("starts when the dome is open").And.Contain("runs until the dome closes");
            planner.Options.RunMode = RunMode.WithSafety;
            vm.TargetChips.Should().Contain("starts when safe").And.Contain("runs until unsafe");
            planner.Options.RunMode = RunMode.WithoutSafety;
            vm.TargetChips.Should().Contain("starts when dark").And.Contain("runs until dawn");
        }

        [Test]
        public void The_between_targets_steps_have_no_dome_in_Run_with_dome() {
            planner.Options.GapMount = GapMountAction.StopTrackingAndPark;
            planner.Options.GapCloseDome = true;
            vm.GapCloseDomeVisible.Should().BeTrue();
            vm.GapWaitSteps.Select(c => c.Text).Should().Contain("Close Dome Shutter");
            planner.Options.RunMode = RunMode.WithDome;
            vm.GapCloseDomeVisible.Should().BeFalse("the planner does not close the dome in Run with dome");
            vm.GapWaitSteps.Select(c => c.Text).Should().NotContain("Close Dome Shutter");
            vm.GapResumeSteps.Select(c => c.Text).Should().NotContain("Open Dome Shutter");
        }

        [Test]
        public void The_dome_device_is_locked_on_in_Keep_connected_only_in_Run_with_dome() {
            var dome = vm.KeepItems.Single(k => k.Device == "Dome");
            var monitor = vm.KeepItems.Single(k => k.Device == "Safety Monitor");
            monitor.Locked.Should().BeTrue();
            monitor.Note.Should().Be("required with safety");
            dome.Locked.Should().BeFalse();
            planner.Options.RunMode = RunMode.WithDome;
            dome.Refresh(); monitor.Refresh();
            dome.Locked.Should().BeTrue();
            dome.Note.Should().Be("required with dome");
            dome.IsChecked.Should().BeTrue();
            monitor.Locked.Should().BeFalse();
            planner.Options.RunMode = RunMode.WithoutSafety;
            dome.Refresh(); monitor.Refresh();
            dome.Locked.Should().BeFalse();
            monitor.Locked.Should().BeFalse();
        }

        private static IEnumerable<System.Windows.DependencyObject> Visuals(System.Windows.DependencyObject d) {
            yield return d;
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) {
                foreach (var c in Visuals(System.Windows.Media.VisualTreeHelper.GetChild(d, i))) { yield return c; }
            }
        }

        private static bool Shows(System.Windows.DependencyObject root, string text) =>
            Visuals(root).OfType<System.Windows.Controls.TextBlock>().Any(t => t.IsVisible && (t.Text ?? "").Contains(text));

        private System.Windows.Window Show(int tab) {
            var template = (System.Windows.DataTemplate)new PlannerView()["NINA.ObservatoryPlanner.UI.PlannerDockableVM_Dockable"];
            var w = new System.Windows.Window {
                Content = new System.Windows.Controls.ContentControl { Content = vm, ContentTemplate = template }, Width = 700, Height = 3000,
                ShowActivated = false, WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -10000
            };
            vm.SelectedPlannerTab = tab;
            w.Show();
            w.UpdateLayout();
            foreach (var e in Visuals(w).OfType<System.Windows.Controls.Expander>()) { e.IsExpanded = true; }
            w.UpdateLayout();
            return w;
        }

        [Test]
        public void The_options_tab_draws_in_every_mode_and_shows_only_what_belongs_to_the_mode() {
            foreach (var mode in new[] { RunMode.WithSafety, RunMode.WithDome, RunMode.WithoutSafety }) {
                planner.Options.RunMode = mode;
                var w = Show(2);
                try {
                    Shows(w, "Run with safety").Should().BeTrue(mode.ToString());
                    Shows(w, "Run with dome").Should().BeTrue(mode.ToString());
                    Shows(w, "Manual").Should().BeTrue(mode.ToString());
                    Shows(w, "the shutter tells the planner that the dome is open, not that the weather is good").Should().Be(mode == RunMode.WithDome, mode.ToString());
                    Shows(w, "When the dome closes during the night:").Should().Be(mode == RunMode.WithDome, mode.ToString());
                    Shows(w, "The planner does not close the dome in Run with dome").Should().Be(mode == RunMode.WithDome, mode.ToString());
                    Shows(w, "Close the dome/roof anyway when it has to close").Should().Be(mode != RunMode.WithDome, mode.ToString());
                    Shows(w, "When it turns unsafe during the night:").Should().Be(mode == RunMode.WithSafety, mode.ToString());
                    Shows(w, "Nothing watches the weather").Should().Be(mode == RunMode.WithoutSafety, mode.ToString());
                    Shows(w, "Not available in Manual").Should().Be(mode == RunMode.WithoutSafety, mode.ToString());
                    Shows(w, "When the dome is open, wait").Should().Be(mode == RunMode.WithDome, mode.ToString());
                    Shows(w, "When it becomes safe, wait").Should().Be(mode == RunMode.WithSafety, mode.ToString());
                    Shows(w, "Locked while a run is in progress").Should().BeFalse(mode.ToString());
                } finally { w.Close(); }
            }
        }

        [Test]
        public void The_radios_are_disabled_and_the_lock_note_shows_while_a_run_is_going() {
            var setter = typeof(PlannerService).GetProperty(nameof(PlannerService.IsRunning)).GetSetMethod(true);
            setter.Invoke(planner, new object[] { true });
            try {
                vm.CanChangeMode.Should().BeFalse();
                vm.ModeLocked.Should().BeTrue();
                var w = Show(2);
                try {
                    Shows(w, "Locked while a run is in progress or paused").Should().BeTrue();
                    var radios = Visuals(w).OfType<System.Windows.Controls.RadioButton>().Where(r => r.GroupName == "OP_Mode").ToList();
                    radios.Should().HaveCount(3);
                    radios.Should().OnlyContain(r => !r.IsEnabled, "the run mode can't be changed while a run is in progress");
                } finally { w.Close(); }
            } finally {
                setter.Invoke(planner, new object[] { false });
            }
            vm.CanChangeMode.Should().BeTrue();
            var w2 = Show(2);
            try { Visuals(w2).OfType<System.Windows.Controls.RadioButton>().Where(r => r.GroupName == "OP_Mode").Should().OnlyContain(r => r.IsEnabled); } finally { w2.Close(); }
        }

        [Test]
        public void The_top_block_shows_the_dome_row_only_in_Run_with_dome() {
            foreach (var mode in new[] { RunMode.WithSafety, RunMode.WithDome, RunMode.WithoutSafety }) {
                planner.Options.RunMode = mode;
                var w = Show(0);
                try {
                    Visuals(w).OfType<System.Windows.Controls.TextBlock>().Any(t => t.IsVisible && t.Text == "Not connected").Should().Be(mode == RunMode.WithDome, mode.ToString());
                } finally { w.Close(); }
            }
        }

        [Test]
        public void The_other_tabs_draw_in_every_mode() {
            foreach (var mode in new[] { RunMode.WithSafety, RunMode.WithDome, RunMode.WithoutSafety }) {
                planner.Options.RunMode = mode;
                foreach (var tab in new[] { 0, 1, 3 }) { Show(tab).Close(); }
            }
        }

        [Test]
        public void The_stage_titles_follow_the_mode() {
            planner.Options.RunMode = RunMode.WithDome;
            vm.RefreshStages();
            vm.Stages[0].Title.Should().Be("Begin: when the dome is open");
            vm.Stages[3].Title.Should().Be("End: when the dome closes or finished");
            vm.Stages[0].Hint.Should().Contain("Open and Close Dome Shutter are switched off");
            planner.Options.RunMode = RunMode.WithSafety;
            vm.RefreshStages();
            vm.Stages[0].Title.Should().Be("Begin: when it becomes safe");
        }
    }
}
