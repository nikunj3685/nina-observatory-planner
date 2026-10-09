using FluentAssertions;
using Moq;
using NINA.Core.Enum;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using NINA.ObservatoryPlanner.Core;
using NINA.ObservatoryPlanner.Nina;
using NINA.ObservatoryPlanner.UI.Dialogs;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Trigger.Autofocus;
using NINA.Sequencer.Utility;
using NINA.WPF.Base.Interfaces;
using NINA.WPF.Base.Interfaces.ViewModel;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NINA.ObservatoryPlanner.Tests {

    /// <summary>The 1.1 fixes: countdown, start at the set time, no pause in 4 End, exposure type, linked constraints, error stops.</summary>
    [TestFixture]
    public class ImprovementTests {

        [TestCase(42, "0:42")]
        [TestCase(0.2, "0:01")]
        [TestCase(0, "0:00")]
        [TestCase(-5, "0:00")]
        [TestCase(600, "10:00")]
        [TestCase(3725, "1:02:05")]
        public void Countdown_shows_minutes_and_seconds(double seconds, string expected) {
            PlannerEngine.Countdown(TimeSpan.FromSeconds(seconds)).Should().Be(expected);
        }

        [Test]
        public async Task Wait_after_safe_shows_a_countdown_every_second() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var hw = new FakeHardware(clock);
            var log = new ListLog();
            var options = new PlannerOptions { SafeDelaySeconds = 5 };
            var engine = new PlannerEngine(options, TestData.Selector(options), () => new List<PlannerTarget> { t }, hw, new ScriptedSafety(clock, true), clock, log) {
                WatchDelay = (d, tk) => Task.Delay(1, tk)
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var run = engine.RunAsync(cts.Token);
            while (!hw.Calls.Contains("stage 1") && !run.IsCompleted && !cts.IsCancellationRequested) { await Task.Delay(5); }
            cts.Cancel();
            try { await run; } catch (OperationCanceledException) { }

            log.Statuses.Should().ContainInOrder(
                "Safe: 1 Begin starts in 0:05 if it stays safe", "Safe: 1 Begin starts in 0:04 if it stays safe", "Safe: 1 Begin starts in 0:03 if it stays safe",
                "Safe: 1 Begin starts in 0:02 if it stays safe", "Safe: 1 Begin starts in 0:01 if it stays safe");
        }

        [Test]
        public async Task Waiting_for_the_next_target_counts_down_to_its_start_time() {
            var t1 = TestData.Circumpolar("Later", frames: 1, exposure: 60);
            t1.Start = TestData.At(21, 10);
            var clock = new FakeClock(TestData.Night(21));
            var hw = new FakeHardware(clock);
            var log = new ListLog();
            var options = new PlannerOptions { RunMode = RunMode.WithoutSafety };
            var engine = new PlannerEngine(options, TestData.Selector(options), () => new List<PlannerTarget> { t1 }, hw, new ScriptedSafety(clock, false), clock, log);
            await engine.RunAsync(CancellationToken.None);

            log.Statuses.Should().Contain(s => s.StartsWith("Waiting for Later at 21:10 (in 9:"), "1 Begin took a minute in this rig, so about 9 minutes are left");
            hw.Calls.Should().Contain("target Later at 21:10", "the target starts at its set time");
        }

        [Test]
        public async Task Pause_is_ignored_while_4_End_runs() {
            var t = TestData.Circumpolar("T", frames: 1, exposure: 60);
            var clock = new FakeClock(TestData.Night(21));
            var hw = new PauseHardware(clock);
            var log = new ListLog();
            var options = new PlannerOptions { RunMode = RunMode.WithoutSafety };
            var engine = new PlannerEngine(options, TestData.Selector(options), () => new List<PlannerTarget> { t }, hw, new SwitchedSafety(), clock, log) {
                WatchDelay = (d, tk) => Task.Delay(1, tk)
            };
            var inEndSeen = false;
            hw.OnStage = stage => {
                if (stage != StageKind.End) { return; }
                inEndSeen = engine.InEnd;
                engine.RequestPause(PauseKind.Now);
                engine.RequestPause(PauseKind.AfterFrame);
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var outcome = await engine.RunAsync(cts.Token);

            inEndSeen.Should().BeTrue();
            outcome.Should().Be(RunOutcome.Finished, "4 End ran to the end and the run finished instead of pausing");
            engine.PausePending.Should().BeFalse();
        }

        [Test]
        public void Set_switch_before_the_hub_is_connected_is_reported_in_1_Begin() {
            var notKept = new PlannerOptions();
            notKept.KeepConnected.Remove("Switch");
            var setFirst = new[] { new StageEntry("SetSwitchValue", "Set Switch Value"), new StageEntry("ConnectAllEquipment", "Connect All Equipment") };
            StageChecks.Check(StageKind.Begin, setFirst, notKept).Should().Contain(w => w.Contains("before the switch hub is connected"));

            var hubFirst = new[] { new StageEntry("ConnectEquipment", "Connect Equipment", "Switch"), new StageEntry("SetSwitchValue", "Set Switch Value") };
            StageChecks.Check(StageKind.Begin, hubFirst, notKept).Should().NotContain(w => w.Contains("switch hub is connected"));
            StageChecks.Check(StageKind.Begin, setFirst, new PlannerOptions()).Should().NotContain(w => w.Contains("switch hub is connected"),
                "the hub is kept connected by default");
        }

        [Test]
        public void Exposure_type_is_saved_and_old_lists_with_a_gain_still_load() {
            var old = "{\"Name\":\"Old\",\"Targets\":[{\"Name\":\"M31\",\"Exposures\":[{\"Filter\":\"L\",\"ExposureTime\":300,\"Gain\":100,\"Binning\":\"1x1\",\"Count\":5,\"Done\":2}]}]}";
            var list = PlannerStore.Deserialize<TargetList>(old);
            var e = list.Targets[0].Exposures[0];
            e.Type.Should().Be(ExposureType.Light, "rows from before 1.1 are light frames");
            e.Done.Should().Be(2);

            e.Type = ExposureType.Bias;
            var json = PlannerStore.Serialize(list);
            json.Should().Contain("\"Type\": \"Bias\"").And.NotContain("Gain").And.NotContain("IsActive");
            PlannerStore.Deserialize<TargetList>(json).Targets[0].Exposures[0].Type.Should().Be(ExposureType.Bias);
            e.Clone(keepProgress: false).Type.Should().Be(ExposureType.Bias);

            // "Dark flat" is no longer offered: rows saved with it load as Dark
            var darkFlat = json.Replace("\"Type\": \"Bias\"", "\"Type\": \"DarkFlat\"");
            PlannerStore.Deserialize<TargetList>(darkFlat).Targets[0].Exposures[0].Type.Should().Be(ExposureType.Dark);
        }

        [TestCase(ExposureType.Light, "LIGHT")]
        [TestCase(ExposureType.Dark, "DARK")]
        [TestCase(ExposureType.Bias, "BIAS")]
        [TestCase(ExposureType.Flat, "FLAT")]
        public void Exposure_type_maps_to_NINAs_image_type(ExposureType type, string imageType) {
            InstructionFactory.ImageTypeOf(type).Should().Be(imageType);
        }

        [Test]
        public void Old_options_with_a_lead_time_still_load() {
            var o = PlannerStore.Deserialize<PlannerOptions>("{\"RunMode\":\"WithSafety\",\"GapMinutes\":45,\"GapLeadMinutes\":5}");
            o.GapMinutes.Should().Be(45);
        }

        [Test]
        public void Failed_instruction_that_skips_to_the_end_is_found_but_continue_on_error_is_not() {
            var stage = new SequentialContainer();
            var ok = new WaitForTimeSpan { Name = "Wait", Status = SequenceEntityStatus.FINISHED };
            var tolerated = new WaitForTimeSpan { Name = "Tolerated", Status = SequenceEntityStatus.FAILED, ErrorBehavior = InstructionErrorBehavior.ContinueOnError };
            stage.Add(ok);
            stage.Add(tolerated);
            NinaPlannerHardware.FindErrorStop(stage, "1 Begin", inEnd: false).Should().BeNull();

            var nested = new SequentialContainer();
            var power = new WaitForTimeSpan { Name = "Set Switch Value", Status = SequenceEntityStatus.FAILED, ErrorBehavior = InstructionErrorBehavior.SkipToSequenceEndInstructions };
            nested.Add(power);
            stage.Add(nested);
            var stop = NinaPlannerHardware.FindErrorStop(stage, "1 Begin", inEnd: false);
            stop.Should().NotBeNull();
            stop.Item.Should().Be("Set Switch Value");
            stop.BehaviorText.Should().Be("Skip to end of sequence instructions");

            power.ErrorBehavior = InstructionErrorBehavior.AbortOnError;
            NinaPlannerHardware.FindErrorStop(stage, "1 Begin", inEnd: false).BehaviorText.Should().Be("Abort sequence");
        }

        [Test]
        public void Autofocus_after_filter_change_can_start_from_the_last_light_filter() {
            static T M<T>() where T : class => new Mock<T> { DefaultValue = DefaultValue.Mock }.Object;
            var af = new AutofocusAfterFilterChange(M<IProfileService>(), M<IImageHistoryVM>(), M<ICameraMediator>(), M<IFilterWheelMediator>(), M<IFocuserMediator>(), M<IAutoFocusVMFactory>());
            PlannerImagingRun.SeedLastFilter(af, "Ha").Should().BeTrue();
            af.LastAutoFocusFilter.Should().Be("Ha");
        }
    }

    /// <summary>The service's frame bookkeeping: the ▶ row and the filter autofocus compares with.</summary>
    [TestFixture]
    public class FrameTrackingTests {
        private string root;

        [SetUp]
        public void SetUp() => root = Path.Combine(Path.GetTempPath(), "op-frames-" + Guid.NewGuid().ToString("N"));

        [TearDown]
        public void TearDown() { try { Directory.Delete(root, true); } catch (IOException) { } }

        [Test]
        public void The_row_being_imaged_is_marked_and_only_light_frames_set_the_autofocus_filter() {
            var planner = new PlannerService(null, root);
            var t = planner.NewTarget("M31");
            var ha = t.Exposures[0];
            ha.Filter = "Ha";
            var dark = new PlannerExposure { Filter = "OIII", Type = ExposureType.Dark };
            t.Exposures.Add(dark);

            planner.OnFrameStarting(t, ha);
            ha.IsActive.Should().BeTrue();
            planner.LastLightFilter.Should().Be("Ha");

            planner.OnFrameStarting(t, dark);
            ha.IsActive.Should().BeFalse();
            dark.IsActive.Should().BeTrue();
            planner.LastLightFilter.Should().Be("Ha", "a dark frame does not change the filter focus was checked for");

            planner.OnImagingEnded(t);
            t.Exposures.Should().OnlyContain(x => !x.IsActive);
        }
    }

    /// <summary>Target Settings: altitude and clock time follow each other.</summary>
    [TestFixture]
    public class LinkedConstraintTests {
        private string root;
        private PlannerService planner;

        [SetUp]
        public void SetUp() {
            root = Path.Combine(Path.GetTempPath(), "op-linked-" + Guid.NewGuid().ToString("N"));
            planner = new PlannerService(null, root);
        }

        [TearDown]
        public void TearDown() { try { Directory.Delete(root, true); } catch (IOException) { } }

        private TargetSettingsVM Make() {
            // NGC 7000: sets through 30° after midnight at 45.5° N in October
            var t = planner.NewTarget("NGC 7000");
            t.RaHours = 20.98;
            t.DecDegrees = 44.3;
            t.End = new TimeConstraint { Enabled = true, By = ConstraintBy.Altitude, Altitude = 30 };
            return new TargetSettingsVM(planner, t, isNew: false, NightPlan.For(TestData.Site, TestData.Night(15), TestData.Utc));
        }

        [Test]
        public void Editing_one_value_updates_the_other_at_once_and_keeps_the_lock() {
            var vm = Make();
            var row = vm.End;
            row.ByAltitude.Should().BeTrue();

            row.TimeText = "01:00";
            row.ByAltitude.Should().BeTrue("editing does not move the lock, as in SGP");
            row.Constraint.Time.Should().Be(new TimeSpan(1, 0, 0));
            var at1 = NightTime.At(new TimeSpan(1, 0, 0), vm.Night.Now);
            double.Parse(row.AltitudeText).Should().BeApproximately(vm.Night.Altitude(20.98, 44.3, at1), 0.06);

            row.AltitudeText = "40";
            row.Constraint.Altitude.Should().Be(40);
            var t40 = vm.Night.TimeAtAltitude(20.98, 44.3, 40, rising: false).Value;
            row.TimeText.Should().Be(ConstraintRow.FormatTime(new TimeSpan(t40.Hour, t40.Minute, 0)), "the time is shown to the minute");
        }

        [Test]
        public void One_lock_switches_which_value_stays_constant() {
            var vm = Make();
            var row = vm.End;
            row.TimeLocked.Should().BeFalse();
            row.LockTip.Should().StartWith("Altitude locked");
            row.ToggleLock.Execute(null);
            row.ByAltitude.Should().BeFalse();
            row.TimeLocked.Should().BeTrue();
            row.LockTip.Should().StartWith("Time locked");
            new TimeConstraint().By.Should().Be(ConstraintBy.Time, "a new constraint is time-locked, as in SGP");
        }

        [Test]
        public void Spinners_and_the_day() {
            var vm = Make();
            var row = vm.End;
            row.ToggleLock.Execute(null);
            row.TimeText = "11:30 PM";
            row.Constraint.Time.Should().Be(new TimeSpan(23, 30, 0), "the Windows (12 h) format is read");
            row.Note.Should().Be("today");
            row.TimeUp.Execute(null);
            row.Constraint.Time.Should().Be(new TimeSpan(23, 31, 0));
            row.TimeText = "00:10";
            row.Note.Should().Be("tomorrow", "after midnight belongs to the same night");
            row.TimeDown.Execute(null);
            row.Constraint.Time.Should().Be(new TimeSpan(0, 9, 0));
            row.AltitudeUp.Execute(null);
            row.ByAltitude.Should().BeFalse("the spinner does not move the lock either");
            row.Constraint.Altitude.Should().Be(Math.Round(row.Constraint.Altitude));
        }

        [Test]
        public void An_altitude_never_reached_breaks_the_link_and_only_the_time_is_used() {
            var vm = Make();
            var row = vm.End;
            row.AltitudeText = "89";
            row.Broken.Should().BeTrue();
            row.LinkIcon.Should().Be("⛓");
            row.Note.Should().Contain("only the time is used");
            row.Result().By.Should().Be(ConstraintBy.Time);
            row.AltitudeText = "30";
            row.Broken.Should().BeFalse();
            row.Result().By.Should().Be(ConstraintBy.Altitude);
        }

        [Test]
        public void Changing_the_coordinates_recalculates_the_unlocked_value() {
            var vm = Make();
            var before = vm.End.TimeText;
            vm.RaText = "22h00m00s";
            vm.End.AltitudeText.Should().Be("30", "the altitude is locked");
            vm.End.TimeText.Should().NotBe(before, "the target sets through 30° about an hour later");
        }
    }
}
