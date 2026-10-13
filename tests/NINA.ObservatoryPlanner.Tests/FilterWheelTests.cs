using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NUnit.Framework;
using System.Collections.ObjectModel;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Colour cameras without a filter wheel, and NINA's Manual Filter Wheel (its message box would block the run).</summary>
    public class FilterWheelTests {
        private static readonly string[] Wheel = { "L", "Ha", "OIII" };

        [Test]
        public void Manual_wheel_pauses_only_for_a_change_to_another_filter_of_the_profile() {
            PlannerService.ManualChangeNeeded(true, "Ha", "L", null, Wheel).Should().BeTrue("another filter: pause so someone can change it");
            PlannerService.ManualChangeNeeded(true, "ha", "Ha", null, Wheel).Should().BeFalse("the wheel is on it already");
            PlannerService.ManualChangeNeeded(true, "Ha", "L", "Ha", Wheel).Should().BeFalse("after Start sequence the change goes ahead");
            PlannerService.ManualChangeNeeded(true, "Ha", null, null, Wheel).Should().BeTrue("the wheel's filter is unknown");
            PlannerService.ManualChangeNeeded(true, "SII", "L", null, Wheel).Should().BeFalse("not in the profile: NINA does not change filter at all");
            PlannerService.ManualChangeNeeded(true, "", "L", null, Wheel).Should().BeFalse("a row without a filter");
            PlannerService.ManualChangeNeeded(false, "Ha", "L", null, Wheel).Should().BeFalse("a motorised wheel changes by itself");
        }

        private static PlannerTarget Target(string name, params string[] filters) {
            var t = new PlannerTarget { Name = name, Exposures = new ObservableCollection<PlannerExposure>() };
            foreach (var f in filters) { t.Exposures.Add(new PlannerExposure { Filter = f, Count = 10 }); }
            return t;
        }

        [Test]
        public void Manual_wheel_warns_when_the_ticked_targets_use_more_than_one_filter() {
            var a = Target("M 31", "L");
            var b = Target("M 33", "L");
            StageChecks.ManualFilterWheel(new[] { a, b }, manualWheel: true).Should().BeNull("one filter all night: no change needed");

            var c = Target("NGC 7000", "Ha", "OIII");
            StageChecks.ManualFilterWheel(new[] { a, c }, manualWheel: true).Should().Contain("3 filters").And.Contain("Start sequence");
            StageChecks.ManualFilterWheel(new[] { a, c }, manualWheel: false).Should().BeNull("a motorised wheel");

            c.Enabled = false;
            StageChecks.ManualFilterWheel(new[] { a, c }, manualWheel: true).Should().BeNull("only ticked targets count");
        }

        [Test]
        public void Without_a_filter_wheel_frame_numbers_count_all_rows_alike() {
            var t = Target("M 31", "", "L");
            t.Exposures[0].Done = 2;
            t.Exposures[1].Done = 1;
            ExposurePlanner.FramesTakenLike(t, t.Exposures[1]).Should().Be(1, "with a filter wheel \"L\" counts on its own");
            ExposurePlanner.FramesTakenLike(t, t.Exposures[1], anyFilter: true).Should().Be(3, "a colour camera: the next frame is the 4th");
        }

        [Test]
        public void Without_NINA_a_filter_wheel_is_assumed_so_new_rows_keep_a_filter() {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "op-fw-" + System.Guid.NewGuid().ToString("N"));
            try {
                var planner = new PlannerService(null, root);
                planner.HasFilterWheel.Should().BeTrue();
                planner.ManualFilterWheel.Should().BeFalse();
                planner.NewTarget().Exposures[0].Filter.Should().Be("L");
            } finally {
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }
    }
}
