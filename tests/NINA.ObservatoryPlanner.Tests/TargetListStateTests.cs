using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.UI;
using NUnit.Framework;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>The target list icons: ▶ Imaging, ‖ Paused, ■ Stopped, ✔ Complete.</summary>
    [TestFixture]
    public class TargetListStateTests {

        private static (string Icon, string Brush, string Tip) State(PlannerTarget t, TargetListState s = null) => TargetStateConverter.StateOf(t, s);

        [Test]
        public void Checked_and_not_running_is_paused_in_grey() {
            var t = TestData.Circumpolar("M31");
            State(t).Should().Be(("‖", "OP_Muted", "Paused: checked, not being imaged now"));
        }

        [Test]
        public void Unchecked_or_nothing_to_take_is_stopped() {
            var off = TestData.Circumpolar("M31");
            off.Enabled = false;
            State(off).Icon.Should().Be("■");
            State(off).Tip.Should().Be("Stopped: not checked");

            var rowsOff = TestData.Circumpolar("M33");
            rowsOff.Exposures[0].Enabled = false;
            State(rowsOff).Icon.Should().Be("■");
            State(rowsOff).Tip.Should().StartWith("Stopped: no exposures to take");

            var repeatZero = TestData.Circumpolar("M42");
            repeatZero.Exposures[0].Count = 0;
            State(repeatZero).Icon.Should().Be("■");
        }

        [Test]
        public void The_run_shows_imaging_or_paused_in_orange_on_its_target() {
            var t = TestData.Circumpolar("M31");
            var other = TestData.Circumpolar("M33");
            State(t, new TargetListState(t.Id, null)).Should().Be(("▶", "OP_Accent", "Imaging: the run is on this target now"));
            State(other, new TargetListState(t.Id, null)).Icon.Should().Be("‖", "only the target being imaged shows ▶");
            State(t, new TargetListState(null, t.Id)).Should().Be(("‖", "OP_Accent", "Paused: the run is paused on this target; Start sequence continues with it"));
        }

        [Test]
        public void Frame_numbers_continue_from_the_frames_taken_with_the_same_filter_time_and_binning() {
            // night 1 took 12 Ha 300 s frames; night 2 must continue NINA's $$FRAMENR$$ at 12, not 0
            var t = TestData.Circumpolar("M31", frames: 50, exposure: 300);
            var ha = t.Exposures[0];
            ha.Done = 12;
            var ha2 = new PlannerExposure { Filter = "ha", ExposureTime = 300, Count = 10, Done = 3 };
            var oiii = new PlannerExposure { Filter = "OIII", ExposureTime = 300, Count = 10, Done = 7 };
            var ha60 = new PlannerExposure { Filter = "Ha", ExposureTime = 60, Count = 10, Done = 5 };
            var dark = new PlannerExposure { Filter = "Ha", ExposureTime = 300, Count = 10, Done = 4, Type = ExposureType.Dark };
            foreach (var e in new[] { ha2, oiii, ha60, dark }) { t.Exposures.Add(e); }
            ExposurePlanner.FramesTakenLike(t, ha).Should().Be(15, "both Ha 300 s rows count together, as NINA counts them");
            ExposurePlanner.FramesTakenLike(t, oiii).Should().Be(7);
            ExposurePlanner.FramesTakenLike(t, ha60).Should().Be(5);
        }

        [Test]
        public void Complete_wins_over_everything() {
            var t = TestData.Circumpolar("M31", frames: 2);
            t.Exposures[0].Done = 2;
            State(t, new TargetListState(t.Id, null)).Icon.Should().Be("✔");
            t.Enabled = false;
            State(t).Icon.Should().Be("✔");
        }
    }
}
