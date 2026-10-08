using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Item 5: the guide star is lost. Ignore short blips, wait for it, then stop for the night or go to the next target.</summary>
    [TestFixture]
    public class GuideStarLostTests {

        // ---------- the watch ----------

        [Test]
        public void A_loss_shorter_than_10_s_is_ignored_and_a_longer_one_drops_the_frame() {
            var now = new DateTime(2026, 10, 1, 22, 0, 0);
            var lost = false;
            var watch = new GuiderWatch(() => lost, () => now);

            watch.Poll().Should().BeFalse();
            lost = true;
            watch.Poll().Should().BeFalse();
            now = now.AddSeconds(9);
            watch.Poll().Should().BeFalse("9 s is a blip");
            lost = false;
            watch.Poll().Should().BeFalse();
            watch.BadSince.Should().BeNull("the star came back");

            lost = true;
            watch.Poll().Should().BeFalse();
            now = now.AddSeconds(10);
            watch.Poll().Should().BeTrue("lost for 10 s");
        }

        [Test]
        public async Task The_wait_ends_when_the_star_is_found_again() {
            var now = new DateTime(2026, 10, 1, 22, 0, 0);
            var lostUntil = now.AddSeconds(25);
            var watch = new GuiderWatch(() => now < lostUntil, () => now);
            var statuses = new List<TimeSpan>();
            var found = await watch.WaitUntilGood(TimeSpan.FromSeconds(60), (d, t) => { now += d; return Task.CompletedTask; }, statuses.Add, CancellationToken.None);
            found.Should().BeTrue();
            statuses.First().Should().Be(TimeSpan.FromSeconds(60));
            statuses.Should().HaveCount(25, "the time left is shown every second");
            watch.BadSince.Should().BeNull();
        }

        [Test]
        public async Task The_wait_counts_from_when_the_star_was_lost_and_gives_up() {
            var start = new DateTime(2026, 10, 1, 22, 0, 0);
            var now = start;
            var watch = new GuiderWatch(() => true, () => now);
            watch.Poll();                 // lost at 22:00:00
            now = now.AddSeconds(12);     // the frame was dropped at 10 s, a moment later the wait starts
            var found = await watch.WaitUntilGood(TimeSpan.FromSeconds(60), (d, t) => { now += d; return Task.CompletedTask; }, null, CancellationToken.None);
            found.Should().BeFalse();
            now.Should().Be(start.AddSeconds(60), "60 s from when it was lost, not from when the wait started");
        }

        [Test]
        public async Task Unsafe_weather_during_the_wait_cancels_it() {
            var watch = new GuiderWatch(() => true, () => DateTime.Now);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> wait = () => watch.WaitUntilGood(TimeSpan.FromSeconds(60), (d, t) => Task.Delay(d, t), null, cts.Token);
            await wait.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public void Options_default_to_60_s_and_stopping_for_the_night() {
            var o = new PlannerOptions();
            o.GuideLostWatch.Should().BeTrue();
            o.GuideLostWaitSeconds.Should().Be(60);
            o.GuideLostAction.Should().Be(GuideLostAction.StopForNight);
            o.GuideLostWaitSeconds = 3;
            o.GuideLostWaitSeconds.Should().Be(10, "at least the 10 s that a blip is allowed");
            PlannerStore.Deserialize<PlannerOptions>(PlannerStore.Serialize(new PlannerOptions { GuideLostAction = GuideLostAction.NextTarget, GuideLostWaitSeconds = 90 }))
                .Should().BeEquivalentTo(new { GuideLostAction = GuideLostAction.NextTarget, GuideLostWaitSeconds = 90 });
        }

        // ---------- the engine ----------

        /// <summary>FakeHardware where the guide star is lost for good on the named targets.</summary>
        private sealed class LostStarHardware : IPlannerHardware {
            private readonly FakeHardware inner;
            private readonly HashSet<string> lostOn;
            public LostStarHardware(FakeHardware inner, params string[] lostOn) { this.inner = inner; this.lostOn = lostOn.ToHashSet(); }
            public List<string> Calls => inner.Calls;
            public Task ConnectKeptDevices(CancellationToken token) => inner.ConnectKeptDevices(token);
            public Task RunStage(StageKind stage, CancellationToken token) => inner.RunStage(stage, token);
            public Task RunGapSteps(IReadOnlyList<GapStep> steps, CancellationToken token) => inner.RunGapSteps(steps, token);
            public async Task<int> RunTarget(PlannerTarget target, Func<PlannerExposure, bool> canStartFrame, CancellationToken token) {
                if (!lostOn.Contains(target.Name)) { return await inner.RunTarget(target, canStartFrame, token); }
                inner.Calls.Add($"target {target.Name} (star lost)");
                await Task.Yield();
                throw new GuideStarLostException(target.Name);
            }
        }

        private static (PlannerEngine engine, LostStarHardware hw, ListLog log, FakeClock clock) Make(PlannerOptions options, ISafetySource safety, FakeClock clock, string[] lostOn, params PlannerTarget[] targets) {
            var hw = new LostStarHardware(new FakeHardware(clock), lostOn);
            var log = new ListLog();
            var engine = new PlannerEngine(options, TestData.Selector(options), () => targets.ToList(), hw, safety, clock, log) {
                WatchDelay = (d, t) => Task.Delay(1, t)
            };
            return (engine, hw, log, clock);
        }

        [Test]
        public async Task Next_target_skips_the_target_for_tonight_and_images_the_next_one() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var t2 = TestData.Circumpolar("T2", frames: 2, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var (engine, hw, log, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety, GuideLostAction = GuideLostAction.NextTarget },
                new ScriptedSafety(clock, false), clock, new[] { "T1" }, t1, t2);
            var outcome = await engine.RunAsync(CancellationToken.None);

            outcome.Should().Be(RunOutcome.Finished);
            hw.Calls.Should().Equal("connect", "stage 1", "target T1 (star lost)", "target T2 at 21:01", "stage 4");
            t2.IsComplete.Should().BeTrue();
            log.Phases.Should().Contain(p => p.message == "4 End (finished)");
        }

        [Test]
        public async Task Next_target_with_no_other_target_runs_4_End() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var (engine, hw, _, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety, GuideLostAction = GuideLostAction.NextTarget },
                new ScriptedSafety(clock, false), clock, new[] { "T1" }, t1);
            await engine.RunAsync(CancellationToken.None);
            hw.Calls.Should().Equal("connect", "stage 1", "target T1 (star lost)", "stage 4");
        }

        [Test]
        public async Task Stop_for_the_night_runs_4_End_and_ends_a_run_without_safety() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var t2 = TestData.Circumpolar("T2", frames: 2, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var (engine, hw, log, _) = Make(new PlannerOptions { RunMode = RunMode.WithoutSafety }, new ScriptedSafety(clock, false), clock, new[] { "T1" }, t1, t2);
            var outcome = await engine.RunAsync(CancellationToken.None);

            outcome.Should().Be(RunOutcome.Finished);
            hw.Calls.Should().Equal("connect", "stage 1", "target T1 (star lost)", "stage 4");
            log.Phases.Should().Contain(p => p.message == "4 End (guide star lost)");
            log.Phases.Last().message.Should().Be("Stopped for the night: the guide star was lost and 4 End has run");
        }

        [Test]
        public async Task Stop_for_the_night_with_safety_stays_off_until_the_next_night_although_it_is_safe() {
            var t1 = TestData.Circumpolar("T1", frames: 3, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var (engine, hw, log, _) = Make(new PlannerOptions(), new ScriptedSafety(clock, true), clock, new[] { "T1" }, t1);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = engine.RunAsync(cts.Token);
            // wait until the next night has begun (noon) and 1 Begin ran again
            while (hw.Calls.Count(c => c == "stage 1") < 2 && !run.IsCompleted && !cts.IsCancellationRequested) { await Task.Delay(5); }
            var beginAgainAt = clock.Now;
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }

            hw.Calls.Take(4).Should().Equal("connect", "stage 1", "target T1 (star lost)", "stage 4");
            log.Phases.Should().Contain(p => p.phase == PlannerPhase.WaitingForNextNight && p.message.StartsWith("Stopped for tonight: the guide star was lost"));
            hw.Calls.Count(c => c == "stage 1").Should().Be(2, "1 Begin runs again only on the next night");
            beginAgainAt.Should().BeOnOrAfter(TestData.Night(12).AddDays(1), "not before the next night starts (local noon)");
        }
    }
}
