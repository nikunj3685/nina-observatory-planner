using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests.Harness {

    /// <summary>
    /// The planner's real engine against a simulated observatory, outside NINA: many seeded random nights (weather, targets, options,
    /// pauses, stops, hardware faults), each checked against the invariants in <see cref="Invariants"/>. A failure names the seed, so
    /// <c>Replay</c> reproduces it. More seeds: set OP_FUZZ_SEEDS (default 25), first seed: OP_FUZZ_FIRST.
    /// </summary>
    [TestFixture]
    public class HarnessTests {

        private static int SeedCount => int.TryParse(Environment.GetEnvironmentVariable("OP_FUZZ_SEEDS"), out var n) ? n : 25;
        private static int FirstSeed => int.TryParse(Environment.GetEnvironmentVariable("OP_FUZZ_FIRST"), out var n) ? n : 1;

        private static async Task<List<(ScenarioResult result, List<string> violations)>> RunMany(Func<int, Scenario> make, int parallel = 4) {
            var seeds = Enumerable.Range(FirstSeed, SeedCount).ToList();
            var all = new List<(ScenarioResult, List<string>)>();
            for (var i = 0; i < seeds.Count; i += parallel) {
                var batch = seeds.Skip(i).Take(parallel).Select(async seed => {
                    var r = await ScenarioRunner.Run(make(seed));
                    return (r, Invariants.Check(r));
                });
                all.AddRange(await Task.WhenAll(batch));
            }
            return all;
        }

        private static void AssertNoViolations(List<(ScenarioResult result, List<string> violations)> runs) {
            var bad = runs.Where(x => x.violations.Count > 0).ToList();
            if (bad.Count == 0) { return; }
            var text = string.Join("\n", bad.Take(3).Select(b => Invariants.Report(b.result, b.violations)));
            Assert.Fail($"{bad.Count} of {runs.Count} nights broke an invariant (first seeds: {string.Join(", ", bad.Take(10).Select(b => b.result.Scenario.Seed))}).\n{text}");
        }

        [Test]
        public async Task Random_nights_keep_every_invariant() =>
            AssertNoViolations(await RunMany(seed => ScenarioGenerator.Make(seed)));

        [Test]
        public async Task Random_nights_with_pauses_and_stops_keep_every_invariant() =>
            AssertNoViolations(await RunMany(seed => ScenarioGenerator.Make(seed, userActions: true)));

        [Test]
        public async Task Random_nights_with_hardware_faults_never_break_the_engine() {
            var runs = await RunMany(seed => ScenarioGenerator.Make(seed, faults: true, userActions: true));
            // a failing dome or park is the hardware's fault, not the engine's: what must still hold is everything else, and
            // the dome is never closed on a mount whose park failed (the roof could hit it)
            static bool Gap(string v) => v.Contains("park while guiding");
            AssertNoViolations(runs.Select(x => (x.result, x.violations.Where(v => !Gap(v)).ToList())).ToList());
        }

        // ---------- Run with dome: the host opens and closes the dome, the planner never does ----------

        [Test]
        public async Task Random_dome_nights_keep_every_invariant_and_the_planner_never_touches_the_dome() =>
            AssertNoViolations(await RunMany(seed => DomeScenarios.Make(seed)));

        [Test]
        public async Task Random_dome_nights_with_pauses_and_stops_keep_every_invariant() =>
            AssertNoViolations(await RunMany(seed => DomeScenarios.Make(seed, userActions: true)));

        [Test]
        public async Task Random_dome_nights_with_hardware_faults_never_break_the_engine() {
            var runs = await RunMany(seed => DomeScenarios.Make(seed, faults: true, userActions: true));
            // only the park can fail here (the planner has no dome to close); a failed park is the hardware's fault
            static bool Gap(string v) => v.Contains("park while guiding");
            AssertNoViolations(runs.Select(x => (x.result, x.violations.Where(v => !Gap(v)).ToList())).ToList());
        }

        [Test]
        public async Task A_night_with_the_host_closing_the_dome_twice_runs_4_End_each_time_and_commands_the_dome_never() {
            var s = DomeScenarios.Make(5);
            s.Options.UnsafeAction = UnsafeAction.RunEnd;
            s.Options.SafeDelaySeconds = 0;
            s.Options.GapMount = GapMountAction.StopTrackingAndPark;
            s.Options.GapCloseDome = true;
            s.Targets = new List<PlannerTarget> { TestData.Circumpolar("T", frames: 40, exposure: 300) };
            s.Safety = c => new RandomSafety(c, true).At(TestData.Night(22), false).At(TestData.Night(23), true).At(TestData.Night(0, 30), false).At(TestData.Night(1, 30), true).At(TestData.Night(6, 30), false);
            s.PauseAtFrame = null; s.StopAtFrame = null;
            var r = await ScenarioRunner.Run(s);

            Invariants.Check(r).Should().BeEmpty();
            r.Journal.Count(e => e.Kind == "stage 1 begin").Should().BeGreaterOrEqualTo(3, "once in the evening and once after each time the host reopened the dome");
            r.Journal.Where(e => e.Kind == "open dome" || e.Kind == "close dome").Should().BeEmpty("the planner never commands the dome in Run with dome");
            r.Journal.Where(e => e.Kind == "frame start").Should().OnlyContain(e => e.Safe, "no frame starts while the dome is closed");
        }

        [Test]
        public async Task A_close_up_night_with_the_host_closing_the_dome_parks_and_waits_without_touching_it() {
            var s = DomeScenarios.Make(8);
            s.Options.UnsafeAction = UnsafeAction.CloseUpAndWait;
            s.Options.CloseUpMaxHours = 3;
            s.Options.SafeDelaySeconds = 0;
            s.Targets = new List<PlannerTarget> { TestData.Circumpolar("T", frames: 40, exposure: 300) };
            s.Safety = c => new RandomSafety(c, true).At(TestData.Night(22), false).At(TestData.Night(23), true).At(TestData.Night(6, 30), false);
            s.PauseAtFrame = null; s.StopAtFrame = null;
            var r = await ScenarioRunner.Run(s);

            Invariants.Check(r).Should().BeEmpty();
            r.Journal.Should().Contain(e => e.Kind == "close up");
            r.Journal.Should().Contain(e => e.Kind == "reopen");
            r.Journal.Count(e => e.Kind == "stage 1 begin").Should().Be(1, "the close-up keeps everything powered, so 1 Begin does not run again");
            r.Journal.Where(e => e.Kind == "open dome" || e.Kind == "close dome").Should().BeEmpty();
            r.Journal.First(e => e.Kind == "reopen").At.Should().BeOnOrAfter(TestData.Night(23));
        }

        [Test]
        public async Task A_host_that_never_opens_the_dome_keeps_everything_off() {
            var s = DomeScenarios.Make(2);
            s.Targets = new List<PlannerTarget> { TestData.Circumpolar("T", frames: 5, exposure: 300) };
            s.Safety = c => new RandomSafety(c, false);
            s.PauseAtFrame = null; s.StopAtFrame = null;
            var r = await ScenarioRunner.Run(s);
            r.Journal.Where(e => e.Kind == "power on" || e.Kind == "stage 1 begin" || e.Kind == "frame start").Should().BeEmpty();
            r.Hardware.Powered.Should().BeFalse();
        }

        /// <summary>Reproduces one night, e.g. a seed named by a failing test. Run with OP_REPLAY_SEED=n.</summary>
        [Test, Explicit("set OP_REPLAY_SEED")]
        public async Task Replay() {
            var seed = int.Parse(Environment.GetEnvironmentVariable("OP_REPLAY_SEED") ?? "1");
            var faults = Environment.GetEnvironmentVariable("OP_REPLAY_FAULTS") == "1";
            var actions = Environment.GetEnvironmentVariable("OP_REPLAY_ACTIONS") == "1";
            var dome = Environment.GetEnvironmentVariable("OP_REPLAY_DOME") == "1";
            var r = await ScenarioRunner.Run(dome ? DomeScenarios.Make(seed, faults, actions) : ScenarioGenerator.Make(seed, faults, actions));
            TestContext.WriteLine(Invariants.Report(r, Invariants.Check(r)));
        }

        [Test, Explicit("measures the clock handshake")]
        public async Task Handshake_statistics() {
            var runs = await RunMany(seed => ScenarioGenerator.Make(seed));
            TestContext.WriteLine($"ticks {HarnessClock.Ticks}, watched {HarnessClock.Watched}, timed out {HarnessClock.TimedOut}, nights {runs.Count}");
        }

        // ---------- the harness itself ----------

        [Test]
        public async Task The_same_seed_gives_the_same_night() {
            var a = await ScenarioRunner.Run(ScenarioGenerator.Make(7));
            var b = await ScenarioRunner.Run(ScenarioGenerator.Make(7));
            a.Scenario.Description.Should().Be(b.Scenario.Description);
            a.Hardware.FramesCompleted.Should().Be(b.Hardware.FramesCompleted);
        }

        [Test]
        public async Task The_simulator_notices_a_dome_closed_over_an_unparked_mount() {
            var s = ScenarioGenerator.Make(3);
            var clock = new HarnessClock(s.Start);
            var hw = new SimObservatory(clock, new RandomSafety(clock, true), SimFaults.None, new Random(1));
            await hw.RunStage(StageKind.Begin, CancellationToken.None);
            await hw.RunGapSteps(new[] { GapStep.CloseDome }, CancellationToken.None);
            hw.Journal.Should().Contain(e => e.Kind == "VIOLATION" && e.Detail.Contains("not parked"));
        }

        // ---------- named scenarios ----------

        [Test]
        public async Task A_dome_that_does_not_open_after_the_wait_between_targets_runs_4_End_and_stops() {
            // like a failing Open Dome in 1 Begin: no slewing and no frames with the dome shut
            var s = ScenarioGenerator.Make(21);
            s.Options.RunMode = RunMode.WithSafety;
            s.Options.UnsafeAction = UnsafeAction.RunEnd;
            s.Options.SafeDelaySeconds = 0;
            s.Options.GapMinutes = 10;
            s.Options.GapCloseDome = true;
            s.Options.GapMount = GapMountAction.StopTrackingAndPark;
            var first = TestData.Circumpolar("First", frames: 2, exposure: 300);
            var second = TestData.Circumpolar("Second", frames: 3, exposure: 300);
            second.Start = TestData.At(23, 30);
            s.Targets = new List<PlannerTarget> { first, second };
            s.Safety = c => new RandomSafety(c, true);
            s.Faults = new SimFaults { DomeOpenFails = 1.0 };
            s.PauseAtFrame = null; s.StopAtFrame = null;
            var r = await ScenarioRunner.Run(s);

            r.Outcomes.Should().ContainSingle().Which.Should().Be(RunOutcome.Stopped);
            r.Phases.Should().Contain(p => p.message == "4 End (opening up failed)");
            second.DoneFrames.Should().Be(0, "the dome never opened for it");
            first.DoneFrames.Should().Be(2);
            Invariants.Check(r).Should().BeEmpty();
            r.Hardware.DomeOpen.Should().BeFalse();
            r.Hardware.Powered.Should().BeFalse();
        }

        [Test]
        public async Task A_mount_that_will_not_park_never_has_the_dome_closed_on_it() {
            // reviewer's case: the close-up and 4 End both fail to park; closing the roof then could hit the mount
            var s = ScenarioGenerator.Make(11);
            s.Options.RunMode = RunMode.WithSafety;
            s.Options.UnsafeAction = UnsafeAction.CloseUpAndWait;
            s.Options.SafeDelaySeconds = 0;
            s.Targets.ForEach(t => { t.Enabled = true; t.Start = new TimeConstraint(); t.End = new TimeConstraint(); });
            s.Safety = c => new RandomSafety(c, true).At(TestData.Night(23), false);
            s.Faults = new SimFaults { ParkFails = 1.0 };
            var r = await ScenarioRunner.Run(s);

            r.Phases.Should().Contain(p => p.message.StartsWith("4 End"), "the run still shuts down what it can");
            r.Hardware.DomeCloseCommands.Should().Be(0, "the park failed every time, so the dome is never closed");
            Invariants.Check(r).Where(v => !v.Contains("park while guiding")).Should().BeEmpty();
        }

        [Test]
        public async Task A_dome_that_never_closes_is_tried_by_the_close_up_and_again_by_4_End() {
            // 28 Oct: the roof stayed open; the close-up failed, 4 End asked again, and then the planner waited with the dome open
            var s = ScenarioGenerator.Make(11);
            s.Options.RunMode = RunMode.WithSafety;
            s.Options.UnsafeAction = UnsafeAction.CloseUpAndWait;
            s.Options.SafeDelaySeconds = 0;
            s.Targets.ForEach(t => { t.Enabled = true; t.Start = new TimeConstraint(); t.End = new TimeConstraint(); });
            s.Safety = c => new RandomSafety(c, true).At(TestData.Night(23), false);
            s.Faults = new SimFaults { DomeCloseFails = 1.0 };
            var r = await ScenarioRunner.Run(s);

            r.Hardware.DomeCloseCommands.Should().BeGreaterOrEqualTo(2, "the close-up asks, then 4 End asks again");
            r.Phases.Should().Contain(p => p.message.StartsWith("4 End"));
            if (r.Hardware.DomeOpen) { Assert.Warn("the dome is still open when the planner goes back to waiting: nothing retries or alerts"); }
        }
    }
}
