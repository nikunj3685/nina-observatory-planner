using FluentAssertions;
using NINA.Equipment.Interfaces;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NUnit.Framework;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>A flat panel (cover and light) and a camera rotator next to the planner.</summary>
    public class AccessoryTests {

        [Test]
        public void Each_frame_type_gets_its_panel_use() {
            FlatPanel.For(ExposureType.Light).Should().Be(PanelUse.Sky);
            FlatPanel.For(ExposureType.Flat).Should().Be(PanelUse.Flat);
            FlatPanel.For(ExposureType.Dark).Should().Be(PanelUse.Dark);
            FlatPanel.For(ExposureType.Bias).Should().Be(PanelUse.Dark);
        }

        [Test]
        public void Light_frames_open_a_closed_cover_and_switch_the_light_off() {
            FlatPanel.Steps(PanelUse.Sky, hasCover: true, CoverState.Closed, hasLight: true, lightOn: true)
                .Should().Be((false, true, false, true), "e.g. after 4 End closed the cover, or after flats");
            FlatPanel.Steps(PanelUse.Sky, hasCover: true, CoverState.Open, hasLight: true, lightOn: false)
                .Should().Be((false, false, false, false), "already open and dark: nothing to do");
        }

        [Test]
        public void Flat_frames_close_the_cover_and_light_the_panel() {
            FlatPanel.Steps(PanelUse.Flat, hasCover: true, CoverState.Open, hasLight: true, lightOn: false)
                .Should().Be((true, false, true, false));
            FlatPanel.Steps(PanelUse.Flat, hasCover: false, CoverState.NotPresent, hasLight: true, lightOn: false)
                .Should().Be((false, false, true, false), "a light panel without a cover: only the light");
        }

        [Test]
        public void Darks_close_the_cover_with_the_light_off() {
            FlatPanel.Steps(PanelUse.Dark, hasCover: true, CoverState.Open, hasLight: true, lightOn: true)
                .Should().Be((true, false, false, true));
            FlatPanel.Steps(PanelUse.Dark, hasCover: false, CoverState.NotPresent, hasLight: false, lightOn: false)
                .Should().Be((false, false, false, false), "a panel with neither: nothing is tried");
        }

        [Test]
        public void Without_NINA_a_rotator_is_assumed_so_rotation_chips_stay_plain() {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "op-rot-" + System.Guid.NewGuid().ToString("N"));
            try {
                var planner = new PlannerService(null, root);
                planner.HasRotator.Should().BeTrue();
                var t = planner.NewTarget("IC 443 1");
                t.Rotate = true;
                t.PositionAngle = 90;
                planner.Targets.Add(t);
                var vm = new UI.PlannerDockableVM(new Moq.Mock<NINA.Profile.Interfaces.IProfileService>().Object, planner) { SelectedTarget = t };
                vm.TargetChips.Should().Contain("Rotate 90°");
            } finally {
                try { System.IO.Directory.Delete(root, true); } catch (System.IO.IOException) { }
            }
        }

        [Test]
        public void Without_a_flat_panel_nothing_happens() {
            FlatPanel.Prepare(null, PanelUse.Flat, null, default).GetAwaiter().GetResult().Should().BeFalse();
        }

        [Test]
        public void Flat_panel_and_rotator_steps_name_their_devices() {
            StageChecks.DevicesOf("CloseCover").Should().Equal("Flat Panel");
            StageChecks.DevicesOf("ToggleLight").Should().Equal("Flat Panel");
            StageChecks.DevicesOf("TrainedFlatExposure").Should().Equal("Camera", "Flat Panel");
            StageChecks.DevicesOf("CenterAndRotate").Should().Contain("Rotator");
            StageChecks.DevicesOf("MoveRotatorMechanical").Should().Equal("Rotator");
        }

        [Test]
        public void Closing_the_cover_after_disconnecting_the_flat_panel_is_reported() {
            var end = new[] {
                new StageEntry("DisconnectEquipment", "Disconnect Equipment", Device: "Flat Panel"),
                new StageEntry("CloseCover", "Close Cover")
            };
            StageChecks.Check(StageKind.End, end, new PlannerOptions { KeepConnected = { } })
                .Should().Contain(w => w.Contains("Close Cover") && w.Contains("Flat Panel"));
        }
    }
}
