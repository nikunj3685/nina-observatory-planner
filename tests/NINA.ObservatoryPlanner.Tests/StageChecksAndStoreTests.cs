using FluentAssertions;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NINA.ObservatoryPlanner.Tests {

    [TestFixture]
    public class StageChecksTests {
        private static StageEntry E(string type, string device = null) => new(type, type, device);

        [Test]
        public void Default_end_stage_has_no_warnings() {
            var end = new[] { E("StopGuiding"), E("ParkScope"), E("CloseDomeShutter"), E("WarmCamera"),
                E("DisconnectEquipment", "Camera"), E("DisconnectEquipment", "Mount"), E("SetSwitchValue") };
            StageChecks.Check(StageKind.End, end, new PlannerOptions()).Should().BeEmpty();
        }

        [Test]
        public void Disconnecting_a_kept_device_warns() {
            var w = StageChecks.Check(StageKind.End, new[] { E("DisconnectEquipment", "Dome") }, new PlannerOptions());
            w.Should().ContainSingle().Which.Should().Contain("Dome is set to stay connected");
        }

        [Test]
        public void Disconnect_all_names_the_kept_devices() {
            var w = StageChecks.Check(StageKind.End, new[] { E("DisconnectAllEquipment") }, new PlannerOptions());
            w.Should().ContainSingle().Which.Should().Contain("Safety Monitor, Switch hub, Dome");
        }

        [Test]
        public void Equipment_step_after_disconnect_warns() {
            var w = StageChecks.Check(StageKind.End, new[] { E("DisconnectEquipment", "Camera"), E("TakeExposure") }, new PlannerOptions());
            w.Should().ContainSingle().Which.Should().Contain("runs after equipment starts disconnecting");
        }

        [Test]
        public void Equipment_step_before_connect_warns() {
            var w = StageChecks.Check(StageKind.Begin, new[] { E("CoolCamera"), E("ConnectAllEquipment") }, new PlannerOptions());
            w.Should().ContainSingle().Which.Should().Contain("runs before equipment is connected");
        }

        [Test]
        public void Rotate_with_af_after_filter_change_names_the_target() {
            var t = TestData.Circumpolar("IC1396");
            t.Order = ExposureOrder.RotateThroughFilters;
            t.Exposures.Add(new PlannerExposure { Filter = "OIII", Count = 5 });
            StageChecks.RotateWithFilterAf(new[] { t }, new[] { E("AutofocusAfterFilterChange") }).Should().Contain("IC1396");
            t.Order = ExposureOrder.FinishEachRowFirst;
            StageChecks.RotateWithFilterAf(new[] { t }, new[] { E("AutofocusAfterFilterChange") }).Should().BeNull();
        }

        [Test]
        public void Safety_monitor_is_always_kept_with_safety() {
            var o = new PlannerOptions();
            o.KeepConnected.Clear();
            o.Keeps("Safety Monitor").Should().BeTrue();
            o.RunMode = RunMode.WithoutSafety;
            o.Keeps("Safety Monitor").Should().BeFalse();
        }
    }

    [TestFixture]
    public class StoreTests {
        private string root;

        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "op-tests-" + Guid.NewGuid().ToString("N")); }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

        [Test]
        public void Target_list_round_trip_keeps_settings_and_progress() {
            var store = new PlannerStore(root);
            var t = TestData.Circumpolar("IC1396", frames: 100);
            t.Exposures[0].Done = 8;
            t.End = TestData.At(1, 0);
            t.Order = ExposureOrder.RotateThroughFilters;
            var path = Path.Combine(store.TargetsFolder, "Autumn.json");
            store.SaveTargetList(new TargetList { Name = "Autumn 2026", Targets = { t } }, path);

            var loaded = store.LoadTargetList(path);
            loaded.Name.Should().Be("Autumn 2026");
            var back = loaded.Targets.Single();
            back.Exposures.Single().Done.Should().Be(8);
            back.End.Time.Should().Be(new TimeSpan(1, 0, 0));
            back.Order.Should().Be(ExposureOrder.RotateThroughFilters);
            store.LastTargetListPath().Should().Be(path);
        }

        [Test]
        public void Options_round_trip() {
            var store = new PlannerStore(root);
            var o = new PlannerOptions { RunMode = RunMode.WithoutSafety, GapMinutes = 45, GapMount = GapMountAction.StopTrackingAndFindHome };
            o.KeepConnected.Add("Mount");
            store.SaveOptions(o);
            var back = store.LoadOptions();
            back.RunMode.Should().Be(RunMode.WithoutSafety);
            back.GapMinutes.Should().Be(45);
            back.GapMount.Should().Be(GapMountAction.StopTrackingAndFindHome);
            back.KeepConnected.Should().Equal("Safety Monitor", "Switch", "Dome", "Mount");
        }

        [Test]
        public void A_damaged_options_file_falls_back_to_defaults() {
            var store = new PlannerStore(root);
            File.WriteAllText(Path.Combine(root, "options.json"), "{ this is not json");
            store.LoadOptions().GapMinutes.Should().Be(30);
        }
    }
}
