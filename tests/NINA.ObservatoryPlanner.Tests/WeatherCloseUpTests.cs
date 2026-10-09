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
    /// The "Close up and wait" weather option: unsafe during the night closes up (stop guiding, park, close the dome) and
    /// keeps power and camera cooling on; safe again opens up without 1 Begin; 4 End runs after the time limit, at the end
    /// of the night or on Stop.
    /// </summary>
    [TestFixture]
    public class WeatherCloseUpTests {

        private sealed class WeatherHardware : IPlannerHardware {
            private readonly FakeHardware inner;
            public bool CloseUpWorks = true;
            public Func<StageKind, CancellationToken, Task> BeforeStage;
            public WeatherHardware(FakeHardware inner) { this.inner = inner; }
            public List<string> Calls => inner.Calls;
            public Task ConnectKeptDevices(CancellationToken token) => inner.ConnectKeptDevices(token);
            public async Task RunStage(StageKind stage, CancellationToken token) {
                if (BeforeStage != null) { await BeforeStage(stage, token); }
                await inner.RunStage(stage, token);
            }
            public Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) => inner.RunTarget(target, canStartFrame, token);
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => inner.RunGapSteps(steps, token);
            public async Task<bool> CloseUp(CancellationToken token) {
                await inner.RunGapSteps(GapPlan.For(GapMountAction.StopTrackingAndPark, closeDome: true).Wait, token);
                return CloseUpWorks;
            }
        }

        private static PlannerOptions CloseUp(double hours = 2) => new() { UnsafeAction = UnsafeAction.CloseUpAndWait, CloseUpMaxHours = hours };

        private static (PlannerEngine engine, WeatherHardware hw, ListLog log, FakeClock clock) Make(PlannerOptions options, Func<FakeClock, ISafetySource> safety, DateTime start, params PlannerTarget[] targets) {
            var clock = new FakeClock(start);
            var hw = new WeatherHardware(new FakeHardware(clock));
            var log = new ListLog();
            var list = targets.ToList();
            var engine = new PlannerEngine(options, TestData.Selector(options), () => list, hw, safety(clock), clock, log) {
                WatchDelay = (d, t) => Task.Delay(1, t)
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

        private const string CloseUpSteps = "gap StopGuiding,StopTracking,Park,CloseDome";
        private const string OpenUpSteps = "gap OpenDome,Unpark";

        [Test]
        public async Task Unsafe_closes_up_and_safe_again_opens_up_without_1_Begin_or_4_End() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var (engine, hw, log, _) = Make(CloseUp(), c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false).At(TestData.Night(22, 30), true),
                TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Contains(OpenUpSteps) && hw.Calls.Count(c => c.StartsWith("target T")) >= 2);

            hw.Calls.Should().ContainInOrder("stage 1", "target T at 21:01", CloseUpSteps, OpenUpSteps);
            hw.Calls.Skip(hw.Calls.IndexOf(OpenUpSteps) + 1).First().Should().Be("target T at 22:30", "the same target goes on, with 2 Start of target");
            hw.Calls.Count(c => c == "stage 1").Should().Be(1, "power, connections and cooling stayed on");
            hw.Calls.Take(hw.Calls.IndexOf(OpenUpSteps)).Should().NotContain("stage 4");
            log.Phases.Should().Contain(p => p.phase == PlannerPhase.ClosedUp && p.message.StartsWith("Closed up for the weather"));
        }

        [Test]
        public async Task Still_unsafe_after_the_limit_runs_4_End() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var (engine, hw, log, clock) = Make(CloseUp(hours: 1), c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false), TestData.Night(21), t);
            DateTime endAt = default;
            hw.BeforeStage = (stage, _) => { if (stage == StageKind.End && endAt == default) { endAt = clock.Now; } return Task.CompletedTask; };
            await RunUntil(engine, () => endAt != default);
            hw.Calls.Should().ContainInOrder(CloseUpSteps, "stage 4");
            log.Phases.Should().Contain(p => p.message == "4 End (still unsafe after 1 h)");
            endAt.Should().BeOnOrAfter(TestData.Night(22, 30)).And.BeBefore(TestData.Night(23, 0));
        }

        [Test]
        public async Task The_end_of_the_night_runs_4_End_before_the_time_limit() {
            var t = TestData.Circumpolar("T", frames: 200, exposure: 300);
            // unsafe at 05:30, dawn (sun above -12°) comes about 06:15 at this site on 2 October
            var (engine, hw, log, _) = Make(CloseUp(hours: 4), c => new ScriptedSafety(c, true).At(TestData.Night(5, 30), false), TestData.Night(5), t);
            await RunUntil(engine, () => hw.Calls.Contains("stage 4"));
            hw.Calls.Should().ContainInOrder(CloseUpSteps, "stage 4");
            log.Phases.Should().Contain(p => p.message == "4 End (the night has ended)");
        }

        [Test]
        public async Task Without_the_option_unsafe_still_runs_4_End() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var (engine, hw, _, _) = Make(new PlannerOptions(), c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false).At(TestData.Night(22, 30), true),
                TestData.Night(21), t);
            await RunUntil(engine, () => hw.Calls.Count(c => c == "stage 1") >= 2);
            hw.Calls.Should().ContainInOrder("target T at 21:01", "stage 4", "stage 1");
            hw.Calls.Should().NotContain(CloseUpSteps);
        }

        [Test]
        public async Task Unsafe_during_1_Begin_runs_4_End() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var safety = new SwitchedSafety();
            var (engine, hw, log, _) = Make(CloseUp(), _ => safety, TestData.Night(21), t);
            hw.BeforeStage = async (stage, token) => {
                if (stage != StageKind.Begin) { return; }
                safety.Safe = false;
                await Task.Delay(200, token); // the watchdog interrupts 1 Begin
            };
            await RunUntil(engine, () => hw.Calls.Contains("stage 4"));
            hw.Calls.Should().NotContain(CloseUpSteps, "1 Begin had not finished: the equipment is only half started");
            log.Phases.Should().Contain(p => p.message == "4 End (unsafe)");
        }

        [Test]
        public async Task A_failed_close_up_runs_4_End_at_once() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var (engine, hw, log, _) = Make(CloseUp(), c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false), TestData.Night(21), t);
            hw.CloseUpWorks = false;
            await RunUntil(engine, () => hw.Calls.Contains("stage 4"));
            hw.Calls.Should().ContainInOrder(CloseUpSteps, "stage 4");
            log.Phases.Should().Contain(p => p.message == "4 End (unsafe)");
            log.Phases.Should().NotContain(p => p.message.StartsWith("Closed up for the weather"));
        }

        [Test]
        public async Task Stop_while_closed_up_runs_4_End() {
            var t = TestData.Circumpolar("T", frames: 20, exposure: 300);
            var (engine, hw, log, _) = Make(CloseUp(hours: 8), c => new ScriptedSafety(c, true).At(TestData.Night(21, 30), false), TestData.Night(21), t);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = engine.RunAsync(cts.Token);
            while (!log.Phases.ToArray().Any(p => p.phase == PlannerPhase.ClosedUp && p.message.StartsWith("Closed up"))) { await Task.Delay(2); }
            engine.RequestStop();
            (await run).Should().Be(RunOutcome.Stopped);
            hw.Calls.Should().ContainInOrder(CloseUpSteps, "stage 4");
            engine.PauseAllowed.Should().BeFalse();
        }

        [Test]
        public async Task The_close_up_ends_at_its_own_dawn_even_with_the_night_setting_off() {
            var t = TestData.Circumpolar("T", frames: 200, exposure: 300);
            var o = CloseUp(hours: 6);
            o.NightLimitEnabled = false;
            o.CloseUpEnd = SunLimit.NauticalDawn;
            var (engine, hw, log, clock) = Make(o, c => new ScriptedSafety(c, true).At(TestData.Night(5, 0), false), TestData.Night(4, 30), t);
            DateTime endAt = default;
            hw.BeforeStage = (stage, _) => { if (stage == StageKind.End && endAt == default) { endAt = clock.Now; } return Task.CompletedTask; };
            await RunUntil(engine, () => endAt != default);
            log.Phases.Should().Contain(p => p.message == "4 End (the night has ended)");
            TestData.Selector(o).SunAltitude(endAt).Should().BeInRange(-12.5, -11, "it ended at nautical dawn, not at the 6 h limit");
        }

        [Test]
        public void The_night_setting_can_be_off_and_uses_NINAs_twilight_names() {
            var o = new PlannerOptions();
            o.NightLimitEnabled.Should().BeTrue();
            o.NightLimit.Should().Be(SunLimit.CivilDawn);
            o.CloseUpEnd.Should().Be(SunLimit.CivilDawn);
            SunLimits.Altitude(SunLimit.AstronomicalDawn).Should().Be(-18);
            SunLimits.Altitude(SunLimit.NauticalDawn).Should().Be(-12);
            SunLimits.Altitude(SunLimit.CivilDawn).Should().Be(-6);
            TestData.Selector(o).IsDark(TestData.Night(15)).Should().BeFalse();
            o.NightLimitEnabled = false;
            TestData.Selector(o).IsDark(TestData.Night(15)).Should().BeTrue("off: the Sun is not considered");
        }

        [Test]
        public void The_weather_options_default_to_4_End_and_a_2_hour_limit() {
            var o = new PlannerOptions();
            o.UnsafeAction.Should().Be(UnsafeAction.RunEnd);
            o.CloseUpMaxHours.Should().Be(2);
            o.CloseUpMaxHours = 0;
            o.CloseUpMaxHours.Should().Be(0.25);
            PlannerStore.Deserialize<PlannerOptions>(PlannerStore.Serialize(CloseUp(3))).Should().BeEquivalentTo(new { UnsafeAction = UnsafeAction.CloseUpAndWait, CloseUpMaxHours = 3.0 });
        }
    }
}
