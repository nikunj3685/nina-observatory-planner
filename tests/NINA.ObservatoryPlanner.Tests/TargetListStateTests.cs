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
        public void Complete_wins_over_everything() {
            var t = TestData.Circumpolar("M31", frames: 2);
            t.Exposures[0].Done = 2;
            State(t, new TargetListState(t.Id, null)).Icon.Should().Be("✔");
            t.Enabled = false;
            State(t).Icon.Should().Be("✔");
        }
    }
}
