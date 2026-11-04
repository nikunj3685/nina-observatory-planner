using FluentAssertions;
using Newtonsoft.Json;
using NINA.ObservatoryPlanner.Core;
using NUnit.Framework;
using System.Collections.ObjectModel;
using System.Linq;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>Who handles the move and the guiding at the start of each target, and the calibration targets that skip stages 2 and 3.</summary>
    [TestFixture]
    public class Stage2Tests {
        private static StageEntry E(string type, string name = null) => new(type, name ?? type);

        private static PlannerTarget Target(string name, params ExposureType[] rows) {
            var t = new PlannerTarget { Name = name };
            t.Exposures = new ObservableCollection<PlannerExposure>(rows.Select(r => new PlannerExposure { Type = r }));
            return t;
        }

        private static PlannerOptions Planner() => new() { Stage2Handler = Stage2Handler.Planner };
        private static PlannerOptions Sequencer() => new() { Stage2Handler = Stage2Handler.AdvancedSequencer };

        // ---------- defaults and saved data ----------

        [Test]
        public void New_options_and_old_saved_ones_default_to_the_planner() {
            new PlannerOptions().Stage2Handler.Should().Be(Stage2Handler.Planner);
            JsonConvert.DeserializeObject<PlannerOptions>("{\"GapMinutes\": 20}").Stage2Handler.Should().Be(Stage2Handler.Planner);
        }

        [Test]
        public void The_handler_is_saved_and_copied() {
            var json = JsonConvert.SerializeObject(Sequencer());
            JsonConvert.DeserializeObject<PlannerOptions>(json).Stage2Handler.Should().Be(Stage2Handler.AdvancedSequencer);
            var copy = new PlannerOptions();
            copy.CopyFrom(Sequencer());
            copy.Stage2Handler.Should().Be(Stage2Handler.AdvancedSequencer);
        }

        [Test]
        public void Targets_saved_before_guiding_was_a_setting_load_with_guiding_on() {
            var old = JsonConvert.DeserializeObject<PlannerTarget>("{\"Name\": \"M31\", \"OnStart\": \"DoNotMove\"}");
            old.GuideOnStart.Should().BeTrue();
            old.OnStart.Should().Be(OnStartAction.DoNotMove);
        }

        [Test]
        public void Guiding_is_saved_and_cloned() {
            var t = new PlannerTarget { GuideOnStart = false };
            JsonConvert.DeserializeObject<PlannerTarget>(JsonConvert.SerializeObject(t)).GuideOnStart.Should().BeFalse();
            t.Clone(keepProgress: false).GuideOnStart.Should().BeFalse();
        }

        // ---------- calibration targets ----------

        [Test]
        public void A_target_with_only_darks_bias_and_flats_ticked_is_a_calibration_target() {
            Target("Cal", ExposureType.Dark, ExposureType.Bias, ExposureType.Flat).IsCalibrationOnly.Should().BeTrue();
            Target("Flats", ExposureType.Flat).IsCalibrationOnly.Should().BeTrue();
        }

        [Test]
        public void One_ticked_light_row_makes_it_a_normal_target() {
            Target("Mixed", ExposureType.Light, ExposureType.Dark).IsCalibrationOnly.Should().BeFalse();
            Target("Lights", ExposureType.Light).IsCalibrationOnly.Should().BeFalse();
        }

        [Test]
        public void Only_the_ticked_rows_decide() {
            var t = Target("Mixed", ExposureType.Light, ExposureType.Dark);
            t.Exposures[0].Enabled = false;
            t.IsCalibrationOnly.Should().BeTrue("the light row is switched off");
            t.Exposures[0].Enabled = true;
            t.Exposures[1].Enabled = false;
            t.IsCalibrationOnly.Should().BeFalse();
        }

        [Test]
        public void A_target_with_no_ticked_rows_is_not_a_calibration_target() {
            var t = Target("Empty", ExposureType.Dark);
            t.Exposures[0].Enabled = false;
            t.IsCalibrationOnly.Should().BeFalse();
            Target("None").IsCalibrationOnly.Should().BeFalse();
        }

        [Test]
        public void Old_dark_flat_rows_count_as_darks() {
            Target("DF", ExposureType.DarkFlat).IsCalibrationOnly.Should().BeTrue();
        }

        // ---------- the stage check warnings ----------

        [Test]
        public void Planner_mode_warns_that_the_stages_move_and_guiding_steps_are_ignored() {
            var w = StageChecks.Stage2(new[] { Target("M31", ExposureType.Light), Target("M42", ExposureType.Light) },
                new[] { E("Center", "Center"), E("StartGuiding", "Start Guiding") }, Planner());
            w.Should().HaveCount(2);
            w[0].Should().Contain("\"Center\"").And.Contain("ignored for M31, M42");
            w[1].Should().Contain("\"Start Guiding\"").And.Contain("ignored for M31, M42");
        }

        [Test]
        public void Planner_mode_with_an_empty_stage_has_no_warnings() {
            StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, new[] { E("WaitForTimeSpan") }, Planner()).Should().BeEmpty();
        }

        [Test]
        public void Advanced_sequencer_mode_warns_when_nothing_moves_or_guides() {
            var w = StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, new[] { E("WaitForTimeSpan") }, Sequencer());
            w.Should().HaveCount(2);
            w[0].Should().Contain("the mount will not move for M31");
            w[1].Should().Contain("guiding will not start for M31");
        }

        [Test]
        public void Advanced_sequencer_mode_with_both_steps_is_quiet() {
            StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, new[] { E("CenterAndRotate"), E("StartGuiding") }, Sequencer()).Should().BeEmpty();
            StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, new[] { E("SlewScopeToRaDec"), E("StartGuiding") }, Sequencer()).Should().BeEmpty();
        }

        [Test]
        public void The_warnings_leave_out_calibration_targets_and_targets_that_are_off_or_done() {
            var off = Target("Off", ExposureType.Light); off.Enabled = false;
            var done = Target("Done", ExposureType.Light); done.Exposures[0].Count = 1; done.Exposures[0].Done = 1;
            var w = StageChecks.Stage2(new[] { Target("Flats", ExposureType.Flat), off, done, Target("M31", ExposureType.Light) },
                new[] { E("Center") }, Planner());
            w.Should().ContainSingle().Which.Should().EndWith("ignored for M31. Remove it, or set Stage 2 to the Advanced Sequencer.");
            StageChecks.Stage2(new[] { Target("Flats", ExposureType.Flat) }, new[] { E("Center") }, Planner()).Should().BeEmpty("only calibration targets: stage 2 is skipped for all of them");
            StageChecks.Stage2(new[] { Target("Flats", ExposureType.Flat) }, System.Array.Empty<StageEntry>(), Sequencer()).Should().BeEmpty();
        }

        [Test]
        public void Many_targets_are_listed_with_a_count() {
            var targets = Enumerable.Range(1, 6).Select(n => Target("T" + n, ExposureType.Light)).ToList();
            StageChecks.Stage2(targets, new[] { E("Center") }, Planner()).Should().ContainSingle().Which.Should().Contain("T1, T2, T3, T4 and 2 more");
        }

        // ---------- steps switched off in the Advanced Sequencer ----------

        [Test]
        public void A_disabled_move_or_guiding_step_does_not_count_in_advanced_sequencer_mode() {
            var items = new[] { new StageEntry("Center", "Center", Disabled: true), new StageEntry("StartGuiding", "Start Guiding", Disabled: true) };
            var w = StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, items, Sequencer());
            w.Should().HaveCount(2);
            w[0].Should().Contain("will not move");
            w[1].Should().Contain("guiding will not start");
        }

        [Test]
        public void A_disabled_step_is_not_reported_as_ignored_in_planner_mode() {
            var items = new[] { new StageEntry("Center", "Center", Disabled: true) };
            StageChecks.Stage2(new[] { Target("M31", ExposureType.Light) }, items, Planner()).Should().BeEmpty();
        }

        [Test]
        public void A_switched_off_stage_2_is_reported_in_either_mode() {
            var targets = new[] { Target("M31", ExposureType.Light) };
            foreach (var options in new[] { Planner(), Sequencer() }) {
                StageChecks.Stage2(targets, new[] { E("Center") }, options, stageOff: true)
                    .Should().ContainSingle().Which.Should().Contain("switched off").And.Contain("no move and no guiding for M31");
            }
            StageChecks.Stage2(new[] { Target("Flats", ExposureType.Flat) }, System.Array.Empty<StageEntry>(), Planner(), stageOff: true).Should().BeEmpty();
        }

        [Test]
        public void The_order_checks_ignore_disabled_steps() {
            // 1 Begin: the unpark step is switched off, so nothing unparks the mount
            var begin = new[] { E("ConnectAllEquipment"), new StageEntry("UnparkScope", "Unpark Scope", Disabled: true) };
            StageChecks.Check(StageKind.Begin, begin, new PlannerOptions()).Should().ContainSingle().Which.Should().Contain("unparks the mount");
            // a switched-off Disconnect Equipment for a kept device is not a problem
            var end = new[] { new StageEntry("DisconnectEquipment", "Disconnect Equipment", "Dome", Disabled: true) };
            StageChecks.Check(StageKind.End, end, new PlannerOptions()).Should().BeEmpty();
        }

        [Test]
        public void Move_and_guiding_instructions_are_recognised_by_type_name() {
            new[] { "Center", "CenterAndRotate", "SlewScopeToRaDec" }.All(StageChecks.IsMove).Should().BeTrue();
            StageChecks.IsMove("SlewScopeToAltAz").Should().BeFalse();
            StageChecks.IsGuiding("StartGuiding").Should().BeTrue();
            StageChecks.IsGuiding("StopGuiding").Should().BeFalse();
        }
    }
}
