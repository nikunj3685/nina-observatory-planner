using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NUnit.Framework;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Item 18: start and keep light frames only while the guiding error is below the limit (pixels).</summary>
    [TestFixture]
    public class GuidingCheckTests {
        private static readonly DateTime T0 = new(2026, 10, 1, 22, 0, 0);

        [Test]
        public void The_error_is_the_rms_total_of_the_last_10_steps_in_pixels() {
            var w = new GuideErrorWindow();
            w.Add(T0, 0.3, 0.4);
            w.Add(T0.AddSeconds(2), 0.3, 0.4);
            w.Total(T0.AddSeconds(2)).Should().BeNull("fewer than 3 steps say nothing yet");
            w.Add(T0.AddSeconds(4), 0.3, 0.4);
            w.Total(T0.AddSeconds(4)).Should().BeApproximately(0.5, 1e-9, "each step is 0.5 px from the lock position");

            for (var i = 0; i < 10; i++) { w.Add(T0.AddSeconds(6 + 2 * i), 1.2, -1.6); }
            w.Total(T0.AddSeconds(30)).Should().BeApproximately(2.0, 1e-9, "only the last 10 steps count, and the sign doesn't matter");
        }

        [Test]
        public void A_constant_offset_counts_as_error() {
            var w = new GuideErrorWindow();
            for (var i = 0; i < 10; i++) { w.Add(T0.AddSeconds(i), 1.0, 0); }
            w.Total(T0.AddSeconds(10)).Should().BeApproximately(1.0, 1e-9, "a steady 1 px offset is 1 px of error, not 0");
        }

        [Test]
        public void Old_steps_and_steps_before_a_dither_do_not_count() {
            var w = new GuideErrorWindow();
            for (var i = 0; i < 5; i++) { w.Add(T0.AddSeconds(i), 3, 3); }
            w.Total(T0.AddSeconds(5) + GuideErrorWindow.MaxAge).Should().BeNull("guiding stopped a minute ago");
            w.Total(T0.AddSeconds(5)).Should().NotBeNull();
            w.Clear();
            w.Total(T0.AddSeconds(5)).Should().BeNull("a dither or a guiding restart starts over");
        }

        [Test]
        public void An_error_above_the_limit_for_10_s_restarts_the_frame_but_a_dip_below_resets_it() {
            var now = T0;
            var error = 1.5;
            var watch = new GuiderWatch(() => error > 1.0, () => now);
            watch.Poll().Should().BeFalse();
            now = now.AddSeconds(8);
            watch.Poll().Should().BeFalse();
            error = 0.8;
            watch.Poll().Should().BeFalse();
            error = 1.5;
            now = now.AddSeconds(1);
            watch.Poll().Should().BeFalse();
            now = now.AddSeconds(9);
            watch.Poll().Should().BeFalse("9 s above the limit since the dip");
            now = now.AddSeconds(1);
            watch.Poll().Should().BeTrue("10 s above the limit without a break");
        }

        [Test]
        public async Task Before_a_frame_the_wait_runs_out_after_the_set_time() {
            var now = T0;
            var watch = new GuiderWatch(() => true, () => now);
            var good = await watch.WaitUntilGood(TimeSpan.FromSeconds(120), (d, t) => { now += d; return Task.CompletedTask; }, null, CancellationToken.None);
            good.Should().BeFalse("the frame then starts anyway");
            now.Should().Be(T0.AddSeconds(120));
        }

        [Test]
        public void The_check_is_off_by_default_and_unknown_errors_never_hold_a_frame() {
            var root = Path.Combine(Path.GetTempPath(), "op-guiding-" + Guid.NewGuid().ToString("N"));
            try {
                var planner = new PlannerService(null, root);
                planner.Options.GuidingCheck.Should().BeFalse();
                planner.Options.GuidingLimitPixels.Should().Be(1.0);
                planner.Options.GuidingCheckWaitSeconds.Should().Be(120);

                double? error = 1.5;
                planner.GuidingError = () => error;
                planner.GuidingErrorAbove().Should().BeFalse("the check is off");
                planner.Options.GuidingCheck = true;
                planner.GuidingErrorAbove().Should().BeTrue();
                error = 0.9;
                planner.GuidingErrorAbove().Should().BeFalse();
                error = null;
                planner.GuidingErrorAbove().Should().BeFalse("no recent guide steps: not guiding, nothing to wait for");
                planner.Options.GuidingLimitPixels = 0;
                planner.Options.GuidingLimitPixels.Should().Be(0.1);
            } finally {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }
    }
}
